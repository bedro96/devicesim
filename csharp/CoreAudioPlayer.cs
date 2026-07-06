// =============================================================
// devicesim/csharp/CoreAudioPlayer.cs
// CoreAudio(AudioToolbox AudioQueue) P/Invoke 기반 PCM16 모노 재생 (macOS)
//
// 대상: macOS 개발 환경에서 실제 스피커 출력.
// Linux=ALSA, Windows=WASAPI 와 동일하게, macOS 에서도 실제 하드웨어로
// 재생해야 한다는 요구사항에 따라 CoreAudio 를 직접 P/Invoke 한다.
//
// 설계는 ALSA 백엔드와 대칭:
//   - Enqueue: PCM16/24kHz/모노 바이트를 재생 큐에 넣는다.
//   - 재생 스레드: 큐 → AudioQueue 버퍼로 채워 재생.
//   - Clear:   audio_clear/바지인 시 큐를 비우고 재생 중인 버퍼를 즉시 리셋.
//
// AudioQueue 는 콜백 기반이다: 버퍼 재생이 끝나면 콜백이 해당 버퍼를
// 되돌려주고, 우리는 그 버퍼를 재사용(free pool)한다. 부트스트랩 없이
// 시작할 수 있도록, 미리 할당한 버퍼 풀을 free 큐에 넣고 재생 루프가
// 데이터가 생기는 대로 버퍼를 채워 enqueue 한다.
// =============================================================

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DeviceSim;

/// <summary>
/// CoreAudio(AudioToolbox AudioQueue) 재생 백엔드. macOS 전용.
/// PCM16/24kHz/모노 다운링크 오디오를 실제 스피커로 출력한다.
/// </summary>
internal sealed class CoreAudioPlayer : IAudioPlayer
{
    // -------------------------------------------------------
    // AudioToolbox 프레임워크 P/Invoke
    // -------------------------------------------------------
    private const string AudioToolbox =
        "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    // 'lpcm' (kAudioFormatLinearPCM), 리틀엔디안 부호있는 정수 + packed
    private const uint kAudioFormatLinearPCM               = 0x6C70636D;
    private const uint kLinearPCMFormatFlagIsSignedInteger = 0x4;
    private const uint kLinearPCMFormatFlagIsPacked        = 0x8;

    /// <summary>CoreAudio 오디오 스트림 포맷 서술자 (ASBD).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double mSampleRate;
        public uint   mFormatID;
        public uint   mFormatFlags;
        public uint   mBytesPerPacket;
        public uint   mFramesPerPacket;
        public uint   mBytesPerFrame;
        public uint   mChannelsPerFrame;
        public uint   mBitsPerChannel;
        public uint   mReserved;
    }

    /// <summary>
    /// AudioQueueBuffer 네이티브 레이아웃(64-bit). 콜백에서 버퍼 포인터로
    /// mAudioData/용량/바이트수 필드에 접근하기 위한 오프셋 계산에 사용한다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint   mAudioDataBytesCapacity;
        public IntPtr mAudioData;
        public uint   mAudioDataByteSize;
        public IntPtr mUserData;
        public uint   mPacketDescriptionCapacity;
        public IntPtr mPacketDescriptions;
        public uint   mPacketDescriptionCount;
    }

    // AudioQueueOutputCallback: 버퍼 재생이 끝나면 호출된다.
    private delegate void AudioQueueOutputCallback(IntPtr inUserData, IntPtr inAQ, IntPtr inBuffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription inFormat,
        AudioQueueOutputCallback inCallbackProc,
        IntPtr inUserData,
        IntPtr inCallbackRunLoop,
        IntPtr inCallbackRunLoopMode,
        uint inFlags,
        out IntPtr outAQ);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueAllocateBuffer(IntPtr inAQ, uint inBufferByteSize, out IntPtr outBuffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr inAQ, IntPtr inBuffer, uint inNumPacketDescs, IntPtr inPacketDescs);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStart(IntPtr inAQ, IntPtr inStartTime);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStop(IntPtr inAQ, bool inImmediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueReset(IntPtr inAQ);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueDispose(IntPtr inAQ, bool inImmediate);

    // -------------------------------------------------------
    // 버퍼 풀 파라미터
    // -------------------------------------------------------
    // 4096바이트 = 2048샘플 ≈ 85ms(24kHz 모노). 8개 → 약 680ms 버퍼링.
    private const int BufferBytes = 4096;
    private const int BufferCount = 8;

    // 콜백에서 계산 없이 쓰도록 미리 필드 오프셋을 구해 둔다.
    private static readonly int CapacityOffset =
        (int)Marshal.OffsetOf<AudioQueueBuffer>(nameof(AudioQueueBuffer.mAudioDataBytesCapacity));
    private static readonly int DataPtrOffset =
        (int)Marshal.OffsetOf<AudioQueueBuffer>(nameof(AudioQueueBuffer.mAudioData));
    private static readonly int ByteSizeOffset =
        (int)Marshal.OffsetOf<AudioQueueBuffer>(nameof(AudioQueueBuffer.mAudioDataByteSize));

    // -------------------------------------------------------
    // 상태
    // -------------------------------------------------------
    private IntPtr _queue = IntPtr.Zero;
    private readonly ConcurrentQueue<byte[]> _dataQueue    = new();
    private readonly ConcurrentQueue<IntPtr> _freeBuffers  = new();
    private readonly CancellationTokenSource _cts          = new();
    // 콜백 델리게이트를 GC 로부터 보호 (native 가 참조하는 동안 살아있어야 함)
    private readonly AudioQueueOutputCallback _outputCallback;
    private readonly bool _silentMode;
    private byte[] _leftover = Array.Empty<byte>();

    // -------------------------------------------------------
    // 초기화
    // -------------------------------------------------------
    /// <summary>
    /// CoreAudio 재생 큐를 초기화한다. 실패 시 예외를 던져 상위(AudioPlayer)가
    /// 무음 폴백하도록 한다.
    /// </summary>
    public CoreAudioPlayer()
    {
        _outputCallback = OnBufferConsumed;

        var format = new AudioStreamBasicDescription
        {
            mSampleRate       = AudioPlayer.SampleRate,
            mFormatID         = kAudioFormatLinearPCM,
            mFormatFlags      = kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked,
            mBytesPerPacket   = 2,   // PCM16 모노 = 프레임당 2바이트
            mFramesPerPacket  = 1,
            mBytesPerFrame    = 2,
            mChannelsPerFrame = 1,   // 다운링크 항상 모노
            mBitsPerChannel   = 16,
            mReserved         = 0,
        };

        int st = AudioQueueNewOutput(
            ref format, _outputCallback,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out _queue);
        if (st != 0 || _queue == IntPtr.Zero)
        {
            _silentMode = true;
            throw new InvalidOperationException($"AudioQueueNewOutput 실패: OSStatus={st}");
        }

        // 버퍼 풀 할당 후 free 큐에 등록
        for (int i = 0; i < BufferCount; i++)
        {
            if (AudioQueueAllocateBuffer(_queue, BufferBytes, out IntPtr buf) != 0)
            {
                _silentMode = true;
                AudioQueueDispose(_queue, true);
                _queue = IntPtr.Zero;
                throw new InvalidOperationException("AudioQueueAllocateBuffer 실패");
            }
            _freeBuffers.Enqueue(buf);
        }

        int startSt = AudioQueueStart(_queue, IntPtr.Zero);
        if (startSt != 0)
        {
            _silentMode = true;
            AudioQueueDispose(_queue, true);
            _queue = IntPtr.Zero;
            throw new InvalidOperationException($"AudioQueueStart 실패: OSStatus={startSt}");
        }

        _silentMode = false;
        Console.WriteLine("[CoreAudio 재생] 초기화 완료 PCM16/24kHz/모노 (AudioQueue)");

        Task.Run(() => PlaybackLoop(_cts.Token));
    }

    // -------------------------------------------------------
    // 콜백: 버퍼 재생 완료 → free 풀로 반환
    // -------------------------------------------------------
    private void OnBufferConsumed(IntPtr inUserData, IntPtr inAQ, IntPtr inBuffer)
    {
        if (!_cts.IsCancellationRequested)
            _freeBuffers.Enqueue(inBuffer);
    }

    // -------------------------------------------------------
    // 재생 루프: 데이터 큐 → 빈 버퍼 채워 enqueue
    // -------------------------------------------------------
    private void PlaybackLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _queue != IntPtr.Zero)
        {
            if (!_freeBuffers.TryDequeue(out IntPtr bufPtr))
            {
                // 재사용 가능한 버퍼가 없음 (모두 재생 대기 중) → 잠깐 대기
                Thread.Sleep(1);
                continue;
            }

            int capacity = Marshal.ReadInt32(bufPtr, CapacityOffset);
            byte[] chunk = TakeUpTo(capacity, ct);
            if (chunk.Length == 0)
            {
                // 채울 데이터가 없으면 버퍼를 되돌리고 대기
                _freeBuffers.Enqueue(bufPtr);
                Thread.Sleep(1);
                continue;
            }

            IntPtr dataPtr = Marshal.ReadIntPtr(bufPtr, DataPtrOffset);
            Marshal.Copy(chunk, 0, dataPtr, chunk.Length);
            Marshal.WriteInt32(bufPtr, ByteSizeOffset, chunk.Length);

            if (AudioQueueEnqueueBuffer(_queue, bufPtr, 0, IntPtr.Zero) != 0)
            {
                // enqueue 실패 시 버퍼 반환 후 재시도
                _freeBuffers.Enqueue(bufPtr);
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>
    /// leftover + 데이터 큐에서 최대 max 바이트를 모아 반환한다.
    /// 블로킹하지 않으며(짧게 폴링), 조금이라도 데이터가 있으면 그만큼 반환한다.
    /// max 를 넘는 나머지는 _leftover 에 보관한다.
    /// </summary>
    private byte[] TakeUpTo(int max, CancellationToken ct)
    {
        // 큐가 비어 있고 leftover 도 없으면 짧게 한 번 더 살펴본다.
        if (_leftover.Length == 0 && _dataQueue.IsEmpty)
            return Array.Empty<byte>();

        var acc = new List<byte>(max);
        if (_leftover.Length > 0)
        {
            int take = Math.Min(max, _leftover.Length);
            acc.AddRange(_leftover.AsSpan(0, take).ToArray());
            _leftover = _leftover.Length > take
                ? _leftover.AsSpan(take).ToArray()
                : Array.Empty<byte>();
        }

        while (acc.Count < max && _dataQueue.TryDequeue(out var next))
        {
            int room = max - acc.Count;
            if (next.Length <= room)
            {
                acc.AddRange(next);
            }
            else
            {
                acc.AddRange(next.AsSpan(0, room).ToArray());
                _leftover = next.AsSpan(room).ToArray();
                break;
            }
        }
        return acc.ToArray();
    }

    // -------------------------------------------------------
    // IAudioPlayer 구현
    // -------------------------------------------------------
    /// <inheritdoc/>
    public void Enqueue(byte[] pcm16Mono)
    {
        if (_silentMode)
        {
            Console.WriteLine($"[CoreAudio 재생] 무음 모드 – {pcm16Mono.Length} bytes 수신 (재생 생략)");
            return;
        }
        _dataQueue.Enqueue(pcm16Mono);
    }

    /// <inheritdoc/>
    public void Clear()
    {
        // 대기 중인 데이터를 비우고, 재생 중인 버퍼를 즉시 리셋한다 (바지인).
        while (_dataQueue.TryDequeue(out _)) { }
        _leftover = Array.Empty<byte>();
        if (_queue != IntPtr.Zero)
            AudioQueueReset(_queue);   // enqueue 된 버퍼는 콜백으로 free 풀에 반환됨
        Console.WriteLine("[CoreAudio 재생] 큐 클리어 (audio_clear 수신)");
    }

    /// <inheritdoc/>
    // 재생 중 판정: 대기 데이터/leftover 가 있거나, enqueue 된 버퍼가 아직
    // 재생 대기 중(= free 버퍼 수가 전체보다 적음)이면 재생 중으로 본다.
    public bool IsPlaying =>
        !_silentMode &&
        (!_dataQueue.IsEmpty || _leftover.Length > 0 || _freeBuffers.Count < BufferCount);

    /// <inheritdoc/>
    public void Dispose()
    {
        _cts.Cancel();
        if (_queue != IntPtr.Zero)
        {
            AudioQueueStop(_queue, true);
            AudioQueueDispose(_queue, true);
            _queue = IntPtr.Zero;
        }
        _cts.Dispose();
    }
}

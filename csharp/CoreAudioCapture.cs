// =============================================================
// devicesim/csharp/CoreAudioCapture.cs
// CoreAudio(AudioToolbox AudioQueue) P/Invoke 기반 PCM16 마이크 캡처 (macOS)
//
// 대상: macOS 개발 환경에서 실제 마이크 입력.
// Linux=ALSA, Windows=WASAPI 와 대칭으로, macOS 에서도 실제 하드웨어
// 마이크로 캡처해야 한다는 요구사항에 따라 CoreAudio 를 직접 P/Invoke 한다.
//
// 채널 우선순위: 2채널(MEMS 어레이 에뮬레이션) 시도 → 실패 시 모노 폴백.
// AudioQueue 입력은 콜백 기반: 버퍼가 오디오로 채워지면 콜백이 호출되고,
// 우리는 그 바이트를 onChunk 로 전달한 뒤 버퍼를 다시 enqueue 한다.
// =============================================================

using System.Runtime.InteropServices;

namespace DeviceSim;

/// <summary>
/// CoreAudio(AudioToolbox AudioQueue) 캡처 백엔드. macOS 전용.
/// PCM16/24kHz 마이크 입력을 캡처한다 (2채널 시도 후 모노 폴백).
/// </summary>
internal sealed class CoreAudioCapture : IAudioCapture
{
    // -------------------------------------------------------
    // AudioToolbox 프레임워크 P/Invoke
    // -------------------------------------------------------
    private const string AudioToolbox =
        "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    private const uint kAudioFormatLinearPCM               = 0x6C70636D; // 'lpcm'
    private const uint kLinearPCMFormatFlagIsSignedInteger = 0x4;
    private const uint kLinearPCMFormatFlagIsPacked        = 0x8;

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

    // AudioQueueInputCallback: 버퍼가 캡처 오디오로 채워지면 호출된다.
    private delegate void AudioQueueInputCallback(
        IntPtr inUserData, IntPtr inAQ, IntPtr inBuffer,
        IntPtr inStartTime, uint inNumberPacketDescriptions, IntPtr inPacketDescs);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewInput(
        ref AudioStreamBasicDescription inFormat,
        AudioQueueInputCallback inCallbackProc,
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
    private static extern int AudioQueueDispose(IntPtr inAQ, bool inImmediate);

    // AudioQueueBuffer 필드 오프셋 (콜백에서 데이터 포인터/바이트수 접근용)
    private static readonly int DataPtrOffset =
        (int)Marshal.OffsetOf<AudioQueueBuffer>(nameof(AudioQueueBuffer.mAudioData));
    private static readonly int ByteSizeOffset =
        (int)Marshal.OffsetOf<AudioQueueBuffer>(nameof(AudioQueueBuffer.mAudioDataByteSize));

    private const int BufferCount = 3;

    // -------------------------------------------------------
    // 상태
    // -------------------------------------------------------
    private IntPtr _queue = IntPtr.Zero;
    // 콜백 델리게이트를 GC 로부터 보호 (native 가 참조하는 동안 살아있어야 함)
    private AudioQueueInputCallback? _inputCallback;
    private Action<byte[]>? _onChunk;
    private CancellationToken _ct;

    /// <inheritdoc/>
    public CaptureMode Mode { get; private set; } = CaptureMode.Synthetic;

    // -------------------------------------------------------
    // 초기화: 2ch 시도 → 모노 폴백
    // -------------------------------------------------------
    /// <summary>
    /// CoreAudio 캡처 큐를 초기화한다.
    /// 장치 열기 실패 시 <see cref="CaptureMode.Synthetic"/> 으로 남긴다.
    /// </summary>
    /// <param name="maxChannels">1=모노 강제, 2=2채널 시도</param>
    public CoreAudioCapture(int maxChannels = 2)
    {
        if (maxChannels >= 2 && TryOpen(2))
        {
            Mode      = CaptureMode.Stereo;
            Console.WriteLine("[CoreAudio 캡처] 2채널 PCM16/24kHz (AudioQueue)");
            return;
        }

        if (TryOpen(1))
        {
            Mode      = CaptureMode.Mono;
            Console.WriteLine("[CoreAudio 캡처] 모노 폴백 PCM16/24kHz (AudioQueue)");
            return;
        }

        Console.Error.WriteLine("[CoreAudio 캡처] 장치 열기 실패 → 합성 폴백 사용");
        Mode = CaptureMode.Synthetic;
    }

    // -------------------------------------------------------
    // AudioQueue 입력 큐 열기 시도
    // -------------------------------------------------------
    private bool TryOpen(int channels)
    {
        var format = new AudioStreamBasicDescription
        {
            mSampleRate       = AudioCapture.SampleRate,
            mFormatID         = kAudioFormatLinearPCM,
            mFormatFlags      = kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked,
            mBytesPerPacket   = (uint)(channels * 2),
            mFramesPerPacket  = 1,
            mBytesPerFrame    = (uint)(channels * 2),
            mChannelsPerFrame = (uint)channels,
            mBitsPerChannel   = 16,
            mReserved         = 0,
        };

        _inputCallback = OnBufferFilled;
        int st = AudioQueueNewInput(
            ref format, _inputCallback,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out IntPtr q);
        if (st != 0 || q == IntPtr.Zero)
        {
            _inputCallback = null;
            return false;
        }

        // 입력 버퍼 풀 할당 및 프라이밍(미리 enqueue)
        int bytesPerFrame  = channels * 2;
        int framesPerChunk = AudioCapture.SamplesPerChunk;
        uint bufBytes      = (uint)(framesPerChunk * bytesPerFrame);
        for (int i = 0; i < BufferCount; i++)
        {
            if (AudioQueueAllocateBuffer(q, bufBytes, out IntPtr buf) != 0 ||
                AudioQueueEnqueueBuffer(q, buf, 0, IntPtr.Zero) != 0)
            {
                AudioQueueDispose(q, true);
                _inputCallback = null;
                return false;
            }
        }

        _queue = q;
        return true;
    }

    // -------------------------------------------------------
    // 입력 콜백: 채워진 버퍼 → onChunk → 재-enqueue
    // -------------------------------------------------------
    private void OnBufferFilled(
        IntPtr inUserData, IntPtr inAQ, IntPtr inBuffer,
        IntPtr inStartTime, uint inNumberPacketDescriptions, IntPtr inPacketDescs)
    {
        if (_ct.IsCancellationRequested || _queue == IntPtr.Zero)
            return;

        int byteSize = Marshal.ReadInt32(inBuffer, ByteSizeOffset);
        if (byteSize > 0 && _onChunk is not null)
        {
            IntPtr dataPtr = Marshal.ReadIntPtr(inBuffer, DataPtrOffset);
            var chunk = new byte[byteSize];
            Marshal.Copy(dataPtr, chunk, 0, byteSize);
            _onChunk(chunk);
        }

        // 버퍼를 다시 큐에 넣어 계속 캡처
        AudioQueueEnqueueBuffer(inAQ, inBuffer, 0, IntPtr.Zero);
    }

    // -------------------------------------------------------
    // 캡처 시작
    // -------------------------------------------------------
    /// <inheritdoc/>
    public void Start(Action<byte[]> onChunk, CancellationToken ct)
    {
        if (_queue == IntPtr.Zero)
            throw new InvalidOperationException("CoreAudio 캡처 큐가 열려 있지 않습니다.");

        _onChunk = onChunk;
        _ct      = ct;

        int st = AudioQueueStart(_queue, IntPtr.Zero);
        if (st != 0)
            throw new InvalidOperationException($"AudioQueueStart(입력) 실패: OSStatus={st}");
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_queue != IntPtr.Zero)
            AudioQueueStop(_queue, true);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        if (_queue != IntPtr.Zero)
        {
            AudioQueueDispose(_queue, true);
            _queue = IntPtr.Zero;
        }
    }
}

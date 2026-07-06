// =============================================================
// devicesim/csharp/AlsaPlayer.cs
// ALSA P/Invoke 기반 PCM16 모노 오디오 재생 (Linux/ARM 타겟)
//
// 대상: ARM Cortex-A53 스피커 출력 (Linux/ALSA)
// libasound.so.2 가 설치된 모든 Linux 환경에서 동작합니다.
// =============================================================

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DeviceSim;

/// <summary>
/// ALSA libasound P/Invoke 재생 백엔드.
/// Linux/ARM 환경에서만 동작합니다. Windows 에서는 생성하지 마십시오.
/// </summary>
internal sealed class AlsaPlayer : IAudioPlayer
{
    // -------------------------------------------------------
    // ALSA 상수
    // -------------------------------------------------------
    private const int SND_PCM_STREAM_PLAYBACK     = 0;
    private const int SND_PCM_FORMAT_S16_LE       = 2;
    private const int SND_PCM_ACCESS_RW_INTERLEAVED = 3;
    private const string AlsaLib                  = "libasound.so.2";

    [DllImport(AlsaLib, CharSet = CharSet.Ansi)]
    private static extern int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);

    [DllImport(AlsaLib)]
    private static extern int snd_pcm_close(IntPtr pcm);

    [DllImport(AlsaLib)]
    private static extern int snd_pcm_set_params(
        IntPtr pcm, int format, int access,
        uint channels, uint rate, int soft_resample, uint latency_us);

    [DllImport(AlsaLib)]
    private static extern nint snd_pcm_writei(IntPtr pcm, byte[] buf, nint frames);

    [DllImport(AlsaLib)]
    private static extern int snd_pcm_recover(IntPtr pcm, int err, int silent);

    [DllImport(AlsaLib)]
    private static extern int snd_pcm_drain(IntPtr pcm);

    // -------------------------------------------------------
    // 상태
    // -------------------------------------------------------
    private IntPtr _pcm = IntPtr.Zero;
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly CancellationTokenSource _cts   = new();
    private readonly bool _silentMode;

    // -------------------------------------------------------
    // 초기화
    // -------------------------------------------------------
    /// <summary>
    /// ALSA 재생 장치를 초기화합니다. 실패 시 무음 모드.
    /// </summary>
    public AlsaPlayer(string deviceName = "default")
    {
        if (snd_pcm_open(out _pcm, deviceName, SND_PCM_STREAM_PLAYBACK, 0) < 0)
        {
            Console.Error.WriteLine($"[ALSA 재생] 장치 열기 실패: {deviceName} → 무음 모드");
            _silentMode = true;
            return;
        }

        int ret = snd_pcm_set_params(
            _pcm,
            SND_PCM_FORMAT_S16_LE,
            SND_PCM_ACCESS_RW_INTERLEAVED,
            channels:       1,              // 다운링크 항상 모노
            rate:           AudioPlayer.SampleRate,
            soft_resample:  1,
            latency_us:     20_000u);       // 20 ms

        if (ret < 0)
        {
            Console.Error.WriteLine($"[ALSA 재생] 파라미터 설정 실패: {ret} → 무음 모드");
            snd_pcm_close(_pcm);
            _pcm = IntPtr.Zero;
            _silentMode = true;
            return;
        }

        _silentMode = false;
        Console.WriteLine($"[ALSA 재생] 초기화 완료 PCM16/24kHz/모노 ({deviceName})");

        // 재생 루프 시작
        Task.Run(() => PlaybackLoop(_cts.Token));
    }

    // -------------------------------------------------------
    // 재생 루프 (큐 → ALSA writei)
    // -------------------------------------------------------
    private void PlaybackLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _pcm != IntPtr.Zero)
        {
            if (!_queue.TryDequeue(out var data))
            {
                Thread.Sleep(1);
                continue;
            }

            int bytesPerFrame = 2; // PCM16 모노
            int totalFrames   = data.Length / bytesPerFrame;
            int written       = 0;

            while (written < totalFrames && !ct.IsCancellationRequested)
            {
                int remaining = totalFrames - written;
                var segment   = new byte[remaining * bytesPerFrame];
                Buffer.BlockCopy(data, written * bytesPerFrame, segment, 0, segment.Length);

                nint frames = snd_pcm_writei(_pcm, segment, remaining);
                if (frames < 0)
                {
                    int err = snd_pcm_recover(_pcm, (int)frames, silent: 0);
                    if (err < 0)
                    {
                        Console.Error.WriteLine($"[ALSA 재생] 복구 실패: {err}");
                        return;
                    }
                    continue;
                }
                written += (int)frames;
            }
        }
    }

    // -------------------------------------------------------
    // IAudioPlayer 구현
    // -------------------------------------------------------
    /// <inheritdoc/>
    public void Enqueue(byte[] pcm16Mono)
    {
        if (_silentMode)
        {
            Console.WriteLine($"[ALSA 재생] 무음 모드 – {pcm16Mono.Length} bytes 수신 (재생 생략)");
            return;
        }
        _queue.Enqueue(pcm16Mono);
    }

    /// <inheritdoc/>
    public void Clear()
    {
        // 큐를 비워 재생 중인 오디오를 즉시 중단합니다 (audio_clear / 바지인 처리)
        while (_queue.TryDequeue(out _)) { }
        Console.WriteLine("[ALSA 재생] 큐 클리어 (audio_clear 수신)");
    }

    /// <inheritdoc/>
    // 재생 중 판정: 대기 큐에 데이터가 남아 있으면 재생 중으로 본다.
    // (현재 writei 중인 마지막 청크는 EchoGate hangover 로 흡수)
    public bool IsPlaying => !_silentMode && !_queue.IsEmpty;

    /// <inheritdoc/>
    public void Dispose()
    {
        _cts.Cancel();
        if (_pcm != IntPtr.Zero)
        {
            snd_pcm_drain(_pcm);
            snd_pcm_close(_pcm);
            _pcm = IntPtr.Zero;
        }
        _cts.Dispose();
    }
}

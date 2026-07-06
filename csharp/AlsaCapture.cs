// =============================================================
// devicesim/csharp/AlsaCapture.cs
// ALSA P/Invoke 기반 PCM16 마이크 캡처 (Linux/ARM 타겟)
//
// 대상: ARM Cortex-A53 + 2× MEMS 마이크 어레이 (Linux/ALSA)
// libasound.so.2 가 설치된 모든 Linux 환경에서 동작합니다.
//   Debian/Ubuntu: sudo apt install libasound2-dev
//   Buildroot/Yocto: alsa-lib 패키지
//
// ARM 포팅 메모:
//   - 기본 장치 이름: "default" (또는 "hw:0,0" 직접 지정)
//   - 24 kHz, PCM16, 2ch MEMS 어레이 캡처
//   - snd_pcm_set_params() – 단순 ALSA API (hw_params 체인 없음)
// =============================================================

using System.Runtime.InteropServices;

namespace DeviceSim;

/// <summary>
/// ALSA libasound P/Invoke 캡처 백엔드.
/// Linux/ARM 환경에서만 동작합니다. Windows 에서는 생성하지 마십시오.
/// </summary>
internal sealed class AlsaCapture : IAudioCapture
{
    // -------------------------------------------------------
    // ALSA 상수 (asoundlib.h)
    // -------------------------------------------------------
    private const int SND_PCM_STREAM_CAPTURE      = 1;
    private const int SND_PCM_FORMAT_S16_LE       = 2;
    private const int SND_PCM_ACCESS_RW_INTERLEAVED = 3;
    private const string AlsaLib                  = "libasound.so.2";

    // -------------------------------------------------------
    // ALSA P/Invoke 선언
    // -------------------------------------------------------
    [DllImport(AlsaLib, CharSet = CharSet.Ansi)]
    private static extern int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);

    [DllImport(AlsaLib)]
    private static extern int snd_pcm_close(IntPtr pcm);

    // 단순 파라미터 설정 API:
    //   format, access, channels, rate, soft_resample, latency(us)
    [DllImport(AlsaLib)]
    private static extern int snd_pcm_set_params(
        IntPtr pcm, int format, int access,
        uint channels, uint rate, int soft_resample, uint latency_us);

    // frames: 읽을 프레임 수; 반환값 = 읽은 프레임 수 (음수 = 오류)
    [DllImport(AlsaLib)]
    private static extern nint snd_pcm_readi(IntPtr pcm, byte[] buf, nint frames);

    // 오류 복구 (EPIPE, ESTRPIPE 등)
    [DllImport(AlsaLib)]
    private static extern int snd_pcm_recover(IntPtr pcm, int err, int silent);

    [DllImport(AlsaLib)]
    private static extern int snd_pcm_prepare(IntPtr pcm);

    // -------------------------------------------------------
    // 상태
    // -------------------------------------------------------
    private IntPtr _pcm = IntPtr.Zero;
    private int    _channels;

    /// <inheritdoc/>
    public CaptureMode Mode { get; private set; } = CaptureMode.Synthetic;

    // -------------------------------------------------------
    // 초기화: 2ch 시도 → 모노 폴백 → 실패(합성 폴백은 호출자 처리)
    // deviceName: ALSA 장치 이름 (기본값 "default")
    // maxChannels: 1=모노 강제, 2=2ch 시도
    // -------------------------------------------------------
    /// <summary>
    /// ALSA 캡처 장치를 초기화합니다.
    /// 장치 열기 실패 시 <see cref="CaptureMode.Synthetic"/> 으로 남습니다.
    /// </summary>
    public AlsaCapture(string deviceName = "default", int maxChannels = 2)
    {
        if (maxChannels >= 2 && TryOpenDevice(deviceName, 2))
        {
            Mode      = CaptureMode.Stereo;
            _channels = 2;
            Console.WriteLine($"[ALSA 캡처] 2채널 PCM16/24kHz ({deviceName})");
            return;
        }

        if (TryOpenDevice(deviceName, 1))
        {
            Mode      = CaptureMode.Mono;
            _channels = 1;
            Console.WriteLine($"[ALSA 캡처] 모노 폴백 PCM16/24kHz ({deviceName})");
            return;
        }

        Console.Error.WriteLine($"[ALSA 캡처] 장치 열기 실패: {deviceName} → 합성 폴백 사용");
        Mode = CaptureMode.Synthetic;
    }

    // -------------------------------------------------------
    // ALSA 장치 열기 시도
    // -------------------------------------------------------
    private bool TryOpenDevice(string deviceName, int channels)
    {
        if (snd_pcm_open(out var pcm, deviceName, SND_PCM_STREAM_CAPTURE, 0) < 0)
            return false;

        // 20 ms 레이턴시 (20_000 µs)
        int ret = snd_pcm_set_params(
            pcm,
            SND_PCM_FORMAT_S16_LE,
            SND_PCM_ACCESS_RW_INTERLEAVED,
            (uint)channels,
            AudioCapture.SampleRate,
            soft_resample: 1,
            latency_us: 20_000u);

        if (ret < 0)
        {
            snd_pcm_close(pcm);
            return false;
        }

        _pcm = pcm;
        return true;
    }

    // -------------------------------------------------------
    // 캡처 시작 루프 (백그라운드 태스크)
    // -------------------------------------------------------
    /// <inheritdoc/>
    public void Start(Action<byte[]> onChunk, CancellationToken ct)
    {
        if (_pcm == IntPtr.Zero)
            throw new InvalidOperationException("ALSA 장치가 열려 있지 않습니다.");

        int bytesPerFrame = _channels * 2; // PCM16 = 2 bytes per sample
        int framesPerChunk = AudioCapture.SamplesPerChunk;
        var buf = new byte[framesPerChunk * bytesPerFrame];

        Task.Run(() =>
        {
            snd_pcm_prepare(_pcm);

            while (!ct.IsCancellationRequested && _pcm != IntPtr.Zero)
            {
                nint frames = snd_pcm_readi(_pcm, buf, framesPerChunk);
                if (frames < 0)
                {
                    // ALSA 오버런/언더런 복구 시도
                    int err = snd_pcm_recover(_pcm, (int)frames, silent: 0);
                    if (err < 0)
                    {
                        Console.Error.WriteLine($"[ALSA 캡처] 복구 실패: {err}");
                        break;
                    }
                    continue;
                }

                if (frames == 0) continue;

                int bytes = (int)frames * bytesPerFrame;
                var chunk = new byte[bytes];
                Buffer.BlockCopy(buf, 0, chunk, 0, bytes);
                onChunk(chunk);
            }
        }, ct);
    }

    /// <inheritdoc/>
    public void Stop() { /* 캡처 루프는 CT 취소로 종료됨 */ }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        if (_pcm != IntPtr.Zero)
        {
            snd_pcm_close(_pcm);
            _pcm = IntPtr.Zero;
        }
    }
}

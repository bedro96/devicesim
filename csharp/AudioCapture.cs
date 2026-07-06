// =============================================================
// devicesim/csharp/AudioCapture.cs
// PCM16 마이크 캡처 – 장치 백엔드 (플랫폼 자동 선택)
//
// 실제 대상 하드웨어: ARM Cortex-A53 + 2× MEMS 마이크 (Linux/ALSA)
// 개발 환경: Windows WASAPI (NAudio WaveInEvent)
// CI/하드웨어 없는 환경: 440 Hz 합성 사인파
//
// 채널 우선순위:
//   1. Linux:   2채널 ALSA 캡처 시도 (libasound.so.2)
//               실패 시 모노 ALSA → 합성 폴백
//   2. Windows: 2채널 WASAPI 캡처 시도 (NAudio WaveInEvent)
//               실패 시 모노 WASAPI → 합성 폴백
//   3. 기타 OS: 합성 사인파 (CI/테스트)
//
// --audio-source <file> 지정 시: FileCapture 를 사용하세요.
// =============================================================

using System.Runtime.InteropServices;

namespace DeviceSim;

/// <summary>
/// 캡처 모드 열거형.
/// </summary>
public enum CaptureMode
{
    /// <summary>2채널 MEMS 어레이 (실제 하드웨어 또는 에뮬레이션)</summary>
    Stereo,
    /// <summary>단일 채널 폴백</summary>
    Mono,
    /// <summary>하드웨어 없음 – 합성 사인파 (CI/테스트)</summary>
    Synthetic,
    /// <summary>파일 소스 – PCM/WAV 파일에서 읽기</summary>
    File,
}

/// <summary>
/// PCM16 오디오 캡처기 – 장치 백엔드.
/// Linux에서는 ALSA P/Invoke, Windows에서는 NAudio WASAPI, 그 외에는 합성 사인파.
/// </summary>
public sealed class AudioCapture : IAudioCapture
{
    // -------------------------------------------------------
    // 오디오 포맷 상수 (24 kHz PCM16 – VoiceLive 기본 포맷)
    // -------------------------------------------------------
    public const int SampleRate      = 24_000;
    public const int BitDepth        = 16;
    public const int TargetChunkMs   = 20;
    /// <summary>24000 샘플/s × 0.020 s = 480 샘플/채널</summary>
    public const int SamplesPerChunk = SampleRate * TargetChunkMs / 1000;

    // -------------------------------------------------------
    // 내부 백엔드 (null = 합성 모드)
    // -------------------------------------------------------
    private readonly IAudioCapture? _backend;

    /// <inheritdoc/>
    public CaptureMode Mode { get; private set; } = CaptureMode.Synthetic;

    // -------------------------------------------------------
    // 초기화: 플랫폼에 따라 ALSA/WASAPI/합성 백엔드 선택
    // -------------------------------------------------------
    /// <summary>
    /// 장치 캡처기를 초기화합니다.
    /// </summary>
    /// <param name="deviceIndex">장치 인덱스 (-1 = 기본 장치)</param>
    /// <param name="maxChannels">최대 채널 수 (1=모노 강제, 2=2ch 시도)</param>
    public AudioCapture(int deviceIndex = -1, int maxChannels = 2)
    {
        bool forceMono = maxChannels <= 1 ||
            Environment.GetEnvironmentVariable("DEVICESIM_FORCE_MONO") == "1";

        int requestedChannels = forceMono ? 1 : 2;

        // DEVICESIM_FORCE_SYNTHETIC: 플랫폼과 무관하게 합성 모드 강제 (CI/테스트 결정성).
        // 실제 마이크가 있는 개발 머신(macOS 등)에서도 합성 폴백을 검증할 수 있다.
        bool forceSynthetic =
            Environment.GetEnvironmentVariable("DEVICESIM_FORCE_SYNTHETIC") == "1";

        if (forceSynthetic)
        {
            _backend = null;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _backend = TryAlsaCapture(requestedChannels);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _backend = TryWasapiCapture(deviceIndex, requestedChannels);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            _backend = TryCoreAudioCapture(requestedChannels);
        }

        if (_backend is not null)
        {
            Mode = _backend.Mode;
        }
        else
        {
            Mode = CaptureMode.Synthetic;
            Console.WriteLine("[캡처] 합성 오디오 모드 (하드웨어 없음 – 440Hz 사인파)");
        }
    }

    // -------------------------------------------------------
    // ALSA 캡처 백엔드 (Linux/ARM)
    // -------------------------------------------------------
    private static IAudioCapture? TryAlsaCapture(int channels)
    {
        try
        {
            var alsa = new AlsaCapture("default", channels);
            if (alsa.Mode != CaptureMode.Synthetic)
                return alsa;
            alsa.Dispose();
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"[캡처] libasound.so.2 없음 – 합성 폴백: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[캡처] ALSA 초기화 실패 – 합성 폴백: {ex.Message}");
        }
        return null;
    }

    // -------------------------------------------------------
    // WASAPI 캡처 백엔드 (Windows) – NAudio
    // -------------------------------------------------------
    private static IAudioCapture? TryWasapiCapture(int deviceIndex, int channels)
    {
        try
        {
            var wasapi = new WasapiCapture(deviceIndex, channels);
            if (wasapi.Mode != CaptureMode.Synthetic)
                return wasapi;
            wasapi.Dispose();
        }
        catch (DllNotFoundException)
        {
            // winmm.dll 없음 – Windows 가 아닌 환경 (방어 코드)
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[캡처] WASAPI 초기화 실패 – 합성 폴백: {ex.Message}");
        }
        return null;
    }

    // -------------------------------------------------------
    // CoreAudio 캡처 백엔드 (macOS) – AudioToolbox AudioQueue
    // -------------------------------------------------------
    private static IAudioCapture? TryCoreAudioCapture(int channels)
    {
        try
        {
            var core = new CoreAudioCapture(channels);
            if (core.Mode != CaptureMode.Synthetic)
                return core;
            core.Dispose();
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"[캡처] AudioToolbox 없음 – 합성 폴백: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[캡처] CoreAudio 초기화 실패 – 합성 폴백: {ex.Message}");
        }
        return null;
    }

    // -------------------------------------------------------
    // Start: 백엔드 또는 합성 루프 실행
    // -------------------------------------------------------
    /// <inheritdoc/>
    public void Start(Action<byte[]> onChunk, CancellationToken ct)
    {
        if (_backend is not null)
        {
            _backend.Start(onChunk, ct);
            return;
        }

        // 합성 사인파 생성 태스크 (CI/하드웨어 없는 환경)
        Task.Run(() => SyntheticCaptureLoop(onChunk, ct), ct);
    }

    // -------------------------------------------------------
    // 합성 오디오 생성 루프 (CI/테스트용 – 440 Hz 사인파, 2ch)
    // -------------------------------------------------------
    private static void SyntheticCaptureLoop(Action<byte[]> onChunk, CancellationToken ct)
    {
        const double freq     = 440.0;
        const int    channels = 2;
        int samplesPerChunk   = SamplesPerChunk;
        int bytesPerChunk     = samplesPerChunk * channels * 2;
        double phase          = 0.0;
        double phaseInc       = 2.0 * Math.PI * freq / SampleRate;

        while (!ct.IsCancellationRequested)
        {
            var chunk = new byte[bytesPerChunk];
            for (int i = 0; i < samplesPerChunk; i++)
            {
                short sample = (short)(Math.Sin(phase) * short.MaxValue * 0.3);
                phase += phaseInc;
                int offset = i * 4;
                chunk[offset]     = (byte)(sample & 0xFF);
                chunk[offset + 1] = (byte)((sample >> 8) & 0xFF);
                chunk[offset + 2] = (byte)(sample & 0xFF);
                chunk[offset + 3] = (byte)((sample >> 8) & 0xFF);
            }
            onChunk(chunk);
            Thread.Sleep(TargetChunkMs);
        }
    }

    /// <inheritdoc/>
    public void Stop() => _backend?.Stop();

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        _backend?.Dispose();
    }
}

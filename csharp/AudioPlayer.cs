// =============================================================
// devicesim/csharp/AudioPlayer.cs
// PCM16 모노 오디오 재생 – 장치 백엔드 (플랫폼 자동 선택)
//
// 실제 대상 하드웨어: ARM Cortex-A53 스피커 (Linux/ALSA)
// 개발 환경: Windows WaveOut (NAudio)
// CI/하드웨어 없는 환경: 무음 모드 (바이트 카운트 로그만)
//
// --audio-out <file> 지정 시: FilePlayer 를 사용하세요.
// =============================================================

using System.Runtime.InteropServices;

namespace DeviceSim;

/// <summary>
/// PCM16 모노 오디오 플레이어 – 장치 백엔드.
/// Linux에서는 ALSA P/Invoke, Windows에서는 NAudio WaveOut, 그 외에는 무음 모드.
/// </summary>
public sealed class AudioPlayer : IAudioPlayer
{
    // -------------------------------------------------------
    // 오디오 포맷 상수
    // -------------------------------------------------------
    public const int SampleRate = 24_000;
    public const int BitDepth   = 16;
    public const int Channels   = 1;   // 다운링크 항상 모노

    // -------------------------------------------------------
    // 내부 백엔드
    // -------------------------------------------------------
    private readonly IAudioPlayer _backend;

    // -------------------------------------------------------
    // 초기화: 플랫폼에 따라 ALSA/WASAPI/무음 백엔드 선택
    // -------------------------------------------------------
    /// <summary>장치 플레이어를 초기화합니다.</summary>
    public AudioPlayer()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _backend = TryAlsaPlayer() ?? new SilentPlayer();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _backend = TryWasapiPlayer() ?? new SilentPlayer();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            _backend = TryCoreAudioPlayer() ?? new SilentPlayer();
        }
        else
        {
            _backend = new SilentPlayer();
        }
    }

    private static IAudioPlayer? TryCoreAudioPlayer()
    {
        try
        {
            return new CoreAudioPlayer();
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"[재생] AudioToolbox 없음 – 무음 모드: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[재생] CoreAudio 초기화 실패 – 무음 모드: {ex.Message}");
        }
        return null;
    }

    private static IAudioPlayer? TryAlsaPlayer()
    {
        try
        {
            var alsa = new AlsaPlayer("default");
            return alsa;
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"[재생] libasound.so.2 없음 – 무음 모드: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[재생] ALSA 초기화 실패 – 무음 모드: {ex.Message}");
        }
        return null;
    }

    private static IAudioPlayer? TryWasapiPlayer()
    {
        try
        {
            return new WasapiPlayer();
        }
        catch (DllNotFoundException)
        {
            // winmm.dll 없음 (방어 코드)
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[재생] WASAPI 초기화 실패 – 무음 모드: {ex.Message}");
        }
        return null;
    }

    // -------------------------------------------------------
    // IAudioPlayer 위임
    // -------------------------------------------------------
    /// <inheritdoc/>
    public void Enqueue(byte[] pcm16Mono) => _backend.Enqueue(pcm16Mono);

    /// <inheritdoc/>
    public void Clear() => _backend.Clear();

    /// <inheritdoc/>
    public bool IsPlaying => _backend.IsPlaying;

    /// <inheritdoc/>
    public void Dispose() => _backend.Dispose();
}

// -------------------------------------------------------
// 무음 플레이어 – 하드웨어 없는 환경 (CI/테스트)
// -------------------------------------------------------
internal sealed class SilentPlayer : IAudioPlayer
{
    public SilentPlayer()
    {
        Console.WriteLine("[재생] 무음 모드 (하드웨어 없음)");
    }

    public void Enqueue(byte[] pcm16Mono)
        => Console.WriteLine($"[재생] 무음 모드 – {pcm16Mono.Length} bytes 수신 (재생 생략)");

    public void Clear()
        => Console.WriteLine("[재생] 버퍼 클리어 (audio_clear 수신)");

    public bool IsPlaying => false;

    public void Dispose() { }
}

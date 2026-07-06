// =============================================================
// devicesim/csharp/WasapiPlayer.cs
// NAudio WaveOut 기반 PCM16 모노 오디오 재생 (Windows 개발 환경)
//
// Windows 전용입니다. Linux/ARM 타겟에서는 AlsaPlayer 를 사용하세요.
// AudioPlayer 가 RuntimeInformation.IsOSPlatform(Windows) 확인 후 선택합니다.
// =============================================================

using NAudio.Wave;

namespace DeviceSim;

/// <summary>
/// NAudio WaveOut 재생 백엔드 (Windows 전용).
/// </summary>
internal sealed class WasapiPlayer : IAudioPlayer
{
    private readonly WaveOutEvent        _waveOut;
    private readonly BufferedWaveProvider _buffer;

    /// <summary>WaveOut 재생 장치를 초기화합니다.</summary>
    public WasapiPlayer()
    {
        var format = new WaveFormat(AudioPlayer.SampleRate, AudioPlayer.BitDepth, AudioPlayer.Channels);
        _buffer  = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration          = TimeSpan.FromSeconds(5),
        };
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_buffer);
        _waveOut.Play();
        Console.WriteLine("[WASAPI 재생] WaveOut 초기화 완료 (PCM16/24kHz/모노)");
    }

    /// <inheritdoc/>
    public void Enqueue(byte[] pcm16Mono)
        => _buffer.AddSamples(pcm16Mono, 0, pcm16Mono.Length);

    /// <inheritdoc/>
    public void Clear()
    {
        _buffer.ClearBuffer();
        Console.WriteLine("[WASAPI 재생] 버퍼 클리어 (audio_clear 수신)");
    }

    /// <inheritdoc/>
    // 재생 중 판정: 버퍼에 아직 재생되지 않은 바이트가 남아 있으면 재생 중.
    public bool IsPlaying => _buffer.BufferedBytes > 0;

    /// <inheritdoc/>
    public void Dispose()
    {
        _waveOut.Stop();
        _waveOut.Dispose();
    }
}

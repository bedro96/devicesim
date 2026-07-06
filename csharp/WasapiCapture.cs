// =============================================================
// devicesim/csharp/WasapiCapture.cs
// NAudio WASAPI 기반 PCM16 마이크 캡처 (Windows 개발 환경)
//
// Windows 전용입니다. Linux/ARM 타겟에서는 AlsaCapture 를 사용하세요.
// AudioCapture 가 RuntimeInformation.IsOSPlatform(Windows) 확인 후 선택합니다.
// =============================================================

using NAudio.Wave;

namespace DeviceSim;

/// <summary>
/// NAudio WASAPI 캡처 백엔드 (Windows 전용).
/// </summary>
internal sealed class WasapiCapture : IAudioCapture
{
    private WaveInEvent? _waveIn;

    /// <inheritdoc/>
    public CaptureMode Mode { get; private set; } = CaptureMode.Synthetic;

    /// <summary>
    /// WASAPI 캡처 장치를 초기화합니다.
    /// </summary>
    /// <param name="deviceIndex">마이크 장치 인덱스 (-1 = 기본 장치)</param>
    /// <param name="maxChannels">최대 채널 수 (1=모노 강제, 2=2ch 시도)</param>
    public WasapiCapture(int deviceIndex = -1, int maxChannels = 2)
    {
        int devNum = deviceIndex < 0 ? 0 : deviceIndex;

        if (maxChannels >= 2 && TryOpenDevice(devNum, 2))
        {
            Mode = CaptureMode.Stereo;
            Console.WriteLine("[WASAPI 캡처] 2채널 MEMS 어레이 모드 (PCM16/24kHz/2ch)");
            return;
        }

        if (TryOpenDevice(devNum, 1))
        {
            Mode = CaptureMode.Mono;
            Console.WriteLine("[WASAPI 캡처] 모노 폴백 모드 (PCM16/24kHz/1ch)");
            return;
        }

        Console.Error.WriteLine("[WASAPI 캡처] 장치 열기 실패 → 합성 폴백 사용");
        Mode = CaptureMode.Synthetic;
    }

    private bool TryOpenDevice(int deviceNumber, int channels)
    {
        try
        {
            // 먼저 포맷 지원 여부 확인
            var test = new WaveInEvent
            {
                DeviceNumber       = deviceNumber,
                WaveFormat         = new WaveFormat(AudioCapture.SampleRate, AudioCapture.BitDepth, channels),
                BufferMilliseconds = AudioCapture.TargetChunkMs,
            };
            test.Dispose();

            // 실제 사용할 인스턴스 생성
            _waveIn = new WaveInEvent
            {
                DeviceNumber       = deviceNumber,
                WaveFormat         = new WaveFormat(AudioCapture.SampleRate, AudioCapture.BitDepth, channels),
                BufferMilliseconds = AudioCapture.TargetChunkMs,
            };
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WASAPI 캡처] {channels}ch 장치 열기 실패: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public void Start(Action<byte[]> onChunk, CancellationToken ct)
    {
        if (_waveIn is null)
            throw new InvalidOperationException("WASAPI 장치가 열려 있지 않습니다.");

        _waveIn.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded == 0) return;
            var chunk = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
            onChunk(chunk);
        };

        _waveIn.StartRecording();
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_waveIn is not null && Mode != CaptureMode.Synthetic)
            _waveIn.StopRecording();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        _waveIn?.Dispose();
    }
}

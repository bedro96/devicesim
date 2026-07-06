// =============================================================
// devicesim/csharp/IAudioCapture.cs
// 오디오 캡처 백엔드 추상 인터페이스
//
// 구현체:
//   AudioCapture  – 장치 백엔드 (ALSA on Linux, NAudio on Windows, 합성 폴백)
//   FileCapture   – 파일/루프백 백엔드 (PCM/WAV 파일 → 마이크 소스)
// =============================================================

namespace DeviceSim;

/// <summary>
/// PCM16 오디오 캡처 백엔드 인터페이스.
/// 장치 백엔드(ALSA/WASAPI/합성)와 파일 백엔드가 이 인터페이스를 구현합니다.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>현재 캡처 모드 (Stereo, Mono, Synthetic, File).</summary>
    CaptureMode Mode { get; }

    /// <summary>
    /// 캡처를 시작합니다. PCM16 청크마다 <paramref name="onChunk"/> 가 호출됩니다.
    /// 2채널 모드에서는 인터리브드 스테레오 PCM16 바이트가 전달됩니다.
    /// </summary>
    void Start(Action<byte[]> onChunk, CancellationToken ct);

    /// <summary>캡처를 중지합니다.</summary>
    void Stop();
}

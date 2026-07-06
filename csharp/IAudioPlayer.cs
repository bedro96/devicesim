// =============================================================
// devicesim/csharp/IAudioPlayer.cs
// 오디오 재생 백엔드 추상 인터페이스
//
// 구현체:
//   AudioPlayer  – 장치 백엔드 (ALSA on Linux, NAudio on Windows, 무음 폴백)
//   FilePlayer   – 파일 백엔드 (다운링크 오디오 → WAV 파일 기록)
// =============================================================

namespace DeviceSim;

/// <summary>
/// PCM16 모노 오디오 재생 백엔드 인터페이스.
/// 장치 백엔드(ALSA/WaveOut/무음)와 파일 백엔드가 이 인터페이스를 구현합니다.
/// </summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>
    /// PCM16 모노 오디오 데이터를 재생 큐에 추가합니다.
    /// </summary>
    void Enqueue(byte[] pcm16Mono);

    /// <summary>
    /// 재생 버퍼를 비웁니다. audio_clear JSON 이벤트 수신 시 호출됩니다.
    /// </summary>
    void Clear();

    /// <summary>
    /// 현재 스피커가 재생 중인지 여부. 반이중 에코 억제(EchoGate)에서
    /// 재생 중 마이크 업링크를 억제할지 판단하는 데 사용됩니다.
    /// </summary>
    bool IsPlaying { get; }
}

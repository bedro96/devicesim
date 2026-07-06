// =============================================================
// devicesim/csharp/FilePlayer.cs
// 파일 기반 오디오 재생 백엔드 (CI/스모크 테스트용)
//
// 다운링크 PCM16 모노 오디오를 스피커 대신 WAV 파일에 기록합니다.
// --audio-out <path> 플래그로 활성화됩니다.
//
// 출력: 표준 RIFF WAV (PCM16 모노 24kHz)
//   → ffplay / SoX / Audacity 로 재생 가능
//   → CI 에서 다운링크 오디오 검증에 사용 가능
// =============================================================

namespace DeviceSim;

/// <summary>
/// 다운링크 PCM16 모노 오디오를 WAV 파일에 기록하는 재생 백엔드.
/// </summary>
public sealed class FilePlayer : IAudioPlayer
{
    private readonly string           _outputPath;
    private readonly List<byte>       _samples = new();
    private readonly object           _lock    = new();

    // -------------------------------------------------------
    // 초기화
    // -------------------------------------------------------
    /// <summary>
    /// WAV 출력 파일 경로를 설정합니다.
    /// </summary>
    public FilePlayer(string outputPath)
    {
        _outputPath = outputPath;
        Console.WriteLine($"[파일 재생] 출력 파일: {outputPath} (PCM16/24kHz/모노 WAV)");
    }

    // -------------------------------------------------------
    // IAudioPlayer 구현
    // -------------------------------------------------------
    /// <inheritdoc/>
    public void Enqueue(byte[] pcm16Mono)
    {
        lock (_lock)
        {
            _samples.AddRange(pcm16Mono);
        }
        Console.WriteLine($"[파일 재생] {pcm16Mono.Length} bytes 수신 (누적 {_samples.Count} bytes)");
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_lock)
        {
            _samples.Clear();
        }
        Console.WriteLine("[파일 재생] 버퍼 클리어 (audio_clear 수신)");
    }

    /// <inheritdoc/>
    // 파일 백엔드는 실시간 스피커 출력이 아니므로 에코가 발생하지 않는다.
    public bool IsPlaying => false;

    // -------------------------------------------------------
    // Dispose: 수신된 오디오를 WAV 파일로 저장
    // -------------------------------------------------------
    /// <summary>
    /// 누적된 PCM16 모노 오디오를 WAV 파일로 저장하고 리소스를 해제합니다.
    /// </summary>
    public void Dispose()
    {
        byte[] pcmData;
        lock (_lock)
        {
            pcmData = _samples.ToArray();
        }

        if (pcmData.Length == 0)
        {
            Console.WriteLine("[파일 재생] 저장할 오디오 없음 (다운링크 수신 없음)");
            return;
        }

        WriteWav(_outputPath, pcmData, AudioPlayer.SampleRate, AudioPlayer.Channels);
        double durationSec = (double)pcmData.Length / (AudioPlayer.SampleRate * AudioPlayer.Channels * 2);
        Console.WriteLine($"[파일 재생] WAV 저장 완료: {_outputPath} ({durationSec:F1}초, {pcmData.Length} bytes)");
    }

    // -------------------------------------------------------
    // WAV 파일 쓰기 (PCM16 RIFF)
    // -------------------------------------------------------
    private static void WriteWav(string path, byte[] pcmData, int sampleRate, int channels)
    {
        int byteRate    = sampleRate * channels * 2;  // PCM16 = 2 bytes per sample
        int blockAlign  = channels * 2;

        using var writer = new BinaryWriter(File.Open(path, FileMode.Create));

        // RIFF 헤더
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmData.Length);          // 파일 크기 - 8
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt 청크
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);                            // 청크 크기
        writer.Write((short)1);                      // PCM 포맷
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)16);                     // 비트 깊이

        // data 청크
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmData.Length);
        writer.Write(pcmData);
    }
}

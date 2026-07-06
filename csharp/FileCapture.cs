// =============================================================
// devicesim/csharp/FileCapture.cs
// 파일 기반 오디오 캡처 백엔드 (CI/스모크 테스트용)
//
// 원시 PCM 파일 또는 WAV 파일을 마이크 소스로 사용합니다.
// --audio-source <path> 플래그로 활성화됩니다.
//
// 지원 형식:
//   .wav  – RIFF WAV 헤더를 건너뜁니다 (PCM16 모노/스테레오)
//   기타  – 원시 PCM16 데이터로 처리합니다
//
// 동작:
//   파일 끝에 도달하면 처음부터 다시 재생합니다 (루프).
//   채널 수는 WAV 헤더에서 읽거나 --channels 파라미터로 지정합니다.
// =============================================================

namespace DeviceSim;

/// <summary>
/// PCM/WAV 파일을 마이크 소스로 사용하는 캡처 백엔드.
/// CI 환경, 파일 기반 스모크 테스트, 루프백 검증에 적합합니다.
/// </summary>
public sealed class FileCapture : IAudioCapture
{
    private readonly string _filePath;
    private readonly int    _fileChannels;

    /// <inheritdoc/>
    public CaptureMode Mode { get; }

    // -------------------------------------------------------
    // 초기화
    // -------------------------------------------------------
    /// <summary>
    /// PCM/WAV 파일을 마이크 소스로 초기화합니다.
    /// </summary>
    /// <param name="filePath">PCM16 또는 WAV 파일 경로</param>
    /// <param name="channels">채널 수 (WAV 헤더가 없는 경우 사용)</param>
    public FileCapture(string filePath, int channels = 1)
    {
        _filePath = filePath;
        _fileChannels = channels;
        Mode = channels >= 2 ? CaptureMode.Stereo : CaptureMode.Mono;

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"오디오 소스 파일을 찾을 수 없습니다: {filePath}");

        Console.WriteLine($"[파일 캡처] 소스: {filePath} ({channels}ch PCM16)");
    }

    // -------------------------------------------------------
    // 캡처 시작 – 파일을 20ms 청크로 읽어 콜백 호출
    // -------------------------------------------------------
    /// <inheritdoc/>
    public void Start(Action<byte[]> onChunk, CancellationToken ct)
    {
        Task.Run(() => ReadLoop(onChunk, ct), ct);
    }

    private void ReadLoop(Action<byte[]> onChunk, CancellationToken ct)
    {
        int bytesPerFrame  = _fileChannels * 2;  // PCM16: 2 bytes per sample
        int framesPerChunk = AudioCapture.SamplesPerChunk;
        int chunkBytes     = framesPerChunk * bytesPerFrame;
        var buf            = new byte[chunkBytes];

        while (!ct.IsCancellationRequested)
        {
            using var stream = OpenPcmStream(_filePath, out int _);

            while (!ct.IsCancellationRequested)
            {
                int totalRead = 0;
                while (totalRead < chunkBytes)
                {
                    int read = stream.Read(buf, totalRead, chunkBytes - totalRead);
                    if (read == 0)
                        break;  // EOF → 루프 재시작
                    totalRead += read;
                }

                if (totalRead == 0)
                    break;  // 파일 끝 → 루프

                // 마지막 청크가 짧은 경우 0 으로 채움
                if (totalRead < chunkBytes)
                    Array.Clear(buf, totalRead, chunkBytes - totalRead);

                var chunk = new byte[chunkBytes];
                Buffer.BlockCopy(buf, 0, chunk, 0, chunkBytes);
                onChunk(chunk);

                // 실시간 속도 유지 (20ms 청크 = 20ms 대기)
                Thread.Sleep(AudioCapture.TargetChunkMs);
            }
        }
    }

    // -------------------------------------------------------
    // WAV 헤더 건너뛰기 (RIFF 체크)
    // -------------------------------------------------------
    private static Stream OpenPcmStream(string path, out int headerBytes)
    {
        var stream = File.OpenRead(path);
        headerBytes = 0;

        // WAV 파일 감지: "RIFF" 마커
        var magic = new byte[4];
        int read  = stream.Read(magic, 0, 4);
        if (read == 4 &&
            magic[0] == 'R' && magic[1] == 'I' && magic[2] == 'F' && magic[3] == 'F')
        {
            // 최소 WAV 헤더 = 44 bytes, "data" 청크 위치까지 스킵
            stream.Seek(0, SeekOrigin.Begin);
            headerBytes = SkipWavHeader(stream);
        }
        else
        {
            // 원시 PCM: 처음부터 읽음
            stream.Seek(0, SeekOrigin.Begin);
        }

        return stream;
    }

    private static int SkipWavHeader(Stream stream)
    {
        // RIFF WAV: "RIFF" (4) + size (4) + "WAVE" (4) + chunks…
        // "data" 서브청크를 찾아 그 직후부터 PCM 데이터
        var header = new byte[12];
        stream.Read(header, 0, 12);  // RIFF + size + WAVE

        int offset = 12;
        var chunkId   = new byte[4];
        var chunkSize = new byte[4];

        while (stream.Position < stream.Length)
        {
            if (stream.Read(chunkId, 0, 4) < 4) break;
            if (stream.Read(chunkSize, 0, 4) < 4) break;
            offset += 8;

            int size = chunkId[0] == 'd' && chunkId[1] == 'a' && chunkId[2] == 't' && chunkId[3] == 'a'
                ? 0   // "data" 청크: PCM 시작 위치 반환
                : BitConverter.ToInt32(chunkSize, 0);

            if (size == 0) return offset;

            stream.Seek(size, SeekOrigin.Current);
            offset += size;
        }

        return offset;
    }

    /// <inheritdoc/>
    public void Stop() { }

    /// <inheritdoc/>
    public void Dispose() { }
}

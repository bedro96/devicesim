// =============================================================
// devicesim/csharp/Program.cs
// C# 디바이스 시뮬레이터 – CLI 진입점
//
// 실행 예시:
//   dotnet run -- --url https://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io
//   dotnet run -- --url https://kukovm2.koreacentral.cloudapp.azure.com:5173 --insecure
//   dotnet run -- --url https://localhost:8000 --channels 2             # 2-마이크 캡처(모노 다운믹스)
//   dotnet run -- --url https://localhost:8000 --audio-source mic.wav   # 파일 소스
//   dotnet run -- --url https://localhost:8000 --audio-out out.wav      # WAV 저장
//
// 오디오 백엔드:
//   Linux  → ALSA P/Invoke (libasound.so.2) → 합성 폴백
//   Windows → NAudio WASAPI/WaveOut         → 합성/무음 폴백
//   CI     → 합성 440Hz 사인파 (캡처) + 무음 (재생)
//
// 파일 백엔드 (--audio-source / --audio-out):
//   양방향 파일 기반 테스트에 사용합니다.
//   CI 스모크 테스트: --audio-source test.wav --audio-out out.wav
//
// 참고: devicesim/ms_ref/foundry_LV_Csharp_ref.cs (VoiceLive 2ch 조사)
// =============================================================

using System.CommandLine;
using DeviceSim;

// -------------------------------------------------------
// CLI 파라미터 정의
// -------------------------------------------------------

var urlOption = new Option<string>(
    name:        "--url",
    description: "연결할 서버 URL (예: https://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io)"
)
{ IsRequired = true };

var channelsOption = new Option<int>(
    name:         "--channels",
    description:  "캡처 채널 수 (1=모노 기본값, 2=2-마이크 어레이 캡처 후 모노 다운믹스)",
    getDefaultValue: () => 1
);

var durationOption = new Option<int>(
    name:         "--duration",
    description:  "실행 시간 (초). 0=Ctrl+C 까지 무한 실행",
    getDefaultValue: () => 0
);

var deviceOption = new Option<int>(
    name:         "--device",
    description:  "마이크 장치 인덱스 (-1=기본 장치)",
    getDefaultValue: () => -1
);

var audioSourceOption = new Option<string?>(
    name:         "--audio-source",
    description:  "마이크 소스로 사용할 PCM16 또는 WAV 파일 경로 (지정 시 파일 백엔드 사용)",
    getDefaultValue: () => null
);

var audioOutOption = new Option<string?>(
    name:         "--audio-out",
    description:  "다운링크 오디오를 저장할 WAV 파일 경로 (지정 시 파일 백엔드 사용)",
    getDefaultValue: () => null
);

var insecureOption = new Option<bool>(
    name:         "--insecure",
    description:  "자체 서명 인증서를 허용합니다 (예: --url https://kukovm2...:5173). 기본값: false (보안 검증 활성)",
    getDefaultValue: () => false
);

var allowBargeInOption = new Option<bool>(
    name:         "--allow-barge-in",
    description:  "에코 억제(반이중)를 비활성화합니다. 기본값(false)은 억제 활성: 스피커 " +
                  "재생 중 마이크 업링크를 억제하여 장치가 자기 출력을 다시 잡아 서버 VAD 가 " +
                  "바지인으로 오인하고 에이전트 말을 끊는 에코 루프를 방지합니다. 하드웨어 AEC 가 " +
                  "있는 환경에서만 이 플래그 사용을 권장합니다.",
    getDefaultValue: () => false
);

var rootCommand = new RootCommand("LGE ThinQ 디바이스 시뮬레이터 (C#) – ARM 2× MEMS 마이크 에뮬레이션");
rootCommand.AddOption(urlOption);
rootCommand.AddOption(channelsOption);
rootCommand.AddOption(durationOption);
rootCommand.AddOption(deviceOption);
rootCommand.AddOption(audioSourceOption);
rootCommand.AddOption(audioOutOption);
rootCommand.AddOption(insecureOption);
rootCommand.AddOption(allowBargeInOption);

// -------------------------------------------------------
// 메인 핸들러
// -------------------------------------------------------
rootCommand.SetHandler(async (
    string  url,
    int     channels,
    int     duration,
    int     device,
    string? audioSource,
    string? audioOut,
    bool    insecure,
    bool    allowBargeIn) =>
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n[종료] Ctrl+C 수신 – 세션 종료 중...");
        cts.Cancel();
    };

    if (duration > 0)
    {
        cts.CancelAfter(TimeSpan.FromSeconds(duration));
        Console.WriteLine($"[설정] {duration}초 후 자동 종료");
    }

    Console.WriteLine($"[설정] 요청 채널={channels}, URL={url}");
    if (insecure)
        Console.WriteLine("[설정] TLS 검증 비활성화 (--insecure)");
    if (allowBargeIn)
        Console.WriteLine("[설정] 에코 억제 비활성화 (--allow-barge-in): 풀듀플렉스 (AEC 필요)");

    // -------------------------------------------------------
    // 오디오 캡처 백엔드 선택
    //   --audio-source: 파일 백엔드
    //   기본: 장치 백엔드 (ALSA/WASAPI/합성)
    // -------------------------------------------------------
    IAudioCapture capture;
    if (audioSource is not null)
    {
        capture = new FileCapture(audioSource, channels);
        Console.WriteLine($"[설정] 파일 캡처 백엔드: {audioSource}");
    }
    else
    {
        capture = new AudioCapture(device, maxChannels: channels);
    }

    // -------------------------------------------------------
    // 오디오 재생 백엔드 선택
    //   --audio-out: 파일 백엔드 (WAV 저장)
    //   기본: 장치 백엔드 (ALSA/WASAPI/무음)
    // -------------------------------------------------------
    IAudioPlayer player;
    if (audioOut is not null)
    {
        player = new FilePlayer(audioOut);
        Console.WriteLine($"[설정] 파일 재생 백엔드: {audioOut}");
    }
    else
    {
        player = new AudioPlayer();
    }

    // -------------------------------------------------------
    // WebSocket 세션 실행
    // -------------------------------------------------------
    await using var session = new WsSession(url, capture, player, insecure, echoSuppression: !allowBargeIn);

    try
    {
        await session.RunAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[종료] 세션 정상 종료");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[오류] {ex.GetType().Name}: {ex.Message}");
        Environment.Exit(1);
    }
    finally
    {
        capture.Dispose();
        player.Dispose();
    }
}, urlOption, channelsOption, durationOption, deviceOption, audioSourceOption, audioOutOption, insecureOption, allowBargeInOption);

return await rootCommand.InvokeAsync(args);

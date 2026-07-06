// =============================================================
// devicesim/csharp/WsSession.cs
// WebSocket 세션 핸들러 – /session 엔드포인트 연결
//
// WSS 계약 (frontend/backend 확인):
//   - wss://<host>/session 에 연결 (바이너리 프레이밍)
//   - 서버가 자동 시작: appliance_snapshot → status:listening
//   - 연결 즉시 업링크 스트리밍 시작
//   - 다운링크 바이너리: PCM16/24kHz/모노 → 스피커
//   - 다운링크 텍스트 JSON: appliance_snapshot, appliance_update,
//                          transcript, status, audio_clear
//   - 업링크 와이어 포맷: 항상 모노 PCM16 (2채널 캡처는 전송 전 다운믹스)
// =============================================================

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DeviceSim;

/// <summary>
/// WebSocket /session 세션 관리자.
/// 업링크(마이크 → 서버)와 다운링크(서버 → 스피커) 루프를 병렬 실행합니다.
/// </summary>
public sealed class WsSession : IAsyncDisposable
{
    // -------------------------------------------------------
    // 수신 버퍼 크기 (최대 프레임: ~64 KB)
    // -------------------------------------------------------
    private const int RecvBufSize = 65_536;

    private readonly Uri             _uri;
    private readonly IAudioCapture   _capture;
    private readonly IAudioPlayer    _player;
    private readonly ClientWebSocket _ws;
    private readonly bool            _echoSuppression;

    // -------------------------------------------------------
    // 생성자: 연결 URI, 캡처/플레이어 주입
    // -------------------------------------------------------
    /// <summary>
    /// WsSession 을 초기화합니다.
    /// </summary>
    /// <param name="baseUrl">서버 기본 URL</param>
    /// <param name="capture">오디오 캡처 백엔드</param>
    /// <param name="player">오디오 재생 백엔드</param>
    /// <param name="insecure">true 이면 자체 서명 인증서를 허용합니다 (개발/테스트용)</param>
    /// <param name="echoSuppression">
    /// true(기본)이면 반이중 에코 억제를 활성화합니다: 스피커 재생 중 마이크
    /// 업링크를 억제하여, 장치가 자기 스피커 출력을 다시 잡아 서버 VAD 가
    /// 바지인으로 오인하고 에이전트 음성을 끊는 에코 루프를 방지합니다.
    /// </param>
    public WsSession(string baseUrl, IAudioCapture capture, IAudioPlayer player,
                     bool insecure = false, bool echoSuppression = true)
    {
        // 업링크 와이어 포맷은 항상 모노 PCM16 이다 (Azure Voice Live 요구사항).
        // 2채널 캡처는 UplinkLoop 에서 전송 전에 모노로 다운믹스하므로, URL 에
        // channels 쿼리 파라미터를 붙이지 않는다 (브라우저 /session 계약과 동일).
        _uri             = BuildWssUri(baseUrl, "");
        _capture         = capture;
        _player          = player;
        _echoSuppression = echoSuppression;
        _ws              = new ClientWebSocket();

        if (insecure)
        {
            // 자체 서명 인증서 허용 (예: kukovm2:5173 개발 환경)
            _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
    }

    // -------------------------------------------------------
    // HTTP(S) → WSS URI 변환
    // -------------------------------------------------------
    private static Uri BuildWssUri(string baseUrl, string extra)
    {
        // https:// → wss://, http:// → ws://
        var url = baseUrl.TrimEnd('/');
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "wss://" + url["https://".Length..];
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "ws://"  + url["http://".Length..];

        return new Uri($"{url}/session{extra}");
    }

    // -------------------------------------------------------
    // 캡처 모드 → 소스 채널 수 매핑
    //   Stereo    : 실제 2-마이크 어레이 (ALSA/WASAPI/CoreAudio) → 2채널
    //   Synthetic : CI용 합성 신호 (SyntheticCaptureLoop가 2채널 인터리브 생성) → 2채널
    //   그 외(Mono/File-mono) → 1채널
    //   2채널로 판정된 소스는 업링크 전에 모노로 다운믹스된다.
    // -------------------------------------------------------
    internal static int SourceChannels(CaptureMode mode) => mode switch
    {
        CaptureMode.Stereo    => 2,
        CaptureMode.Synthetic => 2,
        _                     => 1,
    };

    // -------------------------------------------------------
    // 세션 실행: 연결 → 즉시 스트리밍 → 종료
    // -------------------------------------------------------
    /// <summary>
    /// WebSocket 세션을 실행합니다. CT 취소 시 정상 종료됩니다.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"[세션] 연결 중: {_uri}");
        await _ws.ConnectAsync(_uri, ct);
        Console.WriteLine($"[세션] 연결 완료 – 스트리밍 시작");

        // 업링크/다운링크 병렬 실행
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var uplinkTask   = Task.Run(() => UplinkLoop(cts.Token),   cts.Token);
        var downlinkTask = Task.Run(() => DownlinkLoop(cts.Token), cts.Token);

        try
        {
            await Task.WhenAny(uplinkTask, downlinkTask);
        }
        finally
        {
            cts.Cancel();
        }

        // 예외 전파
        await uplinkTask;
        await downlinkTask;
    }

    // -------------------------------------------------------
    // 업링크 루프: 마이크 캡처 → WebSocket 바이너리 전송
    // -------------------------------------------------------
    private async Task UplinkLoop(CancellationToken ct)
    {
        // 캡처 버퍼를 큐에 담아 비동기 전송
        var queue = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();

        // 소스 채널 수(1 또는 2) 결정 — 2채널 캡처는 전송 전에 모노로 다운믹스한다.
        int srcChannels = SourceChannels(_capture.Mode);

        _capture.Start(chunk =>
        {
            // 업링크는 항상 모노 PCM16 (Azure Voice Live 요구사항).
            // 2채널(스테레오/합성) 청크는 여기서 모노로 다운믹스한다.
            queue.Enqueue(Downmix.ToMono(chunk, srcChannels));
        }, ct);

        // 반이중 에코 억제 게이트: 스피커 재생 중 + hangover 동안 업링크 억제
        var gate    = _echoSuppression ? new EchoGate() : null;
        var clock   = EchoGate.MonotonicClock();
        bool suppressing = false;

        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            if (queue.TryDequeue(out var chunk))
            {
                // 스피커 재생 중이면(+hangover) 이 마이크 청크를 드롭 (에코 방지)
                if (gate is not null && !gate.ShouldSend(_player.IsPlaying, clock()))
                {
                    if (!suppressing)
                    {
                        Console.WriteLine("[에코 억제] 스피커 재생 중 – 마이크 업링크 일시 중단");
                        suppressing = true;
                    }
                    continue;
                }
                if (suppressing)
                {
                    Console.WriteLine("[에코 억제] 재생 종료 – 마이크 업링크 재개");
                    suppressing = false;
                }

                await _ws.SendAsync(
                    new ArraySegment<byte>(chunk),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    ct
                );
            }
            else
            {
                // 큐가 비면 짧게 대기
                await Task.Delay(1, ct);
            }
        }

        _capture.Stop();
    }

    // -------------------------------------------------------
    // 다운링크 루프: WebSocket 수신 → 오디오/JSON 처리
    // -------------------------------------------------------
    private async Task DownlinkLoop(CancellationToken ct)
    {
        var buf = new byte[RecvBufSize];

        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            int totalBytes = 0;

            // 프레임 완전 수신 (분할 메시지 처리)
            using var ms = new System.IO.MemoryStream();
            do
            {
                result = await _ws.ReceiveAsync(
                    new ArraySegment<byte>(buf),
                    ct
                );

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("[세션] 서버가 연결을 닫았습니다.");
                    return;
                }

                ms.Write(buf, 0, result.Count);
                totalBytes += result.Count;
            } while (!result.EndOfMessage);

            var payload = ms.ToArray();

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // 다운링크 PCM16 모노 오디오 → 스피커
                _player.Enqueue(payload);
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                // JSON 텍스트 이벤트 처리
                HandleJsonEvent(Encoding.UTF8.GetString(payload));
            }
        }
    }

    // -------------------------------------------------------
    // JSON 텍스트 이벤트 처리 (로그 출력, 화면 없음)
    // -------------------------------------------------------
    private void HandleJsonEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
            {
                Console.WriteLine($"[JSON] {json}");
                return;
            }

            var type = typeProp.GetString();
            switch (type)
            {
                case "appliance_snapshot":
                    Console.WriteLine("[이벤트] appliance_snapshot 수신 – 기기 상태 초기화");
                    break;

                case "appliance_update":
                    // 기기 ID/상태 로그
                    var id = root.TryGetProperty("appliance", out var app)
                        ? app.TryGetProperty("id", out var i) ? i.GetString() : "?" : "?";
                    Console.WriteLine($"[이벤트] appliance_update – id={id}");
                    break;

                case "transcript":
                    var role = root.TryGetProperty("role", out var r) ? r.GetString() : "?";
                    var text = root.TryGetProperty("text", out var t) ? t.GetString() : "";
                    Console.WriteLine($"[전사] [{role}] {text}");
                    break;

                case "status":
                    var state = root.TryGetProperty("state", out var s) ? s.GetString() : "?";
                    Console.WriteLine($"[상태] {state}");
                    break;

                case "audio_clear":
                    // 재생 버퍼 플러시 (바지인 처리)
                    _player.Clear();
                    break;

                default:
                    Console.WriteLine($"[JSON] type={type}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[JSON 파싱 오류] {ex.Message}: {json[..Math.Min(80, json.Length)]}");
        }
    }

    // -------------------------------------------------------
    // 리소스 해제: WebSocket 정상 종료
    // -------------------------------------------------------
    /// <summary>WebSocket 을 정상 종료하고 리소스를 해제합니다.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "디바이스 종료",
                    CancellationToken.None
                );
            }
            catch { /* 종료 중 오류 무시 */ }
        }
        _ws.Dispose();
    }
}

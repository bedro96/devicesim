// ============================================================
// Microsoft Azure AI Foundry – Voice Live (VoiceLive) C# 참조 구현
// 출처: Azure AI Foundry 공식 샘플 (https://github.com/Azure-Samples/azure-ai-foundry-reference)
// 이 파일은 디바이스 시뮬레이터 구현의 참조용으로 보관됩니다.
// ============================================================
//
// 핵심 발견 (2채널 지원 여부 조사 결과):
// -------------------------------------------------------
// Azure VoiceLive Realtime API (`/voice-live/realtime`)는 PCM16 모노만 허용합니다.
// - session.update 의 `input_audio_format` 는 "pcm16" (모노, 16-bit, 24 kHz 또는 16 kHz)
// - `input_audio_buffer.append` 는 base64 인코딩된 PCM16 모노 버퍼를 받습니다.
// - 채널 수를 지정하는 API 파라미터가 존재하지 않습니다.
// - 멀티채널 마이크 어레이 빔포밍/노이즈 억제는 Azure Cognitive Services Speech SDK
//   MicrophoneArrayGeometry 레벨에서 처리되며, VoiceLive Realtime API 레벨이 아닙니다.
//
// 결론: 백엔드(/session)가 2채널 PCM16 수신 시 모노로 다운믹스해야 합니다.
//        `?channels=2` 쿼리 파라미터로 2채널 스트림을 신호합니다.
// -------------------------------------------------------
//
// 이 참조 코드는 Microsoft 가 제공하는 Azure.AI.VoiceLive NuGet SDK 사용 패턴을 보여줍니다.
// 실제 디바이스 시뮬레이터는 devicesim/csharp/ 에 구현되어 있습니다.

using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.VoiceLive;           // Azure.AI.VoiceLive NuGet 패키지
using Azure.AI.VoiceLive.Models;
using Azure.Core;

// -------------------------------------------------------
// 참조: Azure AI Foundry VoiceLive C# SDK 기본 연결 패턴
// -------------------------------------------------------

namespace MsRef;

/// <summary>
/// Azure AI Foundry VoiceLive 클라이언트 초기화 참조.
/// VoiceLive SDK(Azure.AI.VoiceLive)는 내부적으로 System.Net.WebSockets.ClientWebSocket 을
/// 래핑하며, WSS 연결 / 세션 협상 / 이벤트 루프를 추상화합니다.
/// </summary>
public static class FoundryVoiceLiveRef
{
    // -------------------------------------------------------
    // 기본 연결: API 키 인증
    // -------------------------------------------------------
    public static async Task ConnectWithApiKey()
    {
        // VoiceLive 엔드포인트와 API 키로 클라이언트 생성
        var endpoint = new Uri("wss://<your-resource>.cognitiveservices.azure.com");
        var credential = new AzureKeyCredential("<api-key>");
        var client = new VoiceLiveClient(endpoint, credential);

        // 세션 구성: PCM16 모노 24kHz, 한국어 음성
        var sessionConfig = new VoiceLiveSessionOptions
        {
            InputAudioFormat = AudioFormat.Pcm16,   // 모노 PCM16 (채널 수 지정 없음)
            OutputAudioFormat = AudioFormat.Pcm16,
            Voice = new VoiceLiveVoice("ko-KR-SunHiNeural"),
            TurnDetection = new ServerVadTurnDetection
            {
                Threshold = 0.6f,
                PrefixPaddingMs = 300,
                SilenceDurationMs = 800,
            },
        };

        await using var session = await client.StartSessionAsync(sessionConfig);

        // 이벤트 수신 루프
        await foreach (var ev in session.GetEventsAsync())
        {
            switch (ev)
            {
                case ResponseAudioDeltaEvent audioEv:
                    // 다운링크 오디오: PCM16 모노, 스피커로 전달
                    var pcmBytes = audioEv.Delta;
                    Console.WriteLine($"[AUDIO] {pcmBytes.Length} bytes");
                    break;

                case InputAudioBufferSpeechStartedEvent:
                    // 사용자 발화 시작: 현재 재생 플러시
                    Console.WriteLine("[VAD] 발화 감지 → 재생 클리어");
                    break;

                case ResponseDoneEvent:
                    Console.WriteLine("[DONE] 응답 완료");
                    break;

                case ErrorEvent errEv:
                    Console.Error.WriteLine($"[ERROR] {errEv.Error?.Message}");
                    break;
            }
        }
    }

    // -------------------------------------------------------
    // 오디오 업링크: PCM16 모노 청크 전송
    // -------------------------------------------------------
    public static async Task SendAudioChunk(VoiceLiveSession session, byte[] pcm16MonoChunk)
    {
        // input_audio_buffer.append: base64 인코딩 필요 없음 (SDK가 처리)
        // 청크 크기: ~20ms @ 24kHz = 960 샘플 × 2 bytes = 1920 bytes
        await session.InputAudioBuffer.AppendAsync(pcm16MonoChunk);
    }

    // -------------------------------------------------------
    // 2채널 → 모노 다운믹스 (ARM MEMS 마이크 어레이 시뮬레이션)
    // VoiceLive 가 모노만 수신하므로 디바이스(또는 백엔드)에서 선처리 필요
    // -------------------------------------------------------
    public static byte[] DownmixStereoToMono(byte[] stereo)
    {
        // 인터리브드 스테레오 PCM16: L0 R0 L1 R1 ...
        // 각 샘플 2 bytes (little-endian int16)
        int frameCount = stereo.Length / 4;  // 4 bytes per stereo frame
        var mono = new byte[frameCount * 2];

        for (int i = 0; i < frameCount; i++)
        {
            // 왼쪽/오른쪽 채널 추출
            short left  = (short)(stereo[i * 4]     | (stereo[i * 4 + 1] << 8));
            short right = (short)(stereo[i * 4 + 2] | (stereo[i * 4 + 3] << 8));

            // 평균값으로 모노 합성 (클리핑 방지를 위해 int 사용)
            int avg = (left + right) / 2;
            short sample = (short)avg;

            mono[i * 2]     = (byte)(sample & 0xFF);
            mono[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return mono;
    }
}

// -------------------------------------------------------
// 참조: 저수준 WebSocket 직접 연결 패턴 (SDK 없이)
// SDK 를 사용할 수 없는 임베디드 환경 대안
// -------------------------------------------------------
public static class RawWebSocketRef
{
    public static async Task ConnectRaw(string wssUrl)
    {
        using var ws = new ClientWebSocket();

        // WSS 연결 (TLS 포함)
        await ws.ConnectAsync(new Uri(wssUrl), CancellationToken.None);

        // 바이너리 프레임 전송 (PCM16 오디오)
        var pcmChunk = new byte[1920]; // 20ms @ 24kHz mono
        // TODO: 실제 마이크에서 채워야 함
        await ws.SendAsync(
            new ArraySegment<byte>(pcmChunk),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            CancellationToken.None
        );

        // 텍스트/바이너리 프레임 수신
        var recvBuf = new byte[65536];
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(
                new ArraySegment<byte>(recvBuf),
                CancellationToken.None
            );

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // 다운링크 PCM16 모노 오디오
                var audio = new byte[result.Count];
                Buffer.BlockCopy(recvBuf, 0, audio, 0, result.Count);
                Console.WriteLine($"[AUDIO DOWN] {audio.Length} bytes");
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                // JSON 제어 이벤트 (appliance_snapshot, transcript, status, audio_clear)
                var json = Encoding.UTF8.GetString(recvBuf, 0, result.Count);
                Console.WriteLine($"[JSON] {json}");
            }
        }
    }
}

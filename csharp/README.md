# C# 디바이스 시뮬레이터 (`devicesim/csharp`)

ARM Cortex-A53 + 2× MEMS 마이크 어레이 디바이스를 에뮬레이션하는 headless C# CLI입니다.
Python 에뮬레이터와 동일한 end-to-end 동작(마이크 입력 → 업링크, 오디오 다운링크 → 스피커)을
제공하며, 네이티브 C 스타일(절차적, 명시적 버퍼, 최소 추상화)로 구현했습니다.

---

## 채널 처리: 온디바이스 모노 다운믹스 (핵심 발견)

> **결론: Azure VoiceLive Realtime API 는 PCM16 모노만 허용합니다.**  
> 따라서 **디바이스가 전송 전에 모노로 다운믹스하며, 와이어 포맷은 항상 모노입니다.**
> 기본값은 `--channels 1`(모노)이고, `--channels 2`는 2-마이크 어레이에서 캡처한 뒤
> **업링크 직전에 로컬에서 모노로 다운믹스**합니다.

### 조사 과정

`devicesim/ms_ref/foundry_LV_Csharp_ref.cs` 및 Azure VoiceLive SDK/문서, 그리고
배포 백엔드(`assistant/voice_handler.py`, `app.py`)를 검토한 결과:

| 항목 | 값 |
|------|----|
| VoiceLive Realtime API 입력 포맷 | `pcm16` (채널 수 파라미터 없음) |
| `input_audio_buffer.append` | base64 인코딩된 PCM16 **모노** 버퍼만 수신 |
| 백엔드 `/session` 처리 | 디바이스 바이트를 **그대로(verbatim)** VoiceLive 로 전달 (채널 파싱/다운믹스 없음) |
| 멀티채널 빔포밍 지원 수준 | Azure Speech SDK `MicrophoneArrayGeometry` 레벨 (Realtime API 레벨 아님) |

배포 백엔드 `assistant/app.py` `/session` 엔드포인트는 디바이스가 보낸 바이너리를
**그대로 VoiceLive 로 전달**하며 채널을 해석하지 않습니다. 따라서 인터리브드 2채널
PCM16 을 그대로 보내면 모노 디코더가 샘플을 오해석해 오디오가 깨집니다
(피치/속도 왜곡). **정답은 디바이스가 모노로 보내는 것**입니다.

### 실제 타깃 디바이스(ThinQ ON) 관점

ThinQ ON 은 하드웨어 AEC · 빔포밍 · 원거리(far-field) 인식을 온디바이스에서 수행합니다.
즉, 노이즈 억제를 위해 2채널을 서버로 보낼 이유가 없습니다 — 잡음 제거는 이미 디바이스에서
끝나며, VoiceLive 는 두 번째 채널을 활용하지 못합니다. 따라서 **ThinQ ON 은 모노 업링크가 정답**입니다.
(자세한 근거와 비교표는 `devicesim/docs` PPTX 슬라이드 참조.)

### 구현 방식: 클라이언트(온디바이스) 다운믹스

```
C# 디바이스 (2× MEMS)                       →  wss://.../session  →  assistant/app.py  →  VoiceLive
  캡처 2ch → Downmix.ToMono() → 모노 업링크       (모노 PCM16)         (verbatim 전달)      (PCM16 모노)
```

- 기본값 `--channels 1`: 단일 마이크 모노 캡처 → 모노 전송 (브라우저/Python 클라이언트와 동일 계약)
- `--channels 2`: 2-마이크 어레이 캡처(`Stereo` 모드) → `WsSession.UplinkLoop` 에서
  `Downmix.ToMono()` 로 **모노 다운믹스 후 전송**
- 와이어 포맷은 항상 모노이므로 `?channels=2` 쿼리 파라미터는 **사용하지 않습니다**
  (백엔드가 무시하며, 모노를 보내면서 2채널이라 표기하는 것은 오해의 소지가 있음)
- 백엔드 변경 불필요 — 배포된 `assistant/app.py` 를 그대로 사용

---

## 오디오 백엔드 계층

```
┌──────────────────────────────────────────────┐
│  IAudioCapture / IAudioPlayer (인터페이스)     │
│                                               │
│  장치 백엔드 (기본)                             │
│  ├─ Linux/ARM:  AlsaCapture / AlsaPlayer      │
│  │              (libasound.so.2 P/Invoke)     │
│  ├─ Windows:    WasapiCapture / WasapiPlayer  │
│  │              (NAudio WASAPI/WaveOut)       │
│  ├─ macOS:      CoreAudioCapture/CoreAudioPlayer│
│  │              (AudioToolbox AudioQueue)     │
│  └─ CI/no-HW:  SyntheticCapture / SilentPlayer│
│                (440Hz 사인파 / 무음 로그)      │
│                                               │
│  파일 백엔드 (--audio-source / --audio-out)    │
│  ├─ FileCapture: PCM/WAV 파일 → 마이크 소스   │
│  └─ FilePlayer:  다운링크 → WAV 파일 저장     │
└──────────────────────────────────────────────┘
```

---

## 빌드

```bash
cd devicesim/csharp
dotnet build
```

ARM64 Linux 크로스 컴파일:

```bash
dotnet publish -c Release -r linux-arm64 --self-contained true -o ./out-arm64
```

실제 ARM 장치에서는 libasound 가 필요합니다:

```bash
# Debian/Ubuntu (ARM)
sudo apt install libasound2
# Buildroot/Yocto: alsa-lib 패키지 포함
```

---

## 실행

```bash
# 기본 (모노, 무한 실행)
dotnet run -- --url https://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io

# 두 번째 프로덕션 타겟
dotnet run -- --url https://kukovm2.koreacentral.cloudapp.azure.com

# 2-마이크 어레이 캡처 (전송 전 모노로 다운믹스)
dotnet run -- --url https://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io --channels 2

# 30초 후 자동 종료
dotnet run -- --url https://localhost:8000 --duration 30

# 파일 소스로 연결 (CI/스모크 테스트)
dotnet run -- --url https://localhost:8000 --audio-source mic.wav --audio-out received.wav
```

### 옵션

| 옵션 | 기본값 | 설명 |
|------|--------|------|
| `--url` | *(필수)* | 연결할 서버 URL |
| `--channels` | `1` | 캡처 채널 수 (1=모노 기본, 2=2-마이크 어레이 캡처 후 모노 다운믹스). 와이어 포맷은 항상 모노 |
| `--duration` | `0` | 실행 시간(초). 0=Ctrl+C 까지 |
| `--device` | `-1` | 마이크 장치 인덱스 (-1=기본 장치) |
| `--audio-source` | *(없음)* | PCM16/WAV 파일을 마이크 소스로 사용 |
| `--audio-out` | *(없음)* | 다운링크 오디오를 WAV 파일로 저장 |
| `--insecure` | `false` | 자체 서명 인증서 허용 (예: kukovm2:5173) |
| `--allow-barge-in` | `false` | 에코 억제 비활성화(풀듀플렉스). 기본은 억제 활성 — 하드웨어 AEC 없는 PoC 에서 스피커 재생 중 마이크를 억제해 에코 바지인 루프 방지 |

> **에코 억제(반이중)**: 이 PoC 는 하드웨어 AEC 가 없어 마이크가 자기 스피커
> 출력을 다시 잡으면 서버 VAD 가 사용자 발화로 오인해 에이전트 음성을 끊는
> 에코 루프가 발생합니다. 기본적으로 스피커 재생 중(+0.25s hangover) 마이크
> 업링크를 억제합니다. 실제 ThinQ 실리콘에는 AEC 가 있으므로, AEC 가 있는
> 환경에서만 `--allow-barge-in` 으로 풀듀플렉스를 사용하세요.

---

## 오디오 폴백 우선순위

| 환경 | 캡처 | 재생 |
|------|------|------|
| Linux/ARM (ALSA 장치 있음) | `AlsaCapture` (2ch MEMS) | `AlsaPlayer` |
| Linux/ARM (ALSA 장치 없음) | 합성 사인파 (440Hz) | `SilentPlayer` (로그) |
| Windows (NAudio 장치 있음) | `WasapiCapture` (2ch) | `WasapiPlayer` |
| Windows (장치 없음) | 합성 사인파 | `SilentPlayer` |
| macOS (CoreAudio) | `CoreAudioCapture` (2ch→모노 폴백) | `CoreAudioPlayer` |
| CI / `--audio-source` | `FileCapture` (PCM/WAV) | `FilePlayer` (WAV 저장) |

> **플랫폼 자동 감지**: `RuntimeInformation.IsOSPlatform` 으로 Linux/Windows/macOS 를
> 판별해 각각 ALSA / WASAPI / CoreAudio(AudioToolbox AudioQueue) 백엔드를 선택합니다.
> 세 플랫폼 모두 실제 마이크·스피커 하드웨어로 캡처·재생합니다.
> 테스트/CI 에서 합성 폴백을 강제하려면 `DEVICESIM_FORCE_SYNTHETIC=1` 을 사용하세요.

---

## 테스트

```bash
cd devicesim/csharp/Tests
dotnet test
```

테스트 항목:
- `DownmixTests`: PCM16 스테레오→모노 다운믹스 (8개 케이스)
- `UplinkMonoTests`: 업링크 모노 정규화 — `Downmix.ToMono()` 채널별 동작 + `WsSession.SourceChannels()` 모드→채널 매핑 (7개 케이스)
- `AudioCaptureTests`: 초기화 폴백 로직, 합성 모드 청크 생성 (6개 케이스)
- `EchoGateTests`: 반이중 에코 억제 게이트 (3개 케이스)

---

## 아키텍처

```
┌─────────────────────────────────────────┐
│           devicesim/csharp              │
│                                         │
│  IAudioCapture (인터페이스)              │
│  ├─ AudioCapture (장치 백엔드 선택자)    │
│  │   ├─ AlsaCapture (Linux/ARM)         │
│  │   ├─ WasapiCapture (Windows)        │
│  │   └─ 합성 사인파 폴백 (CI)           │
│  └─ FileCapture (파일 소스)             │
│                                         │
│  WsSession                              │
│  ├─ 업링크: 2ch 캡처 → Downmix.ToMono() │
│  │         → 모노 PCM16 → WebSocket      │
│  │         wss://.../session (모노)      │
│  └─ 다운링크: JSON 이벤트 로그          │
│             바이너리 PCM16 → 스피커      │
│                                         │
│  IAudioPlayer (인터페이스)               │
│  ├─ AudioPlayer (장치 백엔드 선택자)    │
│  │   ├─ AlsaPlayer (Linux/ARM)          │
│  │   ├─ WasapiPlayer (Windows)         │
│  │   └─ SilentPlayer 폴백 (CI)         │
│  └─ FilePlayer (WAV 파일 저장)          │
│                                         │
│  Downmix.ToMono() / StereoToMono() 유틸 │
└─────────────────────────────────────────┘
          │ wss://.../session (모노 PCM16)
          ▼
┌─────────────────────────────────────────┐
│  backend/src/assistant/app.py           │
│  /session: 디바이스 바이트를 그대로     │
│            VoiceLive 로 전달 (verbatim) │
│           │                             │
│           ▼                             │
│  Azure VoiceLive (PCM16 모노만 허용)    │
└─────────────────────────────────────────┘
```

---

## 스모크 테스트

### 하드웨어 장치 (ARM/Windows)

```bash
# 타겟 1: Azure Container Apps
dotnet run -- \
  --url https://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io \
  --duration 15

# 타겟 2: Azure VM (kukovm2)
dotnet run -- \
  --url https://kukovm2.koreacentral.cloudapp.azure.com \
  --duration 15
```

### CI / 파일 기반 (headless)

```bash
# WAV 파일을 마이크 소스로, 다운링크를 WAV 파일로 저장
dotnet run -- \
  --url https://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io \
  --audio-source tests/sample_24k_mono.wav \
  --audio-out /tmp/received.wav \
  --duration 15
```

예상 출력:
```
[설정] 요청 채널=2, URL=https://...
[ALSA 캡처] 2채널 PCM16/24kHz (2-마이크 어레이)  ← ARM 하드웨어 (업링크 전 모노 다운믹스)
[파일 캡처] 소스: tests/sample.wav (2ch)   ← 파일 소스
[캡처] 합성 오디오 모드 (하드웨어 없음)    ← CI
[세션] 연결 중: wss://.../session          ← 와이어는 항상 모노 PCM16
[세션] 연결 완료 – 스트리밍 시작
[이벤트] appliance_snapshot 수신 – 기기 상태 초기화
[상태] listening
[전사] [user] ...
[전사] [agent] ...
[파일 재생] WAV 저장 완료: /tmp/received.wav (X.Xs, YYYY bytes)
[종료] 세션 정상 종료
```

---

## ARM 포팅 가이드

| 컴포넌트 | 현재 (Windows 개발) | ARM Linux 타겟 |
|----------|---------------------|----------------|
| 마이크 캡처 | `WasapiCapture` (NAudio) | **`AlsaCapture`** (libasound P/Invoke) |
| 스피커 재생 | `WasapiPlayer` (NAudio) | **`AlsaPlayer`** (libasound P/Invoke) |
| WebSocket | `ClientWebSocket` | 동일 (cross-platform) |
| 다운믹스 | `Downmix.StereoToMono()` | 동일 (pure C#) |

크로스 컴파일:

```bash
# ARM64 Linux (자기 포함)
dotnet publish -c Release -r linux-arm64 --self-contained true

# ARM32 Linux (Cortex-A53 32-bit)
dotnet publish -c Release -r linux-arm --self-contained true
```

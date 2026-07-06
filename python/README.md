# LGE Voice Live — Python CLI 디바이스 에뮬레이터

헤드리스 Python CLI 디바이스 에뮬레이터로, 마이크와 스피커만 갖춘 IoT 기기처럼 동작합니다.

- **업링크**: 마이크 오디오를 PCM16/24kHz/모노 이진 프레임으로 백엔드에 스트리밍
- **다운링크**: 서버 오디오를 스피커로 저지연 연속 재생
- **텍스트 이벤트**: `status`, `transcript`, `appliance_snapshot/update`, `audio_clear` 로그 출력
- **바지인**: `audio_clear` 수신 시 즉시 재생 중단

## 요구 사항

- Python 3.11+
- [uv](https://docs.astral.sh/uv/) 패키지 매니저
- PortAudio (sounddevice 의존성)
  - macOS: `brew install portaudio`
  - Linux: `sudo apt-get install libportaudio2`
  - Windows: wheel에 포함되어 별도 설치 불필요

## 설치

```bash
cd devicesim/python
uv sync
```

## 실행

```bash
# Azure Container Apps 프로덕션 엔드포인트 (표준 TLS)
uv run device-emulator --host https://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io

# Azure VM 프로덕션 엔드포인트 (포트 5173, 자체 서명 인증서)
uv run device-emulator --host https://kukovm2.koreacentral.cloudapp.azure.com:5173 --insecure

# 로컬 개발 서버 (HTTP)
uv run device-emulator --host http://localhost:8000

# 디버그 로그 활성화
uv run device-emulator --host https://my-server.example.com --log-level DEBUG

# 헤드리스/CI 모드 (마이크·스피커 없이 WAV 파일 사용)
uv run device-emulator --host https://my-server.example.com \
    --audio-source fixtures/hello.wav --audio-out /tmp/response.wav
```

### CLI 옵션

| 옵션 | 설명 | 기본값 |
|------|------|--------|
| `--host URL` | 백엔드 서버 URL **(필수)** | — |
| `--log-level LEVEL` | 로그 레벨 (DEBUG/INFO/WARNING/ERROR) | INFO |
| `--audio-source FILE` | 마이크 대신 사용할 WAV 파일 (헤드리스/CI 모드) | — |
| `--audio-out FILE` | 수신 오디오를 저장할 WAV 파일 (헤드리스/CI 모드) | — |
| `--insecure` | TLS 인증서 검증 비활성화 (자체 서명 인증서 서버 전용) | 비활성 |

URL 스킴 변환이 자동으로 처리됩니다:
- `https://` → `wss://`
- `http://` → `ws://`
- 스킴 없는 호스트명 → `wss://` 기본 적용
- `/session` 경로 자동 추가
- 커스텀 포트(`host:port`)는 변환 후에도 그대로 유지됨

> **참고**: kukovm2 VM은 포트 443/80이 NSG에 의해 차단되어 있으며,
> 포트 **5173**에서 자체 서명 HTTPS 인증서로 서비스됩니다.
> 접속 시 `--insecure` 플래그가 필요합니다.
> ACA 엔드포인트는 표준 TLS로 검증되며 플래그 없이 연결됩니다.

## 종료

`Ctrl+C` 또는 `SIGTERM` 시그널로 안전하게 종료됩니다.
마이크 캡처 스트림과 오디오 재생 스트림이 정상적으로 닫힙니다.

## 테스트

```bash
cd devicesim/python
uv run pytest tests/ -v
```

### 테스트 구성

| 파일 | 범위 | 테스트 수 |
|------|------|----------|
| `test_protocol.py` | JSON 메시지 파싱/포맷 | 21개 |
| `test_audio_codec.py` | PCM16 ↔ float32 변환 | 17개 |
| `test_player.py` | AudioPlayer 큐 / 콜백 로직 | 12개 |
| `test_session.py` | URL 변환 / 세션 메시지 처리 / TLS 설정 | 26개 |
| `test_file_backends.py` | WAV 파일 백엔드 (캡처/재생) | 11개 |

모든 테스트는 PortAudio 하드웨어 없이 실행되며, sounddevice는 mock으로 대체됩니다.

## WebSocket 프로토콜

서버는 `wss://<host>/session`에서 이진 프레이밍으로 연결을 수락합니다.

### 서버 → 클라이언트

| 타입 | 포맷 | 처리 |
|------|------|------|
| 이진 프레임 | PCM16/24kHz/모노 raw bytes | 스피커 재생 |
| `appliance_snapshot` | JSON 텍스트 | 로그 출력 |
| `appliance_update` | JSON 텍스트 | 로그 출력 |
| `transcript` | JSON 텍스트 | 로그 출력 |
| `status` | JSON 텍스트 | 로그 출력 |
| `audio_clear` | JSON 텍스트 | 재생 즉시 중단 |

### 클라이언트 → 서버

| 포맷 | 내용 |
|------|------|
| 이진 프레임 | PCM16/24kHz/모노 마이크 오디오 |

## 구조

```
devicesim/python/
├── pyproject.toml              # uv 프로젝트 설정
├── README.md                   # 이 파일
├── src/
│   └── device_emulator/
│       ├── __init__.py
│       ├── protocol.py         # 서버 메시지 파싱/로깅
│       ├── audio.py            # MicCapture + AudioPlayer (파일 백엔드 포함)
│       ├── session.py          # WebSocket 세션 관리, TLS 설정
│       └── main.py             # CLI 진입점
└── tests/
    ├── test_protocol.py        # 프로토콜 단위 테스트
    ├── test_audio_codec.py     # PCM16 코덱 단위 테스트
    ├── test_player.py          # 재생 큐 단위 테스트
    ├── test_session.py         # 세션 통합 테스트 (URL, TLS)
    └── test_file_backends.py   # WAV 파일 백엔드 단위 테스트
```

## 스모크 테스트 대상 서버

| 서버 | URL | TLS |
|------|-----|-----|
| Azure Container Apps | `https://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io` | 표준 (검증 활성화) |
| Azure VM | `https://kukovm2.koreacentral.cloudapp.azure.com:5173` | 자체 서명 (`--insecure` 필요) |

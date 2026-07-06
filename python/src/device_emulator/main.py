"""
main.py — CLI 진입점 모듈

`uv run device-emulator` 또는 `python -m device_emulator.main` 으로 실행한다.

지원하는 CLI 옵션:
  --host         : 백엔드 서버 URL (예: https://my-server.example.com)
  --log-level    : 로그 레벨 (기본값: INFO)
  --audio-source : 마이크 대신 사용할 WAV 파일 경로 (헤드리스/CI 모드)
  --audio-out    : 수신된 오디오를 저장할 WAV 파일 경로 (헤드리스/CI 모드)
  --insecure     : TLS 인증서 검증 비활성화 (자체 서명 인증서 사용 서버용)

사용 예 (실제 마이크/스피커):
  uv run device-emulator --host https://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io
  uv run device-emulator --host https://kukovm2.koreacentral.cloudapp.azure.com:5173 --insecure

사용 예 (헤드리스 CI 모드):
  uv run device-emulator --host https://my-server.example.com \\
      --audio-source fixtures/hello.wav --audio-out /tmp/response.wav
"""

from __future__ import annotations

import asyncio
import logging
import signal
import sys

import click

from .audio import (
    AudioPlayer,
    MicCapture,
    SounddeviceCaptureBackend,
    SounddevicePlaybackBackend,
    WavCaptureBackend,
    WavPlaybackBackend,
)
from .session import DeviceSession, _make_insecure_ssl_context

# ──────────────────────────────────────────────
# 프로덕션 대상 서버 URL (스모크 테스트 용도)
# ──────────────────────────────────────────────

# Azure Container Apps 기반 프로덕션 엔드포인트
PROD_URL_ACA = "https://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io"

# Azure VM 기반 프로덕션 엔드포인트
PROD_URL_VM = "https://kukovm2.koreacentral.cloudapp.azure.com"


def _configure_logging(log_level: str) -> None:
    """로그 레벨 및 포맷을 설정한다.

    콘솔(표준 출력)에 타임스탬프와 로거 이름을 포함한 로그를 출력한다.

    Args:
        log_level: 로그 레벨 문자열 (DEBUG, INFO, WARNING, ERROR, CRITICAL)
    """
    level = getattr(logging, log_level.upper(), logging.INFO)
    logging.basicConfig(
        level=level,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        datefmt="%Y-%m-%dT%H:%M:%S",
        stream=sys.stderr,
    )


def _install_signal_handlers(loop: asyncio.AbstractEventLoop) -> None:
    """Ctrl+C (SIGINT) 및 SIGTERM 시그널 핸들러를 설치한다.

    시그널 수신 시 이벤트 루프를 정상적으로 종료하여
    마이크 캡처 및 재생 스트림이 올바르게 닫히도록 한다.

    Args:
        loop: 시그널 핸들러를 설치할 asyncio 이벤트 루프
    """

    def _stop(signame: str) -> None:
        # 시그널 수신 시 이벤트 루프 종료 요청
        logging.getLogger("device_emulator.main").info("시그널 수신 (%s): 종료 중...", signame)
        loop.stop()

    # SIGINT (Ctrl+C)와 SIGTERM 모두 처리
    for sig_name in ("SIGINT", "SIGTERM"):
        sig = getattr(signal, sig_name, None)
        if sig is not None:
            try:
                loop.add_signal_handler(sig, _stop, sig_name)
            except (NotImplementedError, ValueError):
                # Windows에서는 add_signal_handler가 미지원이므로 무시
                pass


@click.command()
@click.option(
    "--host",
    required=True,
    metavar="URL",
    help=(
        "백엔드 서버 URL (예: https://my-server.example.com). "
        f"프로덕션 대상: {PROD_URL_ACA} 또는 {PROD_URL_VM}"
    ),
)
@click.option(
    "--log-level",
    default="INFO",
    show_default=True,
    type=click.Choice(["DEBUG", "INFO", "WARNING", "ERROR"], case_sensitive=False),
    help="로그 레벨 설정",
)
@click.option(
    "--audio-source",
    default=None,
    type=click.Path(exists=True, dir_okay=False, readable=True),
    metavar="FILE",
    help=(
        "마이크 대신 WAV 파일을 오디오 소스로 사용 (헤드리스/CI 모드). "
        "지정하지 않으면 실제 마이크를 사용한다."
    ),
)
@click.option(
    "--audio-out",
    default=None,
    type=click.Path(dir_okay=False, writable=True, allow_dash=False),
    metavar="FILE",
    help=(
        "수신된 다운링크 오디오를 WAV 파일로 저장 (헤드리스/CI 모드). "
        "지정하지 않으면 실제 스피커로 재생한다."
    ),
)
@click.option(
    "--insecure",
    is_flag=True,
    default=False,
    help=(
        "TLS 인증서 검증을 비활성화한다 (자체 서명 인증서 사용 서버 전용). "
        "기본값은 검증 활성화(보안). 개발/테스트 서버에서만 사용할 것."
    ),
)
@click.option(
    "--allow-barge-in",
    is_flag=True,
    default=False,
    help=(
        "에코 억제(반이중)를 비활성화하여 재생 중에도 마이크 업링크를 계속 전송한다. "
        "기본값은 에코 억제 활성화: 스피커 재생 중 마이크를 억제하여, 장치가 자기 "
        "스피커 출력을 다시 잡아 서버 VAD가 바지인으로 오인하고 재생을 끊는 에코 "
        "루프를 방지한다. 하드웨어 AEC가 있는 환경에서만 이 플래그 사용을 권장한다."
    ),
)
def main(
    host: str,
    log_level: str,
    audio_source: str | None,
    audio_out: str | None,
    insecure: bool,
    allow_barge_in: bool,
) -> None:
    """LGE Voice Live 헤드리스 Python CLI 디바이스 에뮬레이터.

    지정된 서버에 WebSocket으로 연결하여 마이크 오디오를 스트리밍하고,
    서버로부터 수신한 오디오를 스피커로 재생한다.

    --audio-source / --audio-out 옵션을 사용하면 마이크·스피커 없이
    WAV 파일로 송수신 오디오를 대체할 수 있다 (헤드리스/CI 모드).

    종료: Ctrl+C

    Args:
        host: 백엔드 서버 URL
        log_level: 로그 레벨 (DEBUG/INFO/WARNING/ERROR)
        audio_source: 마이크 대신 사용할 WAV 파일 경로 (None이면 실제 마이크)
        audio_out: 수신 오디오를 저장할 WAV 파일 경로 (None이면 실제 스피커)
        insecure: True이면 TLS 인증서 검증 비활성화
    """
    # 로그 설정 초기화
    _configure_logging(log_level)
    log = logging.getLogger("device_emulator.main")

    log.info("디바이스 에뮬레이터 시작: host=%s", host)

    # TLS 설정: --insecure 지정 시 자체 서명 인증서 허용
    ssl_context = None
    if insecure:
        log.warning("TLS 인증서 검증 비활성화 (--insecure): 개발/자체 서명 서버 전용")
        ssl_context = _make_insecure_ssl_context()

    # 캡처 백엔드 선택: WAV 파일 또는 실제 마이크
    if audio_source:
        log.info("캡처 백엔드: WAV 파일 (%s)", audio_source)
        capture_backend = WavCaptureBackend(audio_source)
    else:
        log.info("캡처 백엔드: 실제 마이크 (sounddevice)")
        capture_backend = SounddeviceCaptureBackend()

    # 재생 백엔드 선택: WAV 파일 저장 또는 실제 스피커
    if audio_out:
        log.info("재생 백엔드: WAV 파일 (%s)", audio_out)
        playback_backend = WavPlaybackBackend(audio_out)
    else:
        log.info("재생 백엔드: 실제 스피커 (sounddevice)")
        playback_backend = SounddevicePlaybackBackend()

    # MicCapture / AudioPlayer에 백엔드 주입
    mic = MicCapture(backend=capture_backend)
    player = AudioPlayer(backend=playback_backend)

    # DeviceSession 생성 (ssl_context: None이면 기본 TLS 검증 사용)
    # echo_suppression: 기본 활성화; --allow-barge-in 지정 시 비활성화(풀듀플렉스)
    if allow_barge_in:
        log.info("에코 억제 비활성화 (--allow-barge-in): 풀듀플렉스 모드 (AEC 필요)")
    session = DeviceSession(
        host,
        player=player,
        mic=mic,
        ssl_context=ssl_context,
        echo_suppression=not allow_barge_in,
    )

    # asyncio 이벤트 루프에서 세션 실행
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    _install_signal_handlers(loop)

    try:
        loop.run_until_complete(session.run())
    except KeyboardInterrupt:
        log.info("Ctrl+C 수신: 종료")
    finally:
        loop.close()
        log.info("디바이스 에뮬레이터 종료")


if __name__ == "__main__":
    main()

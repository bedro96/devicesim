"""
session.py — WebSocket 세션 관리 모듈

백엔드 서버의 /session WebSocket 엔드포인트에 연결하여:
  1. 연결 즉시 마이크 캡처 시작 및 PCM16 업링크 스트리밍
  2. 서버로부터 수신한 이진 PCM16 데이터를 AudioPlayer로 전달
  3. 텍스트 JSON 메시지를 파싱하여 로그 출력
  4. audio_clear 메시지 수신 시 AudioPlayer.clear() 호출

WebSocket 연결은 재시도 없이 단순 연결/종료로 구현한다.
"""

from __future__ import annotations

import asyncio
import logging
import ssl
from typing import Any

from .audio import AudioPlayer, EchoGate, MicCapture, UplinkMeter
from .protocol import MSG_AUDIO_CLEAR, log_server_message, parse_server_message

log = logging.getLogger("device_emulator.session")


def _build_wss_url(host: str) -> str:
    """호스트 URL을 WebSocket WSS URL로 변환한다.

    입력이 이미 ws:// 또는 wss://로 시작하면 그대로 사용하고,
    https://로 시작하면 wss://로, http://로 시작하면 ws://로 변환한다.
    스킴이 없으면 wss://를 기본으로 붙인다.
    경로에 /session이 없으면 자동으로 추가한다.

    Args:
        host: 호스트 URL 또는 호스트명 문자열

    Returns:
        /session 경로가 포함된 WebSocket URL
    """
    # 스킴 변환: https → wss, http → ws
    if host.startswith("https://"):
        url = "wss://" + host[len("https://"):]
    elif host.startswith("http://"):
        url = "ws://" + host[len("http://"):]
    elif host.startswith("wss://") or host.startswith("ws://"):
        url = host
    else:
        # 스킴이 없으면 wss:// 기본 설정
        url = "wss://" + host

    # /session 경로가 없으면 추가 (슬래시 중복 방지)
    if not url.rstrip("/").endswith("/session"):
        url = url.rstrip("/") + "/session"

    return url


def _make_insecure_ssl_context() -> ssl.SSLContext:
    """TLS 인증서 검증을 비활성화한 SSLContext를 생성한다.

    자체 서명 인증서를 사용하는 개발/테스트 서버에 연결할 때 사용한다.
    프로덕션 환경에서는 사용하지 않는다.

    Returns:
        check_hostname=False, verify_mode=CERT_NONE으로 설정된 SSLContext
    """
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    # 자체 서명 인증서 허용: 호스트명 검증 및 인증서 검증 비활성화
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    return ctx


class DeviceSession:
    """백엔드 WebSocket 세션을 관리하는 핵심 클래스.

    연결 즉시 마이크 캡처와 업링크 스트리밍을 시작하고,
    서버로부터 수신한 오디오를 스피커로 재생한다.

    사용 예:
        session = DeviceSession("https://my-server.example.com")
        await session.run()
    """

    def __init__(
        self,
        host: str,
        player: AudioPlayer | None = None,
        mic: MicCapture | None = None,
        ws_factory: Any | None = None,
        ssl_context: ssl.SSLContext | None = None,
        echo_suppression: bool = True,
    ) -> None:
        """DeviceSession 초기화.

        Args:
            host: 백엔드 서버 URL 또는 호스트명
            player: 오디오 재생기 (None이면 새로 생성)
            mic: 마이크 캡처기 (None이면 새로 생성)
            ws_factory: WebSocket 연결 팩토리 (테스트용 주입)
            ssl_context: TLS 설정 (None이면 기본 검증 사용; --insecure 시 CERT_NONE 컨텍스트)
            echo_suppression: True(기본)이면 스피커 재생 중 마이크 업링크를 억제하여
                              음향 에코로 인한 바지인 루프를 방지한다(반이중).
                              False이면 항상 업링크를 전송한다(풀듀플렉스, AEC 필요).
        """
        self._url = _build_wss_url(host)
        # 플레이어와 마이크를 외부에서 주입하거나 기본값으로 생성
        self._player = player or AudioPlayer()
        self._mic = mic or MicCapture()
        self._ws_factory = ws_factory
        # TLS 검증 설정: None이면 websockets 기본값(검증 활성화) 사용
        self._ssl_context = ssl_context
        # 에코 억제(반이중) 활성화 여부
        self._echo_suppression = echo_suppression
        log.info("DeviceSession 초기화: %s", self._url)

    async def run(self) -> None:
        """WebSocket에 연결하고 세션을 실행한다.

        연결 즉시 마이크 캡처를 시작하고,
        업링크(마이크→서버) 및 다운링크(서버→스피커) 루프를 병렬로 실행한다.
        Ctrl+C 또는 연결 종료 시 정상적으로 종료된다.
        """
        import websockets

        ws_connect = self._ws_factory or websockets.connect

        log.info("WebSocket 연결 시도: %s", self._url)
        try:
            # ssl_context가 지정된 경우 TLS 검증 설정을 재정의하여 연결
            connect_kwargs: dict[str, Any] = {}
            if self._ssl_context is not None:
                connect_kwargs["ssl"] = self._ssl_context
            async with ws_connect(self._url, **connect_kwargs) as ws:
                log.info("WebSocket 연결 성공: %s", self._url)
                await self._handle_connection(ws)
        except Exception:
            log.exception("WebSocket 세션 오류")
        finally:
            # 세션 종료 시 반드시 마이크 캡처와 재생 스트림을 닫음
            self._mic.stop()
            self._player.close()
            log.info("세션 종료: 마이크 및 재생 스트림 닫힘")

    async def _handle_connection(self, ws: Any) -> None:
        """WebSocket 연결이 열린 후 업링크/다운링크 루프를 실행한다.

        마이크 캡처 콜백에서 생성된 PCM16 바이트를 WebSocket으로 전송하고,
        WebSocket으로 수신된 이진/텍스트 데이터를 처리한다.

        Args:
            ws: 열린 websockets.WebSocketClientProtocol 인스턴스
        """
        # 마이크 캡처 데이터를 비동기 큐로 전달하기 위한 큐
        audio_queue: asyncio.Queue[bytes] = asyncio.Queue()

        # 실행 중인 이벤트 루프를 미리 캡처한다.
        # 마이크 콜백은 sounddevice 오디오 스레드(비메인 스레드)에서 호출되는데,
        # 그 스레드에는 "현재 이벤트 루프"가 없어 asyncio.get_event_loop()가
        # RuntimeError를 던진다. 여기(루프 내부)에서 참조를 잡아 콜백에 넘겨야
        # 스레드 안전하게 큐로 오디오를 전달할 수 있다.
        loop = asyncio.get_running_loop()

        def _mic_callback(pcm_data: bytes) -> None:
            """마이크 콜백: PCM16 바이트를 비동기 큐에 넣는다.

            sounddevice 오디오 스레드에서 호출되므로, 미리 캡처해 둔 이벤트 루프의
            call_soon_threadsafe를 사용하여 thread-safe하게 큐에 넣는다.

            Args:
                pcm_data: 캡처된 PCM16 바이트 데이터
            """
            try:
                loop.call_soon_threadsafe(audio_queue.put_nowait, pcm_data)
            except RuntimeError:
                # 이벤트 루프가 이미 종료된 경우 무시
                pass

        # 마이크 캡처 시작 (연결 즉시 스트리밍 개시)
        self._mic.start(_mic_callback)
        log.info("마이크 캡처 시작: 업링크 스트리밍 개시")

        async def _uplink_loop() -> None:
            """업링크 루프: 마이크 큐에서 PCM16 데이터를 꺼내 WebSocket으로 전송한다.

            큐에서 데이터를 가져오는 즉시 이진 WebSocket 프레임으로 전송.
            세션 전체 기간 동안 계속 실행된다.
            UplinkMeter로 주기적으로 전송 바이트/오디오 레벨을 로그에 남겨
            실제로 마이크 오디오가 서버로 가고 있는지 확인할 수 있게 한다.
            """
            meter = UplinkMeter()
            # 에코 억제 게이트(반이중): 스피커 재생 중 마이크 업링크를 억제
            gate = EchoGate() if self._echo_suppression else None
            suppressing = False
            while True:
                pcm_data = await audio_queue.get()
                # 반이중 에코 억제: 스피커 재생 중(+행오버)에는 업링크 드롭
                if gate is not None and not gate.should_send(
                    self._player.is_playing(), loop.time()
                ):
                    if not suppressing:
                        log.info("[에코 억제] 스피커 재생 중 — 마이크 업링크 일시 중단")
                        suppressing = True
                    continue
                if suppressing:
                    log.info("[에코 억제] 재생 종료 — 마이크 업링크 재개")
                    suppressing = False
                try:
                    await ws.send(pcm_data)
                    # 업링크 관측: 주기적으로 레벨/바이트 요약, 무음이면 경고
                    summary = meter.observe(pcm_data)
                    if summary is not None:
                        log.info(summary)
                except Exception:
                    log.exception("업링크 전송 오류")
                    break

        async def _downlink_loop() -> None:
            """다운링크 루프: WebSocket으로부터 데이터를 수신하여 처리한다.

            이진 프레임: PCM16 오디오 → AudioPlayer.play() 호출
            텍스트 프레임: JSON 파싱 → 로그 출력, audio_clear 처리
            """
            async for message in ws:
                if isinstance(message, bytes):
                    # 이진 프레임: PCM16 오디오 데이터 → 스피커 재생
                    self._player.play(message)
                elif isinstance(message, str):
                    # 텍스트 프레임: JSON 메시지 파싱 및 처리
                    self._handle_text_message(message)

        # 업링크와 다운링크를 병렬로 실행
        # 어느 하나가 종료되면 나머지도 취소됨
        done, pending = await asyncio.wait(
            [asyncio.create_task(_uplink_loop()), asyncio.create_task(_downlink_loop())],
            return_when=asyncio.FIRST_COMPLETED,
        )
        # 미완료 태스크 취소
        for task in pending:
            task.cancel()

    def _handle_text_message(self, text: str) -> None:
        """텍스트 JSON 메시지를 처리한다.

        메시지를 파싱하고 로그에 기록한다.
        audio_clear 메시지의 경우 AudioPlayer.clear()를 추가로 호출한다.

        Args:
            text: WebSocket 텍스트 프레임의 원시 JSON 문자열
        """
        msg = parse_server_message(text)
        if msg is None:
            return

        # 모든 메시지 타입을 로그에 기록
        log_server_message(msg)

        # audio_clear 메시지: 현재 재생 중인 오디오 즉시 중단
        if msg.get("type") == MSG_AUDIO_CLEAR:
            self._player.clear()
            log.info("audio_clear 처리: 재생 중단")

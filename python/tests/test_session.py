"""
test_session.py — DeviceSession 세션 처리 단위 테스트

session.py의 URL 변환, 텍스트 메시지 처리, 오디오 재생/클리어 로직을 검증한다.
실제 WebSocket 연결 없이 mock을 사용하여 격리 테스트한다.
"""

from __future__ import annotations

import asyncio
import json
from collections import deque
from contextlib import asynccontextmanager
from typing import Any, AsyncIterator
from unittest.mock import AsyncMock, MagicMock, patch

import ssl

import numpy as np
import pytest

from device_emulator.audio import AudioPlayer, MicCapture
from device_emulator.session import DeviceSession, _build_wss_url, _make_insecure_ssl_context


# ──────────────────────────────────────────────
# URL 변환 테스트
# ──────────────────────────────────────────────


class TestBuildWssUrl:
    """_build_wss_url() URL 변환 함수를 검증하는 테스트 클래스."""

    def test_https_to_wss_변환(self) -> None:
        """https:// URL이 wss://로 변환되는지 확인한다."""
        url = _build_wss_url("https://my-server.example.com")
        assert url.startswith("wss://")
        assert "/session" in url

    def test_http_to_ws_변환(self) -> None:
        """http:// URL이 ws://로 변환되는지 확인한다."""
        url = _build_wss_url("http://localhost:8000")
        assert url.startswith("ws://")

    def test_wss_그대로_유지(self) -> None:
        """wss://로 시작하는 URL은 그대로 유지되는지 확인한다."""
        url = _build_wss_url("wss://my-server.example.com")
        assert url.startswith("wss://")

    def test_ws_그대로_유지(self) -> None:
        """ws://로 시작하는 URL은 그대로 유지되는지 확인한다."""
        url = _build_wss_url("ws://localhost:8000")
        assert url.startswith("ws://")

    def test_스킴_없으면_wss_추가(self) -> None:
        """스킴이 없는 호스트명에 wss://를 추가하는지 확인한다."""
        url = _build_wss_url("my-server.example.com")
        assert url.startswith("wss://")

    def test_session_경로_자동_추가(self) -> None:
        """URL에 /session 경로가 자동으로 추가되는지 확인한다."""
        url = _build_wss_url("https://my-server.example.com")
        assert url.endswith("/session")

    def test_session_경로_중복_방지(self) -> None:
        """이미 /session 경로가 있으면 중복 추가하지 않는지 확인한다."""
        url = _build_wss_url("https://my-server.example.com/session")
        # /session이 한 번만 나타나야 함
        assert url.count("/session") == 1

    def test_프로덕션_url_aca_변환(self) -> None:
        """Azure Container Apps 프로덕션 URL이 올바르게 변환되는지 확인한다."""
        url = _build_wss_url(
            "https://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io"
        )
        assert url == "wss://ca-voicelive-fe.agreeablemushroom-8092c27a.koreacentral.azurecontainerapps.io/session"

    def test_프로덕션_url_vm_변환(self) -> None:
        """Azure VM 프로덕션 URL이 올바르게 변환되는지 확인한다."""
        url = _build_wss_url("https://kukovm2.koreacentral.cloudapp.azure.com")
        assert url == "wss://kukovm2.koreacentral.cloudapp.azure.com/session"

    def test_커스텀_포트_보존(self) -> None:
        """https URL에 포함된 커스텀 포트 번호가 wss URL에 그대로 유지되는지 확인한다."""
        url = _build_wss_url("https://kukovm2.koreacentral.cloudapp.azure.com:5173")
        assert url == "wss://kukovm2.koreacentral.cloudapp.azure.com:5173/session"

    def test_커스텀_포트_ws_보존(self) -> None:
        """http URL에 포함된 커스텀 포트 번호가 ws URL에 그대로 유지되는지 확인한다."""
        url = _build_wss_url("http://localhost:8080")
        assert url == "ws://localhost:8080/session"

    def test_wss_커스텀_포트_그대로_유지(self) -> None:
        """이미 wss://로 시작하는 URL의 커스텀 포트가 보존되는지 확인한다."""
        url = _build_wss_url("wss://my-server.example.com:9443")
        assert url == "wss://my-server.example.com:9443/session"


# ──────────────────────────────────────────────
# TLS 설정 테스트
# ──────────────────────────────────────────────


class TestMakeInsecureSslContext:
    """_make_insecure_ssl_context() TLS 컨텍스트 생성을 검증하는 테스트 클래스."""

    def test_insecure_컨텍스트_check_hostname_비활성화(self) -> None:
        """생성된 SSLContext의 check_hostname이 False인지 확인한다."""
        ctx = _make_insecure_ssl_context()
        assert ctx.check_hostname is False

    def test_insecure_컨텍스트_cert_none(self) -> None:
        """생성된 SSLContext의 verify_mode가 CERT_NONE인지 확인한다."""
        ctx = _make_insecure_ssl_context()
        assert ctx.verify_mode == ssl.CERT_NONE

    def test_insecure_컨텍스트_타입(self) -> None:
        """반환 값이 ssl.SSLContext 인스턴스인지 확인한다."""
        ctx = _make_insecure_ssl_context()
        assert isinstance(ctx, ssl.SSLContext)


# ──────────────────────────────────────────────
# Mock 클래스
# ──────────────────────────────────────────────


class MockPlaybackBackend:
    """테스트용 재생 백엔드 mock."""

    def __init__(self) -> None:
        self.played: list[bytes] = []
        self.clear_count: int = 0
        self.close_count: int = 0
        # 테스트에서 재생 중 상태를 제어하기 위한 플래그
        self.playing: bool = False

    def write(self, pcm_data: bytes) -> None:
        self.played.append(pcm_data)

    def clear(self) -> None:
        self.played.clear()
        self.clear_count += 1

    def close(self) -> None:
        self.close_count += 1

    def is_playing(self) -> bool:
        return self.playing


class MockCaptureBackend:
    """테스트용 마이크 캡처 백엔드 mock."""

    def __init__(self) -> None:
        self.started: bool = False
        self.stopped: bool = False
        self._callback = None

    def start(
        self,
        callback,
        sample_rate: int = 24000,
        channels: int = 1,
        frame_size: int = 4096,
    ) -> None:
        self.started = True
        self._callback = callback

    def stop(self) -> None:
        self.stopped = True

    def inject_audio(self, pcm_data: bytes) -> None:
        """테스트에서 가짜 오디오 데이터를 주입한다."""
        if self._callback:
            self._callback(pcm_data)


class MockWebSocket:
    """테스트용 WebSocket 클라이언트 mock.

    미리 정의된 메시지 시퀀스를 반환하고,
    전송된 데이터를 기록한다.
    """

    def __init__(self, messages: list[bytes | str]) -> None:
        # 서버로부터 수신할 메시지 목록
        self._messages = messages
        # 클라이언트가 전송한 데이터 기록
        self.sent: list[bytes | str] = []

    async def send(self, data: bytes | str) -> None:
        """WebSocket 전송 mock: 전송 데이터를 기록한다."""
        self.sent.append(data)

    def __aiter__(self) -> "MockWebSocket":
        """비동기 이터레이터 구현."""
        self._idx = 0
        return self

    async def __anext__(self) -> bytes | str:
        """다음 메시지를 반환한다. 메시지가 소진되면 StopAsyncIteration 발생."""
        if self._idx >= len(self._messages):
            raise StopAsyncIteration
        msg = self._messages[self._idx]
        self._idx += 1
        return msg


# ──────────────────────────────────────────────
# DeviceSession._handle_text_message() 테스트
# ──────────────────────────────────────────────


class TestHandleTextMessage:
    """_handle_text_message() 메서드를 검증하는 테스트 클래스."""

    def _make_session_with_mocks(self) -> tuple[DeviceSession, MockPlaybackBackend]:
        """mock 백엔드를 사용한 DeviceSession을 생성한다."""
        play_backend = MockPlaybackBackend()
        cap_backend = MockCaptureBackend()

        player = AudioPlayer(backend=play_backend)
        mic = MicCapture(backend=cap_backend)

        session = DeviceSession("https://test.example.com", player=player, mic=mic)
        return session, play_backend

    def test_audio_clear_player_clear_호출(self) -> None:
        """audio_clear 메시지 수신 시 player.clear()가 호출되는지 확인한다."""
        session, play_backend = self._make_session_with_mocks()

        # 큐에 데이터 추가
        session._player.play(np.zeros(100, dtype=np.int16).tobytes())

        # audio_clear 처리
        session._handle_text_message(json.dumps({"type": "audio_clear"}))

        # clear가 호출되어 큐가 비워졌어야 함
        assert play_backend.clear_count == 1

    def test_status_메시지_player_clear_미호출(self) -> None:
        """status 메시지 수신 시 player.clear()가 호출되지 않는지 확인한다."""
        session, play_backend = self._make_session_with_mocks()

        session._handle_text_message(json.dumps({"type": "status", "state": "listening"}))

        assert play_backend.clear_count == 0

    def test_유효하지_않은_json_처리(self) -> None:
        """잘못된 JSON 텍스트를 수신해도 예외 없이 처리되는지 확인한다."""
        session, play_backend = self._make_session_with_mocks()

        # 예외 없이 처리되어야 함
        session._handle_text_message("{invalid json")
        assert play_backend.clear_count == 0

    def test_transcript_메시지_처리(self) -> None:
        """transcript 메시지가 오류 없이 처리되는지 확인한다."""
        session, _ = self._make_session_with_mocks()

        text = json.dumps({"type": "transcript", "role": "user", "text": "조명 켜줘"})
        # 예외 없이 처리되어야 함
        session._handle_text_message(text)

    def test_appliance_snapshot_처리(self) -> None:
        """appliance_snapshot 메시지가 오류 없이 처리되는지 확인한다."""
        session, _ = self._make_session_with_mocks()

        text = json.dumps({
            "type": "appliance_snapshot",
            "appliances": [{"id": "1", "name": "에어컨"}],
        })
        session._handle_text_message(text)


# ──────────────────────────────────────────────
# DeviceSession._handle_connection() 통합 테스트
# (WebSocket mock 사용)
# ──────────────────────────────────────────────


class TestHandleConnection:
    """_handle_connection() 메서드를 mock WebSocket으로 검증하는 테스트 클래스."""

    def _make_session_with_mocks(self) -> tuple[DeviceSession, MockPlaybackBackend, MockCaptureBackend]:
        """mock 백엔드를 사용한 DeviceSession을 생성한다."""
        play_backend = MockPlaybackBackend()
        cap_backend = MockCaptureBackend()

        player = AudioPlayer(backend=play_backend)
        mic = MicCapture(backend=cap_backend)

        session = DeviceSession("https://test.example.com", player=player, mic=mic)
        return session, play_backend, cap_backend

    @pytest.mark.asyncio
    async def test_이진_메시지_player_play_호출(self) -> None:
        """이진 WebSocket 메시지 수신 시 player.play()가 호출되는지 확인한다."""
        session, play_backend, _ = self._make_session_with_mocks()

        pcm_data = np.zeros(512, dtype=np.int16).tobytes()
        # 이진 PCM 데이터를 서버 메시지로 시뮬레이션
        ws = MockWebSocket([pcm_data])

        await session._handle_connection(ws)

        # play()가 호출되어 데이터가 기록되어야 함
        assert len(play_backend.played) == 1
        assert play_backend.played[0] == pcm_data

    @pytest.mark.asyncio
    async def test_audio_clear_텍스트_메시지_처리(self) -> None:
        """audio_clear JSON 텍스트 메시지 수신 시 player.clear()가 호출되는지 확인한다."""
        session, play_backend, _ = self._make_session_with_mocks()

        # 먼저 이진 데이터 추가 후 audio_clear 수신
        pcm_data = np.zeros(512, dtype=np.int16).tobytes()
        audio_clear_msg = json.dumps({"type": "audio_clear"})

        ws = MockWebSocket([pcm_data, audio_clear_msg])

        await session._handle_connection(ws)

        # play() 1회, clear() 1회 호출되어야 함
        assert play_backend.clear_count == 1

    @pytest.mark.asyncio
    async def test_마이크_캡처_시작(self) -> None:
        """_handle_connection() 호출 시 마이크 캡처가 시작되는지 확인한다."""
        session, _, cap_backend = self._make_session_with_mocks()

        ws = MockWebSocket([])

        await session._handle_connection(ws)

        # 마이크 캡처가 시작되어야 함
        assert cap_backend.started

    @pytest.mark.asyncio
    async def test_빈_메시지_시퀀스_정상_종료(self) -> None:
        """빈 메시지 시퀀스로 연결 시 정상적으로 종료되는지 확인한다."""
        session, play_backend, _ = self._make_session_with_mocks()

        ws = MockWebSocket([])

        # 예외 없이 정상 종료되어야 함
        await session._handle_connection(ws)

        assert len(play_backend.played) == 0
        assert play_backend.clear_count == 0

    @pytest.mark.asyncio
    async def test_다른_스레드에서_들어온_마이크_오디오가_업링크로_전송됨(self) -> None:
        """sounddevice 오디오 스레드(비메인 스레드)에서 들어온 마이크 콜백이
        실제로 WebSocket 업링크로 전송되는지 확인한다.

        회귀 테스트: 이전 구현은 콜백 내부에서 asyncio.get_event_loop()를
        호출했는데, 비메인 스레드에는 현재 이벤트 루프가 없어 RuntimeError가
        발생하고 except가 이를 삼켜 마이크 오디오가 조용히 버려졌다.
        그 결과 서버(VoiceLive)로 음성이 전혀 전달되지 않았다.
        """
        import threading

        session, _, cap_backend = self._make_session_with_mocks()

        class BlockingWebSocket:
            """다운링크가 닫힐 때까지 열린 상태를 유지하는 mock (업링크 루프 유지용)."""

            def __init__(self) -> None:
                self.sent: list[bytes | str] = []
                self._closed = asyncio.Event()

            async def send(self, data: bytes | str) -> None:
                self.sent.append(data)

            def __aiter__(self) -> "BlockingWebSocket":
                return self

            async def __anext__(self) -> bytes | str:
                await self._closed.wait()
                raise StopAsyncIteration

            def close(self) -> None:
                self._closed.set()

        ws = BlockingWebSocket()
        task = asyncio.create_task(session._handle_connection(ws))

        # 마이크 캡처가 시작되고 업링크 루프가 돌기 시작할 시간을 준다
        await asyncio.sleep(0.05)
        assert cap_backend.started

        # sounddevice 처럼 별도(비메인) 스레드에서 마이크 콜백을 호출한다
        pcm = b"\x11\x22" * 100
        t = threading.Thread(target=cap_backend.inject_audio, args=(pcm,))
        t.start()
        t.join()

        # 업링크로 전송될 때까지 대기 (최대 ~1초)
        for _ in range(50):
            if ws.sent:
                break
            await asyncio.sleep(0.02)

        ws.close()
        await task

        assert pcm in ws.sent, "다른 스레드의 마이크 오디오가 업링크로 전송되지 않았습니다"

    @pytest.mark.asyncio
    async def test_스피커_재생_중에는_마이크_업링크_억제(self) -> None:
        """에코 억제(반이중): 스피커가 재생 중이면 마이크 오디오를 업링크로
        보내지 않는지 확인한다.

        장치가 자기 스피커 출력을 마이크로 다시 잡아(에코) 서버 VAD가 사용자
        발화로 오인하고 audio_clear(바지인)를 보내 재생을 끊는 루프를 방지한다.
        """
        import threading

        session, play_backend, cap_backend = self._make_session_with_mocks()
        # 스피커가 재생 중인 상태로 설정
        play_backend.playing = True

        class BlockingWebSocket:
            def __init__(self) -> None:
                self.sent: list[bytes | str] = []
                self._closed = asyncio.Event()

            async def send(self, data: bytes | str) -> None:
                self.sent.append(data)

            def __aiter__(self) -> "BlockingWebSocket":
                return self

            async def __anext__(self) -> bytes | str:
                await self._closed.wait()
                raise StopAsyncIteration

            def close(self) -> None:
                self._closed.set()

        ws = BlockingWebSocket()
        task = asyncio.create_task(session._handle_connection(ws))
        await asyncio.sleep(0.05)

        # 스피커 재생 중 마이크 오디오 유입 (에코)
        echo = b"\x33\x44" * 100
        t = threading.Thread(target=cap_backend.inject_audio, args=(echo,))
        t.start()
        t.join()
        await asyncio.sleep(0.1)

        ws.close()
        await task

        # 재생 중이었으므로 에코 오디오는 업링크로 전송되지 않아야 함
        assert echo not in ws.sent, "스피커 재생 중 마이크 오디오가 업링크로 전송되었습니다(에코)"

    @pytest.mark.asyncio
    async def test_여러_이진_청크_순서대로_재생(self) -> None:
        """여러 이진 청크가 순서대로 재생되는지 확인한다."""
        session, play_backend, _ = self._make_session_with_mocks()

        chunk1 = np.ones(256, dtype=np.int16).tobytes()
        chunk2 = (np.ones(256, dtype=np.int16) * 2).tobytes()
        chunk3 = (np.ones(256, dtype=np.int16) * 3).tobytes()

        ws = MockWebSocket([chunk1, chunk2, chunk3])

        await session._handle_connection(ws)

        # 3개의 청크가 순서대로 재생되어야 함
        assert len(play_backend.played) == 3
        assert play_backend.played[0] == chunk1
        assert play_backend.played[1] == chunk2
        assert play_backend.played[2] == chunk3

"""
test_protocol.py — 서버 메시지 파싱 단위 테스트

protocol.py의 parse_server_message() 및 포맷 함수들을 검증한다.
실제 네트워크 연결 없이 순수 Python 로직만 테스트한다.
"""

from __future__ import annotations

import json
import logging

import pytest

from device_emulator.protocol import (
    MSG_APPLIANCE_SNAPSHOT,
    MSG_APPLIANCE_UPDATE,
    MSG_AUDIO_CLEAR,
    MSG_STATUS,
    MSG_TRANSCRIPT,
    format_appliance_snapshot,
    format_appliance_update,
    format_status,
    format_transcript,
    log_server_message,
    parse_server_message,
)


# ──────────────────────────────────────────────
# parse_server_message() 테스트
# ──────────────────────────────────────────────


class TestParseServerMessage:
    """parse_server_message() 함수의 동작을 검증하는 테스트 클래스."""

    def test_status_listening_파싱_성공(self) -> None:
        """status 메시지를 올바르게 파싱하는지 확인한다."""
        # Given: 서버에서 수신한 status JSON 문자열
        text = json.dumps({"type": "status", "state": "listening"})

        # When: 파싱 실행
        msg = parse_server_message(text)

        # Then: 딕셔너리로 반환되고 필드가 올바른지 확인
        assert msg is not None
        assert msg["type"] == MSG_STATUS
        assert msg["state"] == "listening"

    def test_status_thinking_파싱_성공(self) -> None:
        """status thinking 메시지를 올바르게 파싱하는지 확인한다."""
        text = json.dumps({"type": "status", "state": "thinking"})
        msg = parse_server_message(text)
        assert msg is not None
        assert msg["state"] == "thinking"

    def test_audio_clear_파싱_성공(self) -> None:
        """audio_clear 메시지를 올바르게 파싱하는지 확인한다."""
        # Given: audio_clear JSON 문자열
        text = json.dumps({"type": "audio_clear"})

        # When/Then: 파싱 결과가 audio_clear 타입인지 확인
        msg = parse_server_message(text)
        assert msg is not None
        assert msg["type"] == MSG_AUDIO_CLEAR

    def test_appliance_snapshot_파싱_성공(self) -> None:
        """appliance_snapshot 메시지를 올바르게 파싱하는지 확인한다."""
        appliances = [
            {"id": "device-1", "name": "거실 에어컨"},
            {"id": "device-2", "name": "침실 TV"},
        ]
        text = json.dumps({"type": "appliance_snapshot", "appliances": appliances})

        msg = parse_server_message(text)

        assert msg is not None
        assert msg["type"] == MSG_APPLIANCE_SNAPSHOT
        assert len(msg["appliances"]) == 2
        assert msg["appliances"][0]["name"] == "거실 에어컨"

    def test_appliance_update_파싱_성공(self) -> None:
        """appliance_update 메시지를 올바르게 파싱하는지 확인한다."""
        appliance = {"id": "device-1", "name": "거실 에어컨", "power": "on"}
        text = json.dumps({"type": "appliance_update", "appliance": appliance})

        msg = parse_server_message(text)

        assert msg is not None
        assert msg["type"] == MSG_APPLIANCE_UPDATE
        assert msg["appliance"]["power"] == "on"

    def test_transcript_파싱_성공(self) -> None:
        """transcript 메시지를 올바르게 파싱하는지 확인한다."""
        text = json.dumps({"type": "transcript", "role": "user", "text": "에어컨 켜줘"})

        msg = parse_server_message(text)

        assert msg is not None
        assert msg["type"] == MSG_TRANSCRIPT
        assert msg["role"] == "user"
        assert msg["text"] == "에어컨 켜줘"

    def test_유효하지_않은_json_반환_none(self) -> None:
        """잘못된 JSON 문자열에 대해 None을 반환하는지 확인한다."""
        # Given: 잘못된 JSON 문자열
        invalid_json = "{not valid json"

        # When/Then: None을 반환해야 함
        msg = parse_server_message(invalid_json)
        assert msg is None

    def test_type_필드_없으면_반환_none(self) -> None:
        """'type' 필드가 없는 메시지에 대해 None을 반환하는지 확인한다."""
        text = json.dumps({"state": "listening"})

        msg = parse_server_message(text)

        assert msg is None

    def test_json_배열이면_반환_none(self) -> None:
        """JSON 배열 형태의 메시지에 대해 None을 반환하는지 확인한다."""
        text = json.dumps([{"type": "status"}])

        msg = parse_server_message(text)

        assert msg is None

    def test_빈_문자열_반환_none(self) -> None:
        """빈 문자열에 대해 None을 반환하는지 확인한다."""
        msg = parse_server_message("")
        assert msg is None

    def test_알수없는_타입_반환(self, caplog: pytest.LogCaptureFixture) -> None:
        """알 수 없는 메시지 타입에 대해 메시지를 반환하고 경고를 로그한다."""
        text = json.dumps({"type": "unknown_future_type", "data": 42})

        with caplog.at_level(logging.WARNING):
            msg = parse_server_message(text)

        # 알 수 없는 타입도 반환은 함 (미래 확장 대비)
        assert msg is not None
        assert msg["type"] == "unknown_future_type"
        # 경고 로그가 찍혔는지 확인
        assert any("알 수 없는 메시지 타입" in r.message for r in caplog.records)

    def test_한국어_텍스트_파싱(self) -> None:
        """한국어 텍스트가 포함된 메시지를 올바르게 파싱하는지 확인한다."""
        text = json.dumps(
            {"type": "transcript", "role": "assistant", "text": "에어컨을 켰습니다."},
            ensure_ascii=False,
        )
        msg = parse_server_message(text)
        assert msg is not None
        assert msg["text"] == "에어컨을 켰습니다."


# ──────────────────────────────────────────────
# 포맷 함수 테스트
# ──────────────────────────────────────────────


class TestFormatFunctions:
    """메시지 포맷 함수들을 검증하는 테스트 클래스."""

    def test_format_appliance_snapshot_이름_포함(self) -> None:
        """appliance_snapshot 포맷 문자열에 가전제품 이름이 포함되는지 확인한다."""
        msg = {
            "type": "appliance_snapshot",
            "appliances": [
                {"id": "1", "name": "에어컨"},
                {"id": "2", "name": "TV"},
            ],
        }
        result = format_appliance_snapshot(msg)
        assert "에어컨" in result
        assert "TV" in result
        assert "2" in result  # 가전제품 수

    def test_format_appliance_snapshot_빈_목록(self) -> None:
        """빈 가전제품 목록을 처리하는지 확인한다."""
        msg = {"type": "appliance_snapshot", "appliances": []}
        result = format_appliance_snapshot(msg)
        assert "0" in result

    def test_format_appliance_update(self) -> None:
        """appliance_update 포맷 문자열에 가전제품 이름이 포함되는지 확인한다."""
        msg = {
            "type": "appliance_update",
            "appliance": {"id": "1", "name": "에어컨", "power": "on"},
        }
        result = format_appliance_update(msg)
        assert "에어컨" in result

    def test_format_transcript_user(self) -> None:
        """transcript 포맷 문자열에 화자와 텍스트가 포함되는지 확인한다."""
        msg = {"type": "transcript", "role": "user", "text": "조명 켜줘"}
        result = format_transcript(msg)
        assert "user" in result
        assert "조명 켜줘" in result

    def test_format_transcript_assistant(self) -> None:
        """어시스턴트 전사 포맷을 확인한다."""
        msg = {"type": "transcript", "role": "assistant", "text": "조명을 켰습니다"}
        result = format_transcript(msg)
        assert "assistant" in result
        assert "조명을 켰습니다" in result

    def test_format_status_listening(self) -> None:
        """status 포맷 문자열에 상태가 포함되는지 확인한다."""
        msg = {"type": "status", "state": "listening"}
        result = format_status(msg)
        assert "listening" in result

    def test_format_status_speaking(self) -> None:
        """speaking 상태 포맷을 확인한다."""
        msg = {"type": "status", "state": "speaking"}
        result = format_status(msg)
        assert "speaking" in result


# ──────────────────────────────────────────────
# log_server_message() 테스트
# ──────────────────────────────────────────────


class TestLogServerMessage:
    """log_server_message() 함수의 로그 출력을 검증하는 테스트 클래스."""

    def test_status_메시지_info_로그(self, caplog: pytest.LogCaptureFixture) -> None:
        """status 메시지가 INFO 레벨로 로그에 출력되는지 확인한다."""
        msg = {"type": "status", "state": "listening"}

        with caplog.at_level(logging.INFO):
            log_server_message(msg)

        assert any("listening" in r.message for r in caplog.records)

    def test_audio_clear_warning_로그(self, caplog: pytest.LogCaptureFixture) -> None:
        """audio_clear 메시지가 WARNING 레벨로 로그에 출력되는지 확인한다."""
        msg = {"type": "audio_clear"}

        with caplog.at_level(logging.WARNING):
            log_server_message(msg)

        # audio_clear는 WARNING 레벨로 출력되어야 함
        warning_records = [r for r in caplog.records if r.levelno == logging.WARNING]
        assert len(warning_records) > 0

    def test_transcript_메시지_로그(self, caplog: pytest.LogCaptureFixture) -> None:
        """transcript 메시지가 로그에 출력되는지 확인한다."""
        msg = {"type": "transcript", "role": "user", "text": "테스트 텍스트"}

        with caplog.at_level(logging.INFO):
            log_server_message(msg)

        assert any("테스트 텍스트" in r.message for r in caplog.records)

"""
protocol.py — 서버 WebSocket 텍스트 메시지 파싱 모듈

서버에서 전송하는 JSON 텍스트 프레임을 파싱하고,
메시지 타입을 분류하는 유틸리티를 제공한다.

서버→클라이언트 텍스트 메시지 종류:
  - appliance_snapshot : 현재 가전제품 전체 목록 스냅샷
  - appliance_update   : 개별 가전제품 상태 변경 알림
  - transcript         : 사용자 또는 어시스턴트 발화 텍스트
  - status             : 세션 상태 전환 (listening / thinking / speaking 등)
  - audio_clear        : 재생 중인 오디오를 즉시 중단(바지인 처리)
"""

from __future__ import annotations

import json
import logging
from typing import Any

log = logging.getLogger("device_emulator.protocol")

# ──────────────────────────────────────────────
# 서버 텍스트 메시지 타입 상수 정의
# ──────────────────────────────────────────────

# 가전제품 전체 목록을 담은 스냅샷 메시지 타입
MSG_APPLIANCE_SNAPSHOT = "appliance_snapshot"

# 개별 가전제품 상태 변경 메시지 타입
MSG_APPLIANCE_UPDATE = "appliance_update"

# 발화 텍스트 메시지 타입
MSG_TRANSCRIPT = "transcript"

# 세션 상태 전환 메시지 타입
MSG_STATUS = "status"

# 오디오 재생 중단 명령 메시지 타입
MSG_AUDIO_CLEAR = "audio_clear"

# 알려진 모든 메시지 타입 집합 (로그 경고용)
_KNOWN_TYPES = frozenset({
    MSG_APPLIANCE_SNAPSHOT,
    MSG_APPLIANCE_UPDATE,
    MSG_TRANSCRIPT,
    MSG_STATUS,
    MSG_AUDIO_CLEAR,
})


def parse_server_message(text: str) -> dict[str, Any] | None:
    """서버로부터 수신한 텍스트 프레임을 JSON으로 파싱한다.

    JSON 파싱에 실패하거나 'type' 필드가 없으면 None을 반환한다.
    알려지지 않은 메시지 타입은 경고 로그 후 그대로 반환한다.

    Args:
        text: WebSocket 텍스트 프레임의 원시 문자열

    Returns:
        파싱된 메시지 딕셔너리, 또는 파싱 불가 시 None
    """
    try:
        msg = json.loads(text)
    except (ValueError, TypeError):
        # JSON 디코딩 실패 시 경고 로그 후 None 반환
        log.warning("JSON 파싱 실패 (무시): %r", text[:120])
        return None

    if not isinstance(msg, dict):
        # 딕셔너리가 아닌 JSON 타입(배열, 원시값 등)은 무시
        log.warning("딕셔너리가 아닌 JSON 메시지 무시: %r", text[:120])
        return None

    if "type" not in msg:
        # 'type' 키가 없는 메시지는 처리 불가
        log.warning("'type' 필드 없는 메시지 무시: %r", text[:120])
        return None

    if msg["type"] not in _KNOWN_TYPES:
        # 알려지지 않은 타입도 경고 후 반환 (미래 확장 대비)
        log.warning("알 수 없는 메시지 타입: %r", msg["type"])

    return msg


def format_appliance_snapshot(msg: dict[str, Any]) -> str:
    """appliance_snapshot 메시지를 사람이 읽기 쉬운 문자열로 포맷한다.

    Args:
        msg: parse_server_message() 로 파싱된 appliance_snapshot 딕셔너리

    Returns:
        가전제품 목록을 요약한 문자열
    """
    appliances = msg.get("appliances", [])
    # 가전제품 이름 목록을 콤마로 연결하여 요약 문자열 생성
    names = [a.get("name", a.get("id", "unknown")) for a in appliances]
    return f"[가전제품 스냅샷] {len(appliances)}개: {', '.join(names)}"


def format_appliance_update(msg: dict[str, Any]) -> str:
    """appliance_update 메시지를 사람이 읽기 쉬운 문자열로 포맷한다.

    Args:
        msg: parse_server_message() 로 파싱된 appliance_update 딕셔너리

    Returns:
        변경된 가전제품 상태 요약 문자열
    """
    appliance = msg.get("appliance", {})
    name = appliance.get("name", appliance.get("id", "unknown"))
    # 변경된 가전제품의 이름(또는 ID)과 상태 정보를 포함한 문자열 반환
    return f"[가전제품 업데이트] {name}: {appliance}"


def format_transcript(msg: dict[str, Any]) -> str:
    """transcript 메시지를 사람이 읽기 쉬운 문자열로 포맷한다.

    Args:
        msg: parse_server_message() 로 파싱된 transcript 딕셔너리

    Returns:
        화자 역할과 발화 텍스트를 포함한 문자열
    """
    role = msg.get("role", "?")
    text = msg.get("text", "")
    # 화자(user/assistant)와 텍스트를 포함한 전사 문자열 반환
    return f"[전사] {role}: {text}"


def format_status(msg: dict[str, Any]) -> str:
    """status 메시지를 사람이 읽기 쉬운 문자열로 포맷한다.

    Args:
        msg: parse_server_message() 로 파싱된 status 딕셔너리

    Returns:
        현재 세션 상태를 나타내는 문자열
    """
    state = msg.get("state", "unknown")
    # 세션 상태(listening/thinking/speaking 등)를 포함한 문자열 반환
    return f"[상태] {state}"


def log_server_message(msg: dict[str, Any]) -> None:
    """서버 텍스트 메시지를 적절한 형식으로 로그에 기록한다.

    메시지 타입에 따라 다른 포맷 함수를 호출하여 로그 출력한다.
    audio_clear는 재생 중단 명령이므로 WARNING 레벨로 기록한다.

    Args:
        msg: parse_server_message() 로 파싱된 메시지 딕셔너리
    """
    msg_type = msg.get("type")

    if msg_type == MSG_APPLIANCE_SNAPSHOT:
        log.info(format_appliance_snapshot(msg))
    elif msg_type == MSG_APPLIANCE_UPDATE:
        log.info(format_appliance_update(msg))
    elif msg_type == MSG_TRANSCRIPT:
        # 전사 텍스트는 INFO 레벨로 출력 (CLI에서 확인용)
        log.info(format_transcript(msg))
    elif msg_type == MSG_STATUS:
        log.info(format_status(msg))
    elif msg_type == MSG_AUDIO_CLEAR:
        # audio_clear는 바지인(끼어들기) 명령이므로 WARNING으로 강조
        log.warning("[오디오 클리어] 재생 중단 명령 수신")
    else:
        # 알 수 없는 타입은 원시 JSON으로 출력
        log.info("[기타 메시지] %s", msg)

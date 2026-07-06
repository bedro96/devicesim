"""
test_echo_gate.py — 반이중(half-duplex) 에코 억제 게이트 단위 테스트

스피커가 재생 중일 때 마이크 업링크를 억제하여, 장치가 자기 스피커 출력을
다시 마이크로 잡아 서버 VAD가 오인(바지인)하는 에코 루프를 방지한다.

seam: EchoGate.should_send(is_playing: bool, now: float) -> bool
"""

from __future__ import annotations

from device_emulator.audio import EchoGate


class TestEchoGate:
    def test_재생_중이면_전송_안함(self) -> None:
        gate = EchoGate(hangover_seconds=0.25)
        assert gate.should_send(is_playing=True, now=0.0) is False

    def test_재생_아니고_행오버_지나면_전송(self) -> None:
        gate = EchoGate(hangover_seconds=0.25)
        # 한 번도 재생하지 않았다면 즉시 전송 허용
        assert gate.should_send(is_playing=False, now=0.0) is True

    def test_재생_종료_직후_행오버_동안_억제(self) -> None:
        gate = EchoGate(hangover_seconds=0.25)
        # 재생 중 → 억제 (행오버 창을 now+0.25로 설정)
        assert gate.should_send(is_playing=True, now=1.0) is False
        # 재생이 막 끝났지만 행오버(0.25s) 이내 → 여전히 억제
        assert gate.should_send(is_playing=False, now=1.1) is False

    def test_행오버_경과_후_전송_재개(self) -> None:
        gate = EchoGate(hangover_seconds=0.25)
        assert gate.should_send(is_playing=True, now=1.0) is False
        # 행오버(0.25s)를 지나면 다시 전송 허용
        assert gate.should_send(is_playing=False, now=1.30) is True

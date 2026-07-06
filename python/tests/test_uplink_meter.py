"""
test_uplink_meter.py — 업링크 오디오 관측(observability) 단위 테스트

마이크→서버 업링크가 실제로 "무음이 아닌" 오디오를 전송하고 있는지
사용자가 확인할 수 있도록 하는 계측기를 검증한다.

seam(공개 경계):
  - pcm16_rms(bytes) -> float          : PCM16 청크의 정규화 RMS(0.0~1.0)
  - UplinkMeter.observe(bytes) -> str? : 누적 오디오가 interval 초를 넘으면
                                         요약 로그 문자열을 반환, 아니면 None
"""

from __future__ import annotations

import numpy as np

from device_emulator.audio import UplinkMeter, pcm16_rms


class TestPcm16Rms:
    """pcm16_rms()의 순수 수치 동작을 검증한다."""

    def test_무음은_0(self) -> None:
        data = np.zeros(4096, dtype=np.int16).tobytes()
        assert pcm16_rms(data) == 0.0

    def test_풀스케일_1에_근접(self) -> None:
        data = (np.ones(4096, dtype=np.int16) * 32767).tobytes()
        assert pcm16_rms(data) > 0.99

    def test_빈_데이터는_0(self) -> None:
        assert pcm16_rms(b"") == 0.0

    def test_알려진_진폭(self) -> None:
        # 전 샘플이 16384 (풀스케일의 약 절반) → RMS ≈ 0.5
        data = (np.ones(1000, dtype=np.int16) * 16384).tobytes()
        rms = pcm16_rms(data)
        assert 0.49 < rms < 0.51


class TestUplinkMeter:
    """UplinkMeter의 주기적 요약 및 무음 감지 동작을 검증한다."""

    def _chunk(self, samples: int, value: int) -> bytes:
        return (np.ones(samples, dtype=np.int16) * value).tobytes()

    def test_interval_전에는_None(self) -> None:
        # interval=1.0초, 24kHz → 24000 샘플 이상이어야 요약 발생
        meter = UplinkMeter(sample_rate=24_000, interval_seconds=1.0)
        # 0.25초 분량(6000 샘플)만 관측 → 아직 None
        assert meter.observe(self._chunk(6000, 1000)) is None

    def test_interval_넘으면_요약_문자열_반환(self) -> None:
        meter = UplinkMeter(sample_rate=24_000, interval_seconds=1.0)
        # 1.5초 분량(36000 샘플) 관측 → 요약 반환
        msg = meter.observe(self._chunk(36_000, 8000))
        assert msg is not None
        assert "업링크" in msg  # 사람이 읽을 수 있는 업링크 요약

    def test_무음이면_경고_포함(self) -> None:
        meter = UplinkMeter(sample_rate=24_000, interval_seconds=1.0)
        # 1.5초 분량의 완전한 무음
        msg = meter.observe(self._chunk(36_000, 0))
        assert msg is not None
        assert "무음" in msg  # 무음(마이크 미입력) 경고

    def test_요약_후_카운터_리셋(self) -> None:
        meter = UplinkMeter(sample_rate=24_000, interval_seconds=1.0)
        # 첫 요약 발생
        assert meter.observe(self._chunk(36_000, 8000)) is not None
        # 리셋 후 소량만 관측하면 다시 None
        assert meter.observe(self._chunk(6000, 8000)) is None

    def test_누적_바이트_보고(self) -> None:
        meter = UplinkMeter(sample_rate=24_000, interval_seconds=1.0)
        chunk = self._chunk(36_000, 8000)  # 72000 bytes
        msg = meter.observe(chunk)
        assert msg is not None
        # 누적 전송 바이트가 요약에 표시되어야 함
        assert "72000" in msg or "72,000" in msg

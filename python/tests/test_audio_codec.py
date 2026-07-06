"""
test_audio_codec.py — PCM16 오디오 코덱 단위 테스트

audio.py의 PCM16 변환 함수들을 검증한다.
실제 오디오 하드웨어 없이 순수 수치 계산만 테스트한다.
"""

from __future__ import annotations

import numpy as np
import pytest

from device_emulator.audio import (
    DEFAULT_FRAME_SIZE,
    SAMPLE_RATE,
    float32_to_pcm16_bytes,
    pcm16_bytes_to_float32,
)


# ──────────────────────────────────────────────
# 상수 검증
# ──────────────────────────────────────────────


class TestAudioConstants:
    """오디오 포맷 상수가 서버 스펙에 맞는지 검증한다."""

    def test_sample_rate_24khz(self) -> None:
        """샘플레이트가 24kHz인지 확인한다 (서버 스펙 일치)."""
        assert SAMPLE_RATE == 24_000

    def test_default_frame_size_4096(self) -> None:
        """기본 프레임 크기가 4096인지 확인한다 (프론트엔드 일치)."""
        assert DEFAULT_FRAME_SIZE == 4096


# ──────────────────────────────────────────────
# pcm16_bytes_to_float32() 테스트
# ──────────────────────────────────────────────


class TestPcm16BytesToFloat32:
    """pcm16_bytes_to_float32() 변환 함수를 검증하는 테스트 클래스."""

    def test_무음_변환(self) -> None:
        """0 값의 PCM16 바이트가 0.0 float32로 변환되는지 확인한다."""
        # Given: 모두 0인 PCM16 바이트 (무음)
        pcm_data = np.zeros(100, dtype=np.int16).tobytes()

        # When: float32로 변환
        result = pcm16_bytes_to_float32(pcm_data)

        # Then: 모든 값이 0.0이어야 함
        assert result.dtype == np.float32
        assert np.all(result == 0.0)
        assert len(result) == 100

    def test_최대값_변환(self) -> None:
        """int16 최대값(32767)이 ~1.0 float32로 변환되는지 확인한다."""
        pcm_data = np.array([32767], dtype=np.int16).tobytes()

        result = pcm16_bytes_to_float32(pcm_data)

        # 32767 / 32768 ≈ 0.9999695 (약 1.0에 근접)
        assert result.dtype == np.float32
        assert abs(result[0] - 1.0) < 0.001

    def test_최소값_변환(self) -> None:
        """int16 최소값(-32768)이 -1.0 float32로 변환되는지 확인한다."""
        pcm_data = np.array([-32768], dtype=np.int16).tobytes()

        result = pcm16_bytes_to_float32(pcm_data)

        # -32768 / 32768 = -1.0
        assert result.dtype == np.float32
        assert abs(result[0] - (-1.0)) < 0.001

    def test_중간값_변환(self) -> None:
        """중간 값의 PCM16이 올바른 float32로 변환되는지 확인한다."""
        # 16384 / 32768 ≈ 0.5
        pcm_data = np.array([16384], dtype=np.int16).tobytes()
        result = pcm16_bytes_to_float32(pcm_data)
        assert abs(result[0] - 0.5) < 0.001

    def test_프레임_크기_변환(self) -> None:
        """DEFAULT_FRAME_SIZE 크기의 PCM16 배열을 변환한다."""
        pcm_data = np.random.randint(-32768, 32767, DEFAULT_FRAME_SIZE, dtype=np.int16).tobytes()

        result = pcm16_bytes_to_float32(pcm_data)

        # 결과 배열 길이와 값 범위 검증
        assert len(result) == DEFAULT_FRAME_SIZE
        assert result.dtype == np.float32
        assert np.all(result >= -1.0)
        assert np.all(result <= 1.0)

    def test_빈_배열_변환(self) -> None:
        """빈 바이트 배열이 빈 float32 배열로 변환되는지 확인한다."""
        result = pcm16_bytes_to_float32(b"")
        assert len(result) == 0
        assert result.dtype == np.float32


# ──────────────────────────────────────────────
# float32_to_pcm16_bytes() 테스트
# ──────────────────────────────────────────────


class TestFloat32ToPcm16Bytes:
    """float32_to_pcm16_bytes() 변환 함수를 검증하는 테스트 클래스."""

    def test_무음_변환(self) -> None:
        """0.0 float32 배열이 0 값의 PCM16 바이트로 변환되는지 확인한다."""
        # Given: 모두 0.0인 float32 배열 (무음)
        arr = np.zeros(100, dtype=np.float32)

        # When: PCM16 바이트로 변환
        result = float32_to_pcm16_bytes(arr)

        # Then: 모든 int16 값이 0이어야 함
        assert len(result) == 100 * 2  # int16 = 2바이트
        result_arr = np.frombuffer(result, dtype=np.int16)
        assert np.all(result_arr == 0)

    def test_최대값_변환(self) -> None:
        """1.0 float32가 32767 int16으로 변환되는지 확인한다."""
        arr = np.array([1.0], dtype=np.float32)
        result = float32_to_pcm16_bytes(arr)
        result_arr = np.frombuffer(result, dtype=np.int16)
        assert result_arr[0] == 32767

    def test_최소값_변환(self) -> None:
        """-1.0 float32가 -32767 int16으로 변환되는지 확인한다."""
        arr = np.array([-1.0], dtype=np.float32)
        result = float32_to_pcm16_bytes(arr)
        result_arr = np.frombuffer(result, dtype=np.int16)
        assert result_arr[0] == -32767

    def test_클리핑_처리(self) -> None:
        """범위를 초과하는 값이 클리핑되는지 확인한다."""
        # 1.0을 초과하는 값은 32767로 클리핑되어야 함
        arr = np.array([2.0, -2.0], dtype=np.float32)
        result = float32_to_pcm16_bytes(arr)
        result_arr = np.frombuffer(result, dtype=np.int16)
        assert result_arr[0] == 32767
        assert result_arr[1] == -32767

    def test_중간값_변환(self) -> None:
        """0.5 float32가 약 16383 int16으로 변환되는지 확인한다."""
        arr = np.array([0.5], dtype=np.float32)
        result = float32_to_pcm16_bytes(arr)
        result_arr = np.frombuffer(result, dtype=np.int16)
        # 0.5 * 32767 ≈ 16383
        assert abs(result_arr[0] - 16383) <= 1

    def test_빈_배열_변환(self) -> None:
        """빈 float32 배열이 빈 바이트로 변환되는지 확인한다."""
        arr = np.array([], dtype=np.float32)
        result = float32_to_pcm16_bytes(arr)
        assert result == b""


# ──────────────────────────────────────────────
# 왕복 변환(round-trip) 테스트
# ──────────────────────────────────────────────


class TestRoundTrip:
    """PCM16 → float32 → PCM16 왕복 변환의 정확성을 검증한다."""

    def test_무음_왕복_변환(self) -> None:
        """무음 PCM16 데이터가 왕복 변환 후 동일한지 확인한다."""
        original = np.zeros(256, dtype=np.int16).tobytes()

        # PCM16 → float32 → PCM16
        float_arr = pcm16_bytes_to_float32(original)
        reconstructed = float32_to_pcm16_bytes(float_arr)

        # 왕복 변환 후 원본과 동일해야 함
        assert original == reconstructed

    def test_랜덤_pcm16_왕복_변환_오차_허용(self) -> None:
        """랜덤 PCM16 데이터의 왕복 변환 오차가 허용 범위 내인지 확인한다."""
        rng = np.random.default_rng(42)
        original_arr = rng.integers(-32768, 32767, 1024, dtype=np.int16)
        original = original_arr.tobytes()

        # PCM16 → float32 → PCM16
        float_arr = pcm16_bytes_to_float32(original)
        reconstructed = float32_to_pcm16_bytes(float_arr)
        reconstructed_arr = np.frombuffer(reconstructed, dtype=np.int16)

        # 부동소수점 반올림 오차로 인해 ±1 허용
        diff = np.abs(original_arr.astype(np.int32) - reconstructed_arr.astype(np.int32))
        assert np.all(diff <= 1), f"최대 오차: {diff.max()}"

    def test_프레임_크기_왕복_변환(self) -> None:
        """DEFAULT_FRAME_SIZE 크기의 데이터 왕복 변환을 검증한다."""
        rng = np.random.default_rng(0)
        original_arr = rng.integers(-1000, 1000, DEFAULT_FRAME_SIZE, dtype=np.int16)
        original = original_arr.tobytes()

        float_arr = pcm16_bytes_to_float32(original)
        reconstructed = float32_to_pcm16_bytes(float_arr)
        reconstructed_arr = np.frombuffer(reconstructed, dtype=np.int16)

        # 결과 크기 검증
        assert len(reconstructed_arr) == DEFAULT_FRAME_SIZE
        # 오차 검증 (±1 허용)
        diff = np.abs(original_arr.astype(np.int32) - reconstructed_arr.astype(np.int32))
        assert np.all(diff <= 1)

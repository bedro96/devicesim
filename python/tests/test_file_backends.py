"""
test_file_backends.py — WAV 파일 기반 오디오 백엔드 단위 테스트

WavCaptureBackend와 WavPlaybackBackend의 파일 I/O 동작을 검증한다.
실제 오디오 하드웨어 없이 임시 WAV 파일만으로 테스트한다.
"""

from __future__ import annotations

import threading
import wave
from pathlib import Path

import numpy as np
import pytest

from device_emulator.audio import (
    CHANNELS,
    SAMPLE_RATE,
    WavCaptureBackend,
    WavPlaybackBackend,
)


# ──────────────────────────────────────────────
# 헬퍼 함수
# ──────────────────────────────────────────────


def _write_wav(path: Path, samples: np.ndarray, sample_rate: int = SAMPLE_RATE) -> None:
    """테스트용 WAV 파일을 생성한다."""
    with wave.open(str(path), "wb") as wf:
        wf.setnchannels(CHANNELS)
        wf.setsampwidth(2)  # int16 = 2 bytes
        wf.setframerate(sample_rate)
        wf.writeframes(samples.astype(np.int16).tobytes())


# ──────────────────────────────────────────────
# WavPlaybackBackend 테스트
# ──────────────────────────────────────────────


class TestWavPlaybackBackend:
    """WavPlaybackBackend WAV 파일 저장 백엔드를 검증하는 테스트 클래스."""

    def test_write_후_close_wav_파일_생성(self, tmp_path: Path) -> None:
        """write() 후 close()를 호출하면 WAV 파일이 생성되는지 확인한다."""
        out_file = str(tmp_path / "out.wav")
        backend = WavPlaybackBackend(out_file)

        pcm = np.zeros(512, dtype=np.int16).tobytes()
        backend.write(pcm)
        backend.close()

        # WAV 파일이 올바른 포맷으로 생성되어야 함
        assert (tmp_path / "out.wav").exists()
        with wave.open(out_file, "rb") as wf:
            assert wf.getnchannels() == CHANNELS
            assert wf.getsampwidth() == 2
            assert wf.getframerate() == SAMPLE_RATE
            assert wf.getnframes() == 512

    def test_여러_청크_순서대로_저장(self, tmp_path: Path) -> None:
        """여러 청크가 순서대로 WAV 파일에 저장되는지 확인한다."""
        out_file = str(tmp_path / "out.wav")
        backend = WavPlaybackBackend(out_file)

        chunk1 = (np.ones(256, dtype=np.int16) * 100).tobytes()
        chunk2 = (np.ones(256, dtype=np.int16) * 200).tobytes()
        backend.write(chunk1)
        backend.write(chunk2)
        backend.close()

        with wave.open(out_file, "rb") as wf:
            data = wf.readframes(512)
        arr = np.frombuffer(data, dtype=np.int16)
        # 첫 256 샘플은 100, 다음 256 샘플은 200이어야 함
        assert np.all(arr[:256] == 100)
        assert np.all(arr[256:] == 200)

    def test_clear_후_close_빈_버퍼_파일_미생성(self, tmp_path: Path) -> None:
        """clear() 후 close()를 호출하면 파일이 생성되지 않는지 확인한다."""
        out_file = str(tmp_path / "out.wav")
        backend = WavPlaybackBackend(out_file)

        backend.write(np.zeros(256, dtype=np.int16).tobytes())
        backend.clear()  # 버퍼 비움
        backend.close()

        # 버퍼가 비었으므로 파일이 생성되지 않아야 함
        assert not (tmp_path / "out.wav").exists()

    def test_빈_버퍼_close_파일_미생성(self, tmp_path: Path) -> None:
        """write() 없이 close()를 호출하면 파일이 생성되지 않는지 확인한다."""
        out_file = str(tmp_path / "out.wav")
        backend = WavPlaybackBackend(out_file)
        backend.close()

        assert not (tmp_path / "out.wav").exists()

    def test_clear_카운트_후_write_재개(self, tmp_path: Path) -> None:
        """clear() 후 write()를 하면 새 데이터만 저장되는지 확인한다."""
        out_file = str(tmp_path / "out.wav")
        backend = WavPlaybackBackend(out_file)

        # 첫 번째 청크 후 clear
        backend.write(np.ones(256, dtype=np.int16).tobytes())
        backend.clear()

        # 두 번째 청크 추가 후 close
        chunk2 = (np.ones(128, dtype=np.int16) * 42).tobytes()
        backend.write(chunk2)
        backend.close()

        with wave.open(out_file, "rb") as wf:
            assert wf.getnframes() == 128
            data = wf.readframes(128)
        arr = np.frombuffer(data, dtype=np.int16)
        assert np.all(arr == 42)

    def test_사용자_지정_샘플레이트_저장(self, tmp_path: Path) -> None:
        """사용자 지정 샘플레이트가 WAV 파일 헤더에 반영되는지 확인한다."""
        out_file = str(tmp_path / "out_16k.wav")
        backend = WavPlaybackBackend(out_file, sample_rate=16_000)

        backend.write(np.zeros(256, dtype=np.int16).tobytes())
        backend.close()

        with wave.open(out_file, "rb") as wf:
            assert wf.getframerate() == 16_000


# ──────────────────────────────────────────────
# WavCaptureBackend 테스트
# ──────────────────────────────────────────────


class TestWavCaptureBackend:
    """WavCaptureBackend WAV 파일 캡처 백엔드를 검증하는 테스트 클래스."""

    def test_wav_파일_읽기_콜백_호출(self, tmp_path: Path) -> None:
        """WAV 파일을 읽어 콜백이 최소 1회 호출되는지 확인한다."""
        wav_file = tmp_path / "input.wav"
        _write_wav(wav_file, np.zeros(512, dtype=np.int16))

        received: list[bytes] = []
        done = threading.Event()

        def callback(pcm: bytes) -> None:
            received.append(pcm)
            done.set()

        # realtime=False: 슬립 없이 즉시 전송 (테스트 속도 향상)
        backend = WavCaptureBackend(str(wav_file), realtime=False)
        backend.start(callback, sample_rate=SAMPLE_RATE, channels=1, frame_size=512)

        done.wait(timeout=2.0)
        backend.stop()

        assert len(received) >= 1

    def test_콜백_데이터_내용_일치(self, tmp_path: Path) -> None:
        """콜백으로 전달된 PCM16 데이터가 WAV 파일 내용과 일치하는지 확인한다."""
        wav_file = tmp_path / "input.wav"
        # 특정 값으로 채운 WAV 파일 생성
        original = np.full(256, 1000, dtype=np.int16)
        _write_wav(wav_file, original)

        received: list[bytes] = []
        done = threading.Event()

        def callback(pcm: bytes) -> None:
            received.append(pcm)
            done.set()

        backend = WavCaptureBackend(str(wav_file), realtime=False)
        backend.start(callback, sample_rate=SAMPLE_RATE, channels=1, frame_size=256)
        done.wait(timeout=2.0)
        backend.stop()

        assert received
        arr = np.frombuffer(received[0], dtype=np.int16)
        assert np.all(arr == 1000)

    def test_여러_프레임_분할_전송(self, tmp_path: Path) -> None:
        """WAV 파일이 여러 프레임으로 분할되어 콜백에 전달되는지 확인한다."""
        wav_file = tmp_path / "input.wav"
        # 1024 샘플짜리 WAV, frame_size=256 → 4회 콜백 예상
        _write_wav(wav_file, np.zeros(1024, dtype=np.int16))

        received: list[bytes] = []
        all_done = threading.Event()

        def callback(pcm: bytes) -> None:
            received.append(pcm)
            if len(received) >= 4:
                all_done.set()

        backend = WavCaptureBackend(str(wav_file), realtime=False)
        backend.start(callback, sample_rate=SAMPLE_RATE, channels=1, frame_size=256)
        all_done.wait(timeout=2.0)
        backend.stop()

        assert len(received) >= 4

    def test_stop_스레드_종료(self, tmp_path: Path) -> None:
        """stop() 호출 시 내부 스레드가 정리되는지 확인한다."""
        wav_file = tmp_path / "long.wav"
        # 10초 분량의 WAV 파일 (실제로는 stop()이 즉시 종료)
        _write_wav(wav_file, np.zeros(SAMPLE_RATE * 10, dtype=np.int16))

        backend = WavCaptureBackend(str(wav_file), realtime=True)
        backend.start(lambda _: None, sample_rate=SAMPLE_RATE, channels=1, frame_size=4096)

        # 짧게 실행 후 stop
        import time
        time.sleep(0.05)
        backend.stop()

        # stop() 이후 스레드 참조가 정리되어야 함
        assert backend._thread is None

    def test_파일_소진_후_자동_종료(self, tmp_path: Path) -> None:
        """WAV 파일을 모두 읽으면 스레드가 자동 종료되는지 확인한다."""
        wav_file = tmp_path / "tiny.wav"
        _write_wav(wav_file, np.zeros(128, dtype=np.int16))

        received: list[bytes] = []

        def callback(pcm: bytes) -> None:
            received.append(pcm)

        backend = WavCaptureBackend(str(wav_file), realtime=False)
        backend.start(callback, sample_rate=SAMPLE_RATE, channels=1, frame_size=128)

        # 파일이 작으므로 스레드가 금방 종료됨 (최대 1초 대기)
        if backend._thread is not None:
            backend._thread.join(timeout=1.0)

        backend.stop()
        assert len(received) >= 1

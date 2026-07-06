"""
test_player.py — AudioPlayer 큐 동작 단위 테스트

AudioPlayer의 재생 큐 관리 및 audio_clear 동작을 검증한다.
sounddevice 하드웨어 없이 MockPlaybackBackend를 사용하여 테스트한다.
"""

from __future__ import annotations

from collections import deque
from typing import Callable
from unittest.mock import MagicMock, patch

import numpy as np
import pytest

from device_emulator.audio import AudioPlayer, SounddevicePlaybackBackend


# ──────────────────────────────────────────────
# Mock 재생 백엔드
# ──────────────────────────────────────────────


class MockPlaybackBackend:
    """테스트용 재생 백엔드 mock.

    실제 sounddevice 스트림 대신 Python deque를 사용하여
    오디오 하드웨어 없이 AudioPlayer의 동작을 검증한다.
    """

    def __init__(self) -> None:
        # 재생 큐: write()로 추가된 데이터를 저장
        self.queue: deque[bytes] = deque()
        # 클리어 호출 횟수 추적
        self.clear_count: int = 0
        # 닫기 호출 횟수 추적
        self.close_count: int = 0

    def write(self, pcm_data: bytes) -> None:
        """PCM16 데이터를 재생 큐에 추가한다."""
        self.queue.append(pcm_data)

    def clear(self) -> None:
        """재생 큐를 비운다."""
        self.queue.clear()
        self.clear_count += 1

    def close(self) -> None:
        """재생 스트림을 종료한다."""
        self.close_count += 1


# ──────────────────────────────────────────────
# AudioPlayer 테스트
# ──────────────────────────────────────────────


class TestAudioPlayer:
    """AudioPlayer의 공개 인터페이스를 검증하는 테스트 클래스."""

    def _make_player(self) -> tuple[AudioPlayer, MockPlaybackBackend]:
        """테스트용 AudioPlayer와 MockPlaybackBackend 쌍을 생성한다."""
        backend = MockPlaybackBackend()
        player = AudioPlayer(backend=backend)
        return player, backend

    def test_play_큐에_데이터_추가(self) -> None:
        """play()가 PCM16 데이터를 재생 큐에 추가하는지 확인한다."""
        # Given: AudioPlayer와 mock 백엔드
        player, backend = self._make_player()
        pcm_data = np.zeros(1024, dtype=np.int16).tobytes()

        # When: PCM16 데이터 추가
        player.play(pcm_data)

        # Then: 백엔드 큐에 데이터가 들어있어야 함
        assert len(backend.queue) == 1
        assert backend.queue[0] == pcm_data

    def test_play_여러_청크_순서_유지(self) -> None:
        """여러 청크를 play()하면 순서대로 큐에 쌓이는지 확인한다."""
        player, backend = self._make_player()

        chunk1 = np.ones(512, dtype=np.int16).tobytes()
        chunk2 = (np.ones(512, dtype=np.int16) * 2).tobytes()
        chunk3 = (np.ones(512, dtype=np.int16) * 3).tobytes()

        player.play(chunk1)
        player.play(chunk2)
        player.play(chunk3)

        # 큐에 3개의 청크가 순서대로 들어있어야 함
        assert len(backend.queue) == 3
        assert list(backend.queue)[0] == chunk1
        assert list(backend.queue)[1] == chunk2
        assert list(backend.queue)[2] == chunk3

    def test_clear_큐_비움(self) -> None:
        """clear()가 재생 큐를 비우는지 확인한다."""
        player, backend = self._make_player()

        # 큐에 데이터 추가
        for _ in range(5):
            player.play(np.zeros(256, dtype=np.int16).tobytes())
        assert len(backend.queue) == 5

        # audio_clear 처리
        player.clear()

        # 큐가 비어있어야 함
        assert len(backend.queue) == 0
        assert backend.clear_count == 1

    def test_clear_여러번_호출_안전(self) -> None:
        """clear()를 여러 번 연속 호출해도 안전한지 확인한다."""
        player, backend = self._make_player()

        # 큐에 데이터 없이 여러 번 clear 호출
        player.clear()
        player.clear()
        player.clear()

        # 예외 없이 정상 완료되어야 함
        assert backend.clear_count == 3

    def test_close_백엔드_닫기(self) -> None:
        """close()가 재생 백엔드를 닫는지 확인한다."""
        player, backend = self._make_player()

        player.close()

        assert backend.close_count == 1

    def test_play_빈_데이터(self) -> None:
        """빈 PCM16 데이터를 play()해도 오류가 없는지 확인한다."""
        player, backend = self._make_player()

        # 빈 바이트 재생 시도
        player.play(b"")

        # 빈 데이터도 큐에 추가되어야 함 (오류 없음)
        assert len(backend.queue) == 1

    def test_play_후_clear_후_play(self) -> None:
        """play → clear → play 순서로 동작하는지 확인한다."""
        player, backend = self._make_player()

        chunk1 = np.ones(256, dtype=np.int16).tobytes()
        chunk2 = (np.ones(256, dtype=np.int16) * 2).tobytes()

        player.play(chunk1)
        player.clear()
        player.play(chunk2)

        # clear 후 추가된 데이터만 큐에 있어야 함
        assert len(backend.queue) == 1
        assert backend.queue[0] == chunk2


# ──────────────────────────────────────────────
# SounddevicePlaybackBackend 콜백 로직 단위 테스트
# (실제 sounddevice 스트림 없이 콜백 메서드만 테스트)
# ──────────────────────────────────────────────


class TestSounddevicePlaybackBackendCallback:
    """SounddevicePlaybackBackend._sd_callback()의 논리를 검증하는 테스트 클래스.

    실제 sounddevice OutputStream은 mock으로 대체하여
    콜백 내부 로직만 격리 테스트한다.
    PortAudio 하드웨어가 없는 CI 환경에서도 동작하도록
    sys.modules 레벨에서 sounddevice 전체를 mock한다.
    """

    def _make_backend(self) -> SounddevicePlaybackBackend:
        """sounddevice 모듈을 sys.modules 수준에서 mock으로 대체하여 백엔드를 생성한다.

        PortAudio가 설치되지 않은 CI 환경에서도 sounddevice 임포트가
        성공하도록 sys.modules에 mock 모듈을 주입한다.
        """
        import sys

        mock_sd = MagicMock()
        mock_stream = MagicMock()
        mock_sd.OutputStream.return_value = mock_stream

        # sys.modules에 mock sounddevice를 주입하여 임포트 실패 방지
        with patch.dict(sys.modules, {"sounddevice": mock_sd}):
            backend = SounddevicePlaybackBackend(sample_rate=24000)
        return backend

    def test_큐_비었을때_무음_출력(self) -> None:
        """큐가 비었을 때 콜백이 무음(0)을 출력하는지 확인한다."""
        backend = self._make_backend()
        frames = 1024
        # (frames, 1) 형상의 int16 출력 버퍼 (초기값: 쓰레기 값)
        outdata = np.ones((frames, 1), dtype=np.int16) * 999

        # When: 빈 큐로 콜백 호출
        backend._sd_callback(outdata, frames, None, None)

        # Then: 모든 샘플이 0 (무음)이어야 함
        assert np.all(outdata == 0)

    def test_한_청크_재생(self) -> None:
        """큐에 한 청크가 있을 때 콜백이 해당 데이터를 출력하는지 확인한다."""
        backend = self._make_backend()
        frames = 100
        # 0..99 값의 PCM16 데이터
        pcm_arr = np.arange(frames, dtype=np.int16)
        backend.write(pcm_arr.tobytes())

        outdata = np.zeros((frames, 1), dtype=np.int16)
        backend._sd_callback(outdata, frames, None, None)

        # 큐에서 읽은 데이터와 출력 버퍼가 일치해야 함
        assert np.array_equal(outdata[:, 0], pcm_arr)

    def test_partial_청크_분할_읽기(self) -> None:
        """청크보다 작은 프레임 크기로 콜백 시 분할 읽기를 확인한다."""
        backend = self._make_backend()

        # 200 샘플 청크를 큐에 추가
        pcm_arr = np.arange(200, dtype=np.int16)
        backend.write(pcm_arr.tobytes())

        # 첫 번째 콜백: 100 샘플 읽기
        outdata1 = np.zeros((100, 1), dtype=np.int16)
        backend._sd_callback(outdata1, 100, None, None)

        # 두 번째 콜백: 나머지 100 샘플 읽기
        outdata2 = np.zeros((100, 1), dtype=np.int16)
        backend._sd_callback(outdata2, 100, None, None)

        # 전체 데이터가 순서대로 출력되어야 함
        combined = np.concatenate([outdata1[:, 0], outdata2[:, 0]])
        assert np.array_equal(combined, pcm_arr)

    def test_clear_플래그_무음_출력(self) -> None:
        """clear() 호출 후 콜백이 무음을 출력하는지 확인한다."""
        backend = self._make_backend()

        # 큐에 데이터 추가 후 클리어
        pcm_arr = np.ones(100, dtype=np.int16) * 1000
        backend.write(pcm_arr.tobytes())
        backend.clear()

        outdata = np.ones((100, 1), dtype=np.int16) * 999
        backend._sd_callback(outdata, 100, None, None)

        # clear 후에는 무음이어야 함
        assert np.all(outdata == 0)

    def test_청크_소진_경계_넘어_다음_청크_재생(self) -> None:
        """한 콜백에서 현재 청크를 다 쓴 뒤 다음 청크로 이어서 재생하는지 확인한다.

        회귀 테스트: 이전 구현은 루프 종료 조건에서 샘플 위치(_pos)를
        바이트 길이(len(bytes))와 비교하여, 청크를 모두 소비한 뒤에도
        다음 청크를 꺼내지 못하고 while 루프가 무한히 도는 버그가 있었다.
        그 결과 첫 청크 이후 오디오 스레드가 멈춰 "안 안 안..." 스터터가 발생했다.

        하나의 콜백이 두 청크의 총 샘플 수를 요청하면,
        두 청크가 끊김 없이 이어져 출력되고 콜백이 즉시 반환되어야 한다.
        """
        backend = self._make_backend()

        chunk1 = np.arange(0, 300, dtype=np.int16)
        chunk2 = np.arange(300, 500, dtype=np.int16)
        backend.write(chunk1.tobytes())
        backend.write(chunk2.tobytes())

        frames = len(chunk1) + len(chunk2)  # 500 = 청크 경계를 가로지름
        outdata = np.zeros((frames, 1), dtype=np.int16)

        # 버그가 있으면 콜백이 무한 루프에 빠지므로, 워커 스레드에서 실행하고
        # 타임아웃으로 실패를 감지한다 (테스트 스위트 전체가 멈추지 않도록).
        import threading

        done = threading.Event()

        def _run() -> None:
            backend._sd_callback(outdata, frames, None, None)
            done.set()

        t = threading.Thread(target=_run, daemon=True)
        t.start()
        assert done.wait(timeout=3.0), "콜백이 청크 경계에서 무한 루프에 빠졌습니다"

        expected = np.concatenate([chunk1, chunk2])
        assert np.array_equal(outdata[:, 0], expected)

    def test_clear_후_새_데이터_재생(self) -> None:
        """clear() 후에 새 데이터를 추가하면 정상 재생되는지 확인한다."""
        backend = self._make_backend()

        # 데이터 추가 후 클리어
        backend.write(np.ones(100, dtype=np.int16).tobytes())
        backend.clear()

        # clear 처리 콜백
        outdata0 = np.zeros((100, 1), dtype=np.int16)
        backend._sd_callback(outdata0, 100, None, None)  # cleared=True 처리

        # 새 데이터 추가
        new_pcm = np.arange(100, dtype=np.int16)
        backend.write(new_pcm.tobytes())

        outdata = np.zeros((100, 1), dtype=np.int16)
        backend._sd_callback(outdata, 100, None, None)

        # 새 데이터가 정상 출력되어야 함
        assert np.array_equal(outdata[:, 0], new_pcm)

    def test_clear_직후_새_데이터의_시작이_잘리지_않음(self) -> None:
        """clear() 직후 후속 콜백 이전에 새 오디오가 큐에 들어오면,
        그 시작 부분이 무음으로 버려지지 않고 즉시 재생되는지 확인한다.

        회귀 테스트: 이전 구현은 clear() 시 _cleared 플래그를 세워, 다음 콜백이
        큐에 새 데이터가 있어도 블록 전체(최대 blocksize=4096, ~170ms)를 무음으로
        채우고 반환했다. 그 결과 서버가 audio_clear 직후 보낸 다음 발화의 시작이
        잘려 "말의 앞부분을 먹는" 현상이 발생했다.
        """
        backend = self._make_backend()

        # 재생 중 audio_clear 수신
        backend.write(np.ones(100, dtype=np.int16).tobytes())
        backend.clear()

        # audio_clear 직후, 다음 콜백 이전에 새 응답 오디오가 도착한다
        new_pcm = np.arange(1, 101, dtype=np.int16)
        backend.write(new_pcm.tobytes())

        # 단 한 번의 콜백에서 새 데이터의 시작이 그대로 재생되어야 한다
        outdata = np.zeros((100, 1), dtype=np.int16)
        backend._sd_callback(outdata, 100, None, None)

        assert np.array_equal(outdata[:, 0], new_pcm), "clear 직후 새 발화의 시작이 잘렸습니다"


    def test_is_playing_큐_비면_False(self) -> None:
        """재생할 데이터가 없으면 is_playing()이 False인지 확인한다."""
        backend = self._make_backend()
        assert backend.is_playing() is False

    def test_is_playing_데이터_있으면_True(self) -> None:
        """큐에 데이터가 있으면 is_playing()이 True인지 확인한다."""
        backend = self._make_backend()
        backend.write(np.ones(100, dtype=np.int16).tobytes())
        assert backend.is_playing() is True

    def test_is_playing_모두_소비하면_False(self) -> None:
        """큐의 데이터를 모두 재생하면 is_playing()이 False인지 확인한다."""
        backend = self._make_backend()
        backend.write(np.ones(100, dtype=np.int16).tobytes())
        outdata = np.zeros((100, 1), dtype=np.int16)
        backend._sd_callback(outdata, 100, None, None)
        assert backend.is_playing() is False

    def test_is_playing_clear_후_False(self) -> None:
        """clear() 후에는 is_playing()이 False인지 확인한다."""
        backend = self._make_backend()
        backend.write(np.ones(100, dtype=np.int16).tobytes())
        backend.clear()
        assert backend.is_playing() is False

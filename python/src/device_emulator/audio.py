"""
audio.py — 마이크 캡처 및 오디오 재생 모듈

PCM16 / 24kHz / 1채널(모노) 포맷으로:
  - MicCapture: 마이크 입력을 캡처하여 콜백으로 전달
  - AudioPlayer: 수신된 PCM 데이터를 스피커로 지연 없이 순차 재생

sounddevice 라이브러리를 사용하여 PortAudio 기반 I/O를 수행한다.
하드웨어 없이 테스트할 수 있도록 인터페이스와 구현을 분리한다.
"""

from __future__ import annotations

import logging
import threading
from collections import deque
from typing import Callable, Protocol, runtime_checkable

import numpy as np

log = logging.getLogger("device_emulator.audio")

# ──────────────────────────────────────────────
# 오디오 포맷 상수
# ──────────────────────────────────────────────

# 샘플레이트: 24kHz (서버 스펙에 맞춤)
SAMPLE_RATE: int = 24_000

# 채널 수: 1 (모노)
CHANNELS: int = 1

# sounddevice 데이터 타입: 16비트 정수 PCM
DTYPE: str = "int16"

# 기본 프레임 크기: 4096 샘플 (~170ms @ 24kHz)
# 프론트엔드 DEFAULT_FRAME_SIZE와 동일하게 설정
DEFAULT_FRAME_SIZE: int = 4096


def pcm16_bytes_to_float32(data: bytes) -> np.ndarray:
    """PCM16 바이트 배열을 float32 numpy 배열로 변환한다.

    sounddevice가 float32 콜백을 사용할 때 필요한 변환 함수.
    -32768..32767 범위의 int16을 -1.0..1.0 범위의 float32로 정규화.

    Args:
        data: PCM16 (int16) 바이트 배열

    Returns:
        float32 numpy 1D 배열 (값 범위: -1.0 ~ 1.0)
    """
    # int16 numpy 배열로 해석 후 float32로 정규화
    arr = np.frombuffer(data, dtype=np.int16).astype(np.float32)
    return arr / 32768.0


def float32_to_pcm16_bytes(arr: np.ndarray) -> bytes:
    """float32 numpy 배열을 PCM16 바이트 배열로 변환한다.

    sounddevice float32 콜백 입력을 int16 PCM 바이트로 직렬화.
    -1.0..1.0 범위의 float32를 클리핑 후 int16으로 변환.

    Args:
        arr: float32 numpy 1D 배열 (값 범위: -1.0 ~ 1.0)

    Returns:
        PCM16 (int16) 바이트 배열
    """
    # -1.0~1.0 범위를 벗어나는 값을 클리핑하고 int16으로 변환
    clipped = np.clip(arr, -1.0, 1.0)
    return (clipped * 32767.0).astype(np.int16).tobytes()


def pcm16_rms(data: bytes) -> float:
    """PCM16 청크의 정규화 RMS(제곱평균제곱근) 레벨을 계산한다.

    업링크로 전송되는 오디오가 실제로 "무음이 아닌지" 판단하는 데 사용한다.
    반환값은 0.0(완전 무음) ~ 1.0(풀스케일) 범위로 정규화된다.

    Args:
        data: PCM16 (int16) 바이트 배열

    Returns:
        정규화된 RMS 레벨 (0.0 ~ 1.0). 빈 데이터는 0.0.
    """
    if not data:
        return 0.0
    # int16 → float(-1.0~1.0) 정규화 후 RMS 계산
    arr = np.frombuffer(data, dtype=np.int16).astype(np.float64) / 32768.0
    return float(np.sqrt(np.mean(np.square(arr))))


class UplinkMeter:
    """마이크→서버 업링크 오디오의 관측(observability) 계측기.

    누적 전송 바이트와 오디오 레벨(RMS)을 추적하여, interval_seconds 분량의
    오디오가 쌓일 때마다 사람이 읽을 수 있는 요약 로그 문자열을 반환한다.
    이를 통해 "로그는 스트리밍이라는데 실제로 마이크 소리가 서버로 가고 있는지"
    확인할 수 있고, 완전 무음이면 경고를 표시한다(마이크 권한/장치 문제 신호).

    벽시계(wall clock)가 아니라 누적 샘플 수로 interval을 판단하므로
    결정적(deterministic)이며 테스트하기 쉽다.
    """

    def __init__(
        self,
        sample_rate: int = SAMPLE_RATE,
        interval_seconds: float = 2.0,
        silence_threshold: float = 1e-3,
    ) -> None:
        """
        Args:
            sample_rate: 오디오 샘플레이트 (Hz)
            interval_seconds: 요약 로그를 출력하는 주기 (초)
            silence_threshold: 이 값 이하의 RMS는 "무음"으로 간주
        """
        self._sample_rate = sample_rate
        self._interval_samples = int(interval_seconds * sample_rate)
        self._silence_threshold = silence_threshold

        # 세션 전체 누적 전송 바이트
        self._total_bytes = 0
        # 현재 윈도우의 샘플 수 및 RMS 누적(제곱합)
        self._window_samples = 0
        self._sq_sum = 0.0
        self._peak = 0.0

    def observe(self, pcm_data: bytes) -> str | None:
        """업링크로 전송한 PCM16 청크를 관측한다.

        누적 오디오가 interval을 넘으면 요약 문자열을 반환하고 윈도우를 리셋한다.
        그렇지 않으면 None을 반환한다.

        Args:
            pcm_data: 방금 전송한 PCM16 바이트 청크

        Returns:
            요약 로그 문자열 또는 None
        """
        n = len(pcm_data)
        self._total_bytes += n

        if pcm_data:
            arr = np.frombuffer(pcm_data, dtype=np.int16).astype(np.float64) / 32768.0
            self._sq_sum += float(np.sum(np.square(arr)))
            self._window_samples += arr.size
            self._peak = max(self._peak, float(np.max(np.abs(arr))) if arr.size else 0.0)

        if self._window_samples < self._interval_samples:
            return None

        # 윈도우 RMS 계산
        rms = np.sqrt(self._sq_sum / self._window_samples) if self._window_samples else 0.0
        seconds = self._total_bytes / (self._sample_rate * 2)  # PCM16 = 2 bytes/sample

        if rms <= self._silence_threshold:
            msg = (
                f"[업링크] 전송 {self._total_bytes:,} bytes (~{seconds:.1f}s) — "
                f"⚠️ 무음 감지 (RMS={rms:.5f}). 마이크 입력이 없습니다. "
                f"(macOS 마이크 권한 / 입력 장치 확인 필요)"
            )
        else:
            msg = (
                f"[업링크] 전송 {self._total_bytes:,} bytes (~{seconds:.1f}s), "
                f"레벨 RMS={rms:.4f} peak={self._peak:.4f} — 마이크 오디오 스트리밍 중"
            )

        # 윈도우 리셋 (누적 바이트는 세션 전체 유지)
        self._window_samples = 0
        self._sq_sum = 0.0
        self._peak = 0.0
        return msg


class EchoGate:
    """반이중(half-duplex) 에코 억제 게이트.

    스피커가 재생 중일 때(그리고 재생 종료 후 짧은 행오버 동안) 마이크 업링크를
    억제하여, 장치가 자기 스피커 출력을 마이크로 다시 잡아 서버 VAD가 사용자
    발화로 오인(바지인)하고 audio_clear로 재생을 끊는 에코 루프를 방지한다.

    실제 LG ThinQ 장치는 하드웨어 AEC(음향 에코 제거)로 풀듀플렉스 바지인을
    지원하지만, PoC 에뮬레이터(노트북 마이크/스피커)에서는 AEC가 없으므로
    반이중 억제가 현실적이고 깔끔한 대안이다. (--allow-barge-in 으로 비활성화)
    """

    def __init__(self, hangover_seconds: float = 0.25) -> None:
        """
        Args:
            hangover_seconds: 재생 종료 후에도 업링크를 억제할 여유 시간(초).
                              스피커 잔향/버퍼 지연으로 인한 꼬리 에코를 막는다.
        """
        self._hangover = hangover_seconds
        self._suppress_until = 0.0

    def should_send(self, is_playing: bool, now: float) -> bool:
        """현재 마이크 청크를 업링크로 보내도 되는지 판단한다.

        Args:
            is_playing: 스피커가 현재 오디오를 재생 중인지 여부
            now: 단조 증가 시각(초). 보통 event loop.time().

        Returns:
            전송 허용이면 True, 억제(드롭)면 False.
        """
        if is_playing:
            # 재생 중이면 억제하고, 종료 후 행오버까지 억제 창을 연장한다
            self._suppress_until = now + self._hangover
            return False
        if now < self._suppress_until:
            return False
        return True


# ──────────────────────────────────────────────
# 오디오 I/O 인터페이스 (테스트 가능성을 위한 프로토콜 정의)
# ──────────────────────────────────────────────

@runtime_checkable
class AudioCaptureBackend(Protocol):
    """마이크 캡처 백엔드 인터페이스.

    실제 구현은 SounddeviceCaptureBackend를 사용하고,
    테스트에서는 MockCaptureBackend로 대체한다.
    """

    def start(
        self,
        callback: Callable[[bytes], None],
        sample_rate: int,
        channels: int,
        frame_size: int,
    ) -> None:
        """캡처 스트림을 시작한다."""
        ...

    def stop(self) -> None:
        """캡처 스트림을 종료한다."""
        ...


@runtime_checkable
class AudioPlaybackBackend(Protocol):
    """오디오 재생 백엔드 인터페이스.

    실제 구현은 SounddevicePlaybackBackend를 사용하고,
    테스트에서는 MockPlaybackBackend로 대체한다.
    """

    def write(self, pcm_data: bytes) -> None:
        """PCM16 바이트를 재생 큐에 추가한다."""
        ...

    def clear(self) -> None:
        """재생 중인 오디오와 큐를 즉시 비운다."""
        ...

    def is_playing(self) -> bool:
        """현재 재생 중이거나 재생 대기 중인 오디오가 있으면 True."""
        ...

    def close(self) -> None:
        """재생 스트림을 종료한다."""
        ...


# ──────────────────────────────────────────────
# sounddevice 기반 실제 구현
# ──────────────────────────────────────────────


class SounddeviceCaptureBackend:
    """sounddevice InputStream을 사용한 마이크 캡처 백엔드.

    캡처된 float32 샘플을 PCM16 바이트로 변환하여 콜백에 전달한다.
    """

    def __init__(self) -> None:
        # sounddevice InputStream 인스턴스 (start() 호출 후 초기화)
        self._stream = None

    def start(
        self,
        callback: Callable[[bytes], None],
        sample_rate: int = SAMPLE_RATE,
        channels: int = CHANNELS,
        frame_size: int = DEFAULT_FRAME_SIZE,
    ) -> None:
        """마이크 캡처 스트림을 시작한다.

        sounddevice InputStream을 열고, 오디오 콜백에서
        float32 샘플을 PCM16 바이트로 변환하여 callback에 전달한다.

        Args:
            callback: PCM16 바이트를 수신할 콜백 함수
            sample_rate: 샘플레이트 (기본값: 24000)
            channels: 채널 수 (기본값: 1, 모노)
            frame_size: 프레임당 샘플 수 (기본값: 4096)
        """
        import sounddevice as sd  # 지연 임포트: 하드웨어 없는 환경에서 임포트 실패 방지

        def _sd_callback(indata: np.ndarray, frames: int, time_info: object, status: object) -> None:
            # sounddevice 콜백: (frames, channels) float32 배열 → PCM16 바이트
            if status:
                log.warning("마이크 캡처 상태 이상: %s", status)
            # 모노(1채널)이므로 첫 번째 채널만 사용
            pcm_bytes = float32_to_pcm16_bytes(indata[:, 0])
            try:
                callback(pcm_bytes)
            except Exception:
                log.exception("마이크 콜백 처리 중 오류")

        self._stream = sd.InputStream(
            samplerate=sample_rate,
            channels=channels,
            dtype="float32",
            blocksize=frame_size,
            callback=_sd_callback,
        )
        self._stream.start()
        log.info("마이크 캡처 시작: %dHz, %dch, frame=%d", sample_rate, channels, frame_size)

    def stop(self) -> None:
        """마이크 캡처 스트림을 종료한다."""
        if self._stream is not None:
            self._stream.stop()
            self._stream.close()
            self._stream = None
            log.info("마이크 캡처 종료")


class SounddevicePlaybackBackend:
    """sounddevice OutputStream을 사용한 저지연 오디오 재생 백엔드.

    TypeScript AudioPlayer와 동일한 방식으로 연속 예약 재생을 구현한다:
    - deque(큐)에 PCM16 데이터를 쌓고
    - OutputStream 콜백에서 순서대로 드레인하여 갭 없는 재생을 보장
    - clear() 호출 시 큐를 즉시 비워 재생을 중단(바지인 지원)
    """

    def __init__(self, sample_rate: int = SAMPLE_RATE) -> None:
        # 재생 대기 PCM16 데이터 큐 (bytes 단위)
        self._queue: deque[bytes] = deque()

        # 현재 재생 중인 청크(int16 샘플 배열)와 위치(샘플 단위)
        self._current: np.ndarray | None = None
        self._pos: int = 0

        # 큐 및 재생 상태 보호용 뮤텍스
        self._lock = threading.Lock()

        # sounddevice OutputStream 초기화 및 시작
        import sounddevice as sd  # 지연 임포트

        self._stream = sd.OutputStream(
            samplerate=sample_rate,
            channels=CHANNELS,
            dtype=DTYPE,
            blocksize=DEFAULT_FRAME_SIZE,
            callback=self._sd_callback,
        )
        self._stream.start()
        log.info("오디오 재생 스트림 시작: %dHz", sample_rate)

    def _sd_callback(
        self,
        outdata: np.ndarray,
        frames: int,
        time_info: object,
        status: object,
    ) -> None:
        """sounddevice 재생 콜백: 큐에서 PCM 데이터를 드레인하여 outdata를 채운다.

        이 콜백은 sounddevice 내부 오디오 스레드에서 호출된다.
        큐가 비어 있으면 무음(0)으로 채운다.

        Args:
            outdata: 채워야 할 출력 버퍼 (frames, channels) int16
            frames: 이번 콜백에서 채워야 할 샘플 수
            time_info: PortAudio 타임스탬프 (미사용)
            status: 스트림 상태 플래그
        """
        if status:
            log.warning("오디오 재생 상태 이상: %s", status)

        with self._lock:
            remaining = frames
            write_pos = 0

            while remaining > 0:
                if self._current is None or self._pos >= len(self._current):
                    # 현재 청크를 모두 소비했으면 다음 청크 꺼내기
                    if self._queue:
                        # bytes → int16 샘플 배열로 변환하여 저장
                        # (_pos/len 비교를 샘플 단위로 일치시켜 무한 루프 방지)
                        self._current = np.frombuffer(self._queue.popleft(), dtype=np.int16)
                        self._pos = 0
                    else:
                        # 큐가 비었으면 나머지를 무음(0)으로 채움
                        outdata[write_pos:].fill(0)
                        break

                # 현재 청크에서 읽을 수 있는 샘플 수 계산
                chunk_arr = self._current
                available = len(chunk_arr) - self._pos
                if available <= 0:
                    # 방어적 처리: 소진된 청크는 다음 반복에서 교체된다
                    self._current = None
                    continue
                to_copy = min(available, remaining)

                # outdata는 (frames, 1) 형상; 모노이므로 첫 번째 채널에만 기록
                outdata[write_pos : write_pos + to_copy, 0] = chunk_arr[self._pos : self._pos + to_copy]
                self._pos += to_copy
                write_pos += to_copy
                remaining -= to_copy

    def write(self, pcm_data: bytes) -> None:
        """PCM16 바이트 데이터를 재생 큐에 추가한다.

        큐에 쌓인 데이터는 콜백에서 순서대로 재생된다.
        갭 없는 연속 재생을 위해 큐 방식을 사용한다.

        Args:
            pcm_data: PCM16 (int16) 바이트 배열
        """
        with self._lock:
            self._queue.append(pcm_data)

    def clear(self) -> None:
        """재생 중인 오디오와 큐를 즉시 비운다 (바지인 처리).

        audio_clear 메시지 수신 시 호출된다.
        큐를 비우고 현재 청크를 폐기하여, 다음 콜백부터 무음이 출력된다.
        (별도의 클리어 플래그로 블록 전체를 무음 처리하지 않으므로,
        클리어 직후 도착한 새 발화의 시작이 잘리지 않는다.)
        """
        with self._lock:
            self._queue.clear()
            self._current = None
            self._pos = 0
        log.info("오디오 재생 큐 비움 (audio_clear)")

    def is_playing(self) -> bool:
        """재생 중이거나 재생 대기 중인 오디오가 있으면 True를 반환한다.

        에코 억제(반이중)에서 스피커 활성 여부를 판단하는 데 사용한다.
        """
        with self._lock:
            if self._queue:
                return True
            return self._current is not None and self._pos < len(self._current)

    def close(self) -> None:
        """재생 스트림을 종료하고 리소스를 해제한다."""
        if self._stream is not None:
            self._stream.stop()
            self._stream.close()
            self._stream = None
            log.info("오디오 재생 스트림 종료")


# ──────────────────────────────────────────────
# 파일 기반 백엔드 (헤드리스/CI 모드)
# ──────────────────────────────────────────────


class WavCaptureBackend:
    """WAV 파일을 마이크 소스로 사용하는 파일 기반 캡처 백엔드.

    실제 마이크 대신 WAV 파일에서 PCM 데이터를 읽어
    실시간 속도로 콜백에 전달한다.
    CI 헤드리스 환경에서 --audio-source 옵션과 함께 사용한다.
    """

    def __init__(self, path: str, realtime: bool = True) -> None:
        # WAV 소스 파일 경로
        self._path = path
        # 실시간 속도 페이싱 여부 (False이면 최대 속도로 전송)
        self._realtime = realtime
        # 읽기 스레드와 종료 이벤트
        self._thread: threading.Thread | None = None
        self._stop_event = threading.Event()

    def start(
        self,
        callback: Callable[[bytes], None],
        sample_rate: int = SAMPLE_RATE,
        channels: int = CHANNELS,
        frame_size: int = DEFAULT_FRAME_SIZE,
    ) -> None:
        """WAV 파일 읽기 스레드를 시작한다.

        Args:
            callback: PCM16 바이트를 수신할 콜백 함수
            sample_rate: 샘플레이트 (기본값: 24000)
            channels: 채널 수 (기본값: 1, 미사용 — WAV 헤더 기준)
            frame_size: 프레임당 샘플 수 (기본값: 4096)
        """
        self._stop_event.clear()
        self._thread = threading.Thread(
            target=self._read_loop,
            args=(callback, sample_rate, frame_size),
            daemon=True,
            name="wav-capture",
        )
        self._thread.start()
        log.info("WAV 파일 캡처 시작: %s", self._path)

    def _read_loop(
        self,
        callback: Callable[[bytes], None],
        sample_rate: int,
        frame_size: int,
    ) -> None:
        """WAV 파일을 실시간 속도로 읽어 콜백에 전달하는 루프.

        _stop_event가 설정되면 루프를 즉시 종료한다.
        """
        import wave

        # 프레임 크기에 해당하는 재생 시간 (실시간 페이싱용)
        frame_duration = frame_size / sample_rate

        try:
            with wave.open(self._path, "rb") as wf:
                while not self._stop_event.is_set():
                    pcm_bytes = wf.readframes(frame_size)
                    if not pcm_bytes:
                        break
                    callback(pcm_bytes)
                    if self._realtime:
                        # 실시간 속도 유지: stop 요청 시 즉시 깨어남
                        self._stop_event.wait(timeout=frame_duration)
        except Exception:
            log.exception("WAV 파일 읽기 중 오류: %s", self._path)
        finally:
            log.info("WAV 파일 캡처 완료: %s", self._path)

    def stop(self) -> None:
        """WAV 파일 읽기 스레드를 종료한다."""
        self._stop_event.set()
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
        log.info("WAV 파일 캡처 종료")


class WavPlaybackBackend:
    """수신된 오디오를 WAV 파일로 저장하는 파일 기반 재생 백엔드.

    스피커 대신 수신된 PCM16 다운링크 오디오를 메모리에 버퍼링하고,
    close() 시점에 WAV 파일로 저장한다.
    CI 헤드리스 환경에서 --audio-out 옵션과 함께 사용한다.
    """

    def __init__(self, path: str, sample_rate: int = SAMPLE_RATE) -> None:
        # 출력 WAV 파일 경로
        self._path = path
        self._sample_rate = sample_rate
        # 수신된 PCM16 데이터 청크 버퍼
        self._chunks: list[bytes] = []
        self._lock = threading.Lock()

    def write(self, pcm_data: bytes) -> None:
        """PCM16 데이터를 버퍼에 추가한다.

        Args:
            pcm_data: PCM16 (int16) 바이트 배열
        """
        with self._lock:
            self._chunks.append(pcm_data)

    def clear(self) -> None:
        """버퍼를 비운다 (audio_clear/바지인 처리)."""
        with self._lock:
            self._chunks.clear()
        log.info("WAV 재생 버퍼 비움 (audio_clear)")

    def is_playing(self) -> bool:
        """파일 재생 백엔드는 실제 스피커가 아니므로 항상 False를 반환한다.

        (헤드리스/CI 모드에서는 음향 에코가 없어 에코 억제가 불필요하다.)
        """
        return False

    def close(self) -> None:
        """버퍼에 쌓인 PCM16 데이터를 WAV 파일로 저장한다.

        수신된 오디오가 없으면 파일을 생성하지 않는다.
        """
        import wave

        with self._lock:
            chunks = list(self._chunks)

        if not chunks:
            log.info("WAV 저장 건너뜀: 수신된 오디오 없음")
            return

        pcm_data = b"".join(chunks)

        try:
            with wave.open(self._path, "wb") as wf:
                wf.setnchannels(CHANNELS)
                wf.setsampwidth(2)  # PCM16 = 2 bytes/sample
                wf.setframerate(self._sample_rate)
                wf.writeframes(pcm_data)
            log.info("WAV 저장 완료: %s (%d 바이트)", self._path, len(pcm_data))
        except Exception:
            log.exception("WAV 파일 저장 중 오류: %s", self._path)


# ──────────────────────────────────────────────
# 고수준 인터페이스: MicCapture / AudioPlayer
# ──────────────────────────────────────────────


class MicCapture:
    """마이크 캡처를 관리하는 고수준 클래스.

    백엔드를 주입받아 실제 sounddevice 또는 테스트용 mock과 함께 동작.
    PCM16/24kHz/모노 포맷으로 오디오를 캡처하여 콜백에 전달한다.
    """

    def __init__(
        self,
        backend: AudioCaptureBackend | None = None,
        sample_rate: int = SAMPLE_RATE,
        frame_size: int = DEFAULT_FRAME_SIZE,
    ) -> None:
        """MicCapture 초기화.

        Args:
            backend: 캡처 백엔드 (None이면 SounddeviceCaptureBackend 사용)
            sample_rate: 캡처 샘플레이트 (기본값: 24000)
            frame_size: 프레임당 샘플 수 (기본값: 4096)
        """
        # 백엔드가 주입되지 않으면 실제 sounddevice 백엔드 사용
        self._backend = backend or SounddeviceCaptureBackend()
        self._sample_rate = sample_rate
        self._frame_size = frame_size
        self._running = False

    def start(self, on_chunk: Callable[[bytes], None]) -> None:
        """마이크 캡처를 시작한다.

        PCM16 바이트를 수신하면 on_chunk 콜백을 호출한다.
        WebSocket 전송 시 이 콜백에서 ws.send(pcm_bytes)를 호출한다.

        Args:
            on_chunk: PCM16 바이트를 수신할 콜백 함수
        """
        if self._running:
            log.warning("마이크 캡처가 이미 실행 중입니다")
            return
        self._backend.start(
            callback=on_chunk,
            sample_rate=self._sample_rate,
            channels=CHANNELS,
            frame_size=self._frame_size,
        )
        self._running = True

    def stop(self) -> None:
        """마이크 캡처를 종료한다."""
        if not self._running:
            return
        self._backend.stop()
        self._running = False

    @property
    def sample_rate(self) -> int:
        """현재 설정된 샘플레이트를 반환한다."""
        return self._sample_rate

    @property
    def frame_size(self) -> int:
        """현재 설정된 프레임 크기를 반환한다."""
        return self._frame_size


class AudioPlayer:
    """저지연 오디오 재생을 관리하는 고수준 클래스.

    백엔드를 주입받아 실제 sounddevice 또는 테스트용 mock과 함께 동작.
    PCM16/24kHz/모노 포맷으로 수신된 오디오를 갭 없이 순차 재생한다.

    TypeScript AudioPlayer 클래스와 동일한 인터페이스를 Python으로 구현:
      - play(): 청크를 큐에 추가하여 순차 재생
      - clear(): 재생 즉시 중단 (audio_clear/바지인 처리)
    """

    def __init__(
        self,
        backend: AudioPlaybackBackend | None = None,
        sample_rate: int = SAMPLE_RATE,
    ) -> None:
        """AudioPlayer 초기화.

        Args:
            backend: 재생 백엔드 (None이면 SounddevicePlaybackBackend 사용)
            sample_rate: 재생 샘플레이트 (기본값: 24000)
        """
        # 백엔드가 주입되지 않으면 실제 sounddevice 백엔드 사용
        self._backend = backend or SounddevicePlaybackBackend(sample_rate)

    def play(self, pcm_data: bytes) -> None:
        """PCM16 바이트 데이터를 재생 큐에 추가한다.

        서버로부터 수신한 이진 WebSocket 프레임을 즉시 재생 큐에 넣는다.
        갭 없는 연속 재생을 위해 큐 방식을 사용한다.

        Args:
            pcm_data: PCM16 (int16) 바이트 배열
        """
        self._backend.write(pcm_data)

    def clear(self) -> None:
        """재생 중인 오디오와 큐를 즉시 비운다 (바지인 처리).

        서버에서 audio_clear 메시지를 수신했을 때 호출된다.
        현재 재생 중인 오디오를 즉시 중단하고 대기 중인 청크도 모두 버린다.
        """
        self._backend.clear()

    def is_playing(self) -> bool:
        """스피커가 현재 오디오를 재생 중이거나 대기 중이면 True를 반환한다.

        에코 억제(반이중)에서 마이크 업링크 억제 여부를 결정하는 데 사용한다.
        """
        return self._backend.is_playing()

    def close(self) -> None:
        """재생 스트림을 종료하고 리소스를 해제한다."""
        self._backend.close()

import threading

import numpy as np
import sounddevice as sd


SAMPLE_RATE = 16_000
MIN_RECORDING_SECONDS = 0.25
MAX_RECORDING_SECONDS = 120


class AudioRecorder:
    def __init__(self) -> None:
        self._stream: sd.InputStream | None = None
        self._lock = threading.Lock()
        self._chunks: list[np.ndarray] = []
        self._sample_count = 0
        self._problem: str | None = None
        self._limit = SAMPLE_RATE * MAX_RECORDING_SECONDS

    @property
    def is_recording(self) -> bool:
        return self._stream is not None

    def _callback(self, indata, frames, time_info, status) -> None:
        with self._lock:
            if status:
                self._problem = f"Сбой записи микрофона: {status}"
            remaining = self._limit - self._sample_count
            if remaining <= 0:
                self._problem = (
                    f"Запись превышает {MAX_RECORDING_SECONDS} секунд. "
                    "Отпустите клавиши и повторите более короткую фразу."
                )
                raise sd.CallbackStop
            chunk = indata[:remaining, 0].copy()
            self._chunks.append(chunk)
            self._sample_count += len(chunk)

    def start(self) -> None:
        if self.is_recording:
            return
        with self._lock:
            self._chunks.clear()
            self._sample_count = 0
            self._problem = None
        stream = None
        try:
            device = sd.query_devices(kind="input")
            if device["max_input_channels"] < 1:
                raise RuntimeError("Микрофон по умолчанию не найден.")
            sd.check_input_settings(
                channels=1, samplerate=SAMPLE_RATE, dtype="float32"
            )
            stream = sd.InputStream(
                samplerate=SAMPLE_RATE,
                channels=1,
                dtype="float32",
                callback=self._callback,
            )
            stream.start()
            self._stream = stream
        except Exception as exc:
            if stream is not None:
                stream.close()
            self.discard()
            raise RuntimeError(
                "Не удалось включить микрофон. Проверьте устройство по умолчанию "
                "и доступ настольных приложений к микрофону в параметрах Windows."
            ) from exc

    def _close_stream(self, abort: bool = False) -> None:
        stream, self._stream = self._stream, None
        if stream is not None:
            try:
                if abort:
                    stream.abort()
                else:
                    stream.stop()
            finally:
                stream.close()

    def stop(self) -> np.ndarray:
        if not self.is_recording:
            raise RuntimeError("Запись микрофона не запущена.")
        try:
            self._close_stream()
        except Exception:
            self.discard()
            raise
        with self._lock:
            chunks, self._chunks = self._chunks, []
            problem, self._problem = self._problem, None
            self._sample_count = 0
        try:
            if problem:
                raise RuntimeError(problem)
            if not chunks:
                raise RuntimeError("Микрофон не передал аудио.")
            audio = np.concatenate(chunks)
            if len(audio) < int(SAMPLE_RATE * MIN_RECORDING_SECONDS):
                audio.fill(0)
                raise RuntimeError("Запись слишком короткая. Удерживайте V + F6 дольше.")
            return audio
        finally:
            for chunk in chunks:
                chunk.fill(0)

    def discard(self) -> None:
        try:
            self._close_stream(abort=True)
        finally:
            with self._lock:
                for chunk in self._chunks:
                    chunk.fill(0)
                self._chunks.clear()
                self._sample_count = 0
                self._problem = None

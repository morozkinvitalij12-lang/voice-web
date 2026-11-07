import logging
import os
from pathlib import Path
import sys

import numpy as np


logger = logging.getLogger(__name__)
_DLL_HANDLES: list[object] = []


def _enable_nvidia_libraries() -> None:
    if os.name != "nt":
        return
    # DLL из необязательных pip-пакетов NVIDIA не входят в PATH Windows.
    for entry in sys.path:
        root = Path(entry) / "nvidia"
        for package in ("cublas", "cudnn", "cuda_runtime"):
            directory = root / package / "bin"
            if directory.is_dir():
                _DLL_HANDLES.append(os.add_dll_directory(str(directory)))
                os.environ["PATH"] = str(directory) + os.pathsep + os.environ.get("PATH", "")


class SpeechRecognizer:
    def __init__(
        self,
        model_name: str = "base",
        device: str = "auto",
        cache_dir: Path | None = None,
    ) -> None:
        _enable_nvidia_libraries()
        import ctranslate2
        from faster_whisper import WhisperModel

        self._model_class = WhisperModel
        self._model_name = model_name
        self._cache_dir = cache_dir
        self._device = "cpu"
        if device in ("auto", "cuda"):
            try:
                if ctranslate2.get_cuda_device_count() > 0:
                    self._device = "cuda"
                elif device == "cuda":
                    logger.warning("CUDA недоступна; используется CPU.")
            except Exception:
                logger.warning("Не удалось проверить CUDA; используется CPU.", exc_info=True)
        logger.info("Загрузка модели... %s (%s)", model_name, self._device)
        if sys.stdout is not None:
            print("Загрузка модели...", flush=True)
        try:
            self._model = self._load_model()
        except Exception:
            if self._device != "cuda":
                raise
            logger.warning("Не удалось загрузить модель на CUDA; переход на CPU.", exc_info=True)
            self._device = "cpu"
            self._model = self._load_model()

    @property
    def device(self) -> str:
        return self._device

    def _load_model(self):
        from huggingface_hub.errors import LocalEntryNotFoundError

        options = {
            "device": self._device,
            "compute_type": "int8_float16" if self._device == "cuda" else "int8",
            "cpu_threads": max(1, min(8, os.cpu_count() or 1)),
            "download_root": str(self._cache_dir) if self._cache_dir else None,
        }
        try:
            return self._model_class(self._model_name, local_files_only=True, **options)
        except LocalEntryNotFoundError:
            logger.info("Модель ещё не скачана; требуется интернет для первой загрузки.")
            return self._model_class(self._model_name, local_files_only=False, **options)

    def _transcribe(self, audio: np.ndarray) -> str:
        segments, _ = self._model.transcribe(
            audio,
            language=None,
            beam_size=5,
            vad_filter=True,
            condition_on_previous_text=False,
        )
        # Whisper возвращает ленивый генератор; ошибки CUDA могут возникнуть при обходе.
        return " ".join(segment.text.strip() for segment in segments).strip()

    def recognize(self, audio: np.ndarray) -> str:
        try:
            return self._transcribe(audio)
        except Exception:
            if self._device != "cuda":
                raise
            logger.warning("Ошибка CUDA при распознавании; повтор на CPU.", exc_info=True)
            self._model = None
            self._device = "cpu"
            self._model = self._load_model()
            return self._transcribe(audio)

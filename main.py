import argparse
import ctypes
from ctypes import wintypes
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading

import keyboard
from PIL import Image, ImageDraw
import pystray

from audio_recorder import AudioRecorder
from recognizer import SpeechRecognizer
from site_router import open_site


APP_NAME = "VoiceSiteLauncher"
V_SCAN_CODE = 0x2F
F6_SCAN_CODE = 0x40
logger = logging.getLogger(__name__)


class PushToTalk:
    def __init__(self, on_start, on_stop) -> None:
        self._on_start = on_start
        self._on_stop = on_stop
        self._pressed: set[int] = set()
        self._active = False
        self._lock = threading.Lock()

    def handle(self, scan_code: int, event_type: str) -> None:
        if scan_code not in (V_SCAN_CODE, F6_SCAN_CODE):
            return
        with self._lock:
            if event_type == keyboard.KEY_DOWN:
                self._pressed.add(scan_code)
            elif event_type == keyboard.KEY_UP:
                self._pressed.discard(scan_code)
            else:
                return
            # Физические scan-коды работают и в русской раскладке, где V обозначена «М».
            if not self._active and len(self._pressed) == 2:
                self._active = True
                self._on_start()
            elif self._active and not self._pressed:
                self._active = False
                self._on_stop()


def microphone_icon(color: str) -> Image.Image:
    image = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((23, 7, 41, 38), radius=9, fill=color)
    draw.arc((15, 19, 49, 48), start=0, end=180, fill=color, width=5)
    draw.line((32, 47, 32, 56), fill=color, width=5)
    draw.rounded_rectangle((21, 55, 43, 59), radius=2, fill=color)
    return image


def data_directory() -> Path:
    return Path(os.environ["LOCALAPPDATA"]) / APP_NAME


def configure_logging(directory: Path) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(
        directory / "app.log", maxBytes=1_000_000, backupCount=2, encoding="utf-8"
    )
    handlers: list[logging.Handler] = [handler]
    if sys.stderr is not None:
        handlers.append(logging.StreamHandler())
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
        handlers=handlers,
    )


def ensure_administrator() -> bool:
    if ctypes.windll.shell32.IsUserAnAdmin():
        return True
    answer = ctypes.windll.user32.MessageBoxW(
        None,
        "Для работы глобальных клавиш приложение запускается от администратора.\n"
        "Перезапустить с повышенными правами? Windows покажет запрос UAC.",
        APP_NAME,
        0x24,
    )
    if answer == 6:
        arguments = (
            sys.argv[1:] if getattr(sys, "frozen", False)
            else [str(Path(__file__).resolve()), *sys.argv[1:]]
        )
        execute = ctypes.windll.shell32.ShellExecuteW
        execute.argtypes = [
            wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR,
            wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.INT,
        ]
        execute.restype = ctypes.c_ssize_t
        result = execute(
            None, "runas", sys.executable, subprocess.list2cmdline(arguments),
            str(Path(__file__).resolve().parent), 1,
        )
        if result <= 32:
            ctypes.windll.user32.MessageBoxW(
                None, "Запуск от администратора отменён или не удался.", APP_NAME, 0x10
            )
    return False


def startup_shortcut() -> Path:
    return (
        Path(os.environ["APPDATA"])
        / "Microsoft/Windows/Start Menu/Programs/Startup"
        / f"{APP_NAME}.lnk"
    )


def manage_startup(install: bool, model: str, device: str) -> None:
    shortcut = startup_shortcut()
    if not install:
        shortcut.unlink(missing_ok=True)
        print("Автозапуск отключён.")
        return
    if shortcut.exists():
        raise FileExistsError("Ярлык уже существует. Сначала отключите автозапуск.")
    if getattr(sys, "frozen", False):
        executable = Path(sys.executable)
        arguments = ["--model", model, "--device", device]
    else:
        executable = Path(sys.executable).with_name("pythonw.exe")
        arguments = [str(Path(__file__).resolve()), "--model", model, "--device", device]
    environment = os.environ.copy()
    environment.update({
        "VOICE_SHORTCUT": str(shortcut),
        "VOICE_EXECUTABLE": str(executable),
        "VOICE_ARGUMENTS": subprocess.list2cmdline(arguments),
        "VOICE_DIRECTORY": str(Path(__file__).resolve().parent),
    })
    # Пути передаются через окружение, а не вставляются в исполняемый PowerShell-код.
    script = (
        "$ErrorActionPreference = 'Stop'; "
        "$shell = New-Object -ComObject WScript.Shell; "
        "$link = $shell.CreateShortcut($env:VOICE_SHORTCUT); "
        "$link.TargetPath = $env:VOICE_EXECUTABLE; "
        "$link.Arguments = $env:VOICE_ARGUMENTS; "
        "$link.WorkingDirectory = $env:VOICE_DIRECTORY; "
        "$link.Description = 'VoiceSiteLauncher'; "
        "$link.Save()"
    )
    subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
        env=environment, check=True, creationflags=subprocess.CREATE_NO_WINDOW,
    )
    print(f"Автозапуск включён: {shortcut}")


class Application:
    def __init__(self, model: str, device: str, directory: Path) -> None:
        self._model_name = model
        self._device = device
        self._directory = directory
        self._recorder = AudioRecorder()
        self._commands: queue.Queue[str] = queue.Queue()
        self._lock = threading.Lock()
        self._ready = False
        self._session_pending = False
        self._worker_done = False
        self._closing = threading.Event()
        self._hook = None
        self._keys = PushToTalk(self._on_start, self._on_stop)
        self._icon = pystray.Icon(
            APP_NAME,
            microphone_icon("#858585"),
            "Загрузка модели...",
            menu=pystray.Menu(pystray.MenuItem("Выход", self._exit)),
        )
        self._worker = threading.Thread(target=self._work, name="speech-worker")

    def _status(self, title: str, color: str) -> None:
        try:
            self._icon.title = title
            self._icon.icon = microphone_icon(color)
        except Exception:
            logger.exception("Не удалось обновить значок трея")

    def _notify(self, message: str) -> None:
        logger.warning("%s", message)
        if self._closing.is_set():
            return
        try:
            self._icon.notify(message, APP_NAME)
        except Exception:
            threading.Thread(
                target=ctypes.windll.user32.MessageBoxW,
                args=(None, message, APP_NAME, 0x10), daemon=True,
            ).start()

    def _on_start(self) -> None:
        with self._lock:
            if self._closing.is_set() or not self._ready or self._session_pending:
                return
            self._session_pending = True
            self._commands.put("start")

    def _on_stop(self) -> None:
        with self._lock:
            if self._session_pending and not self._closing.is_set():
                self._commands.put("stop")

    def _key_event(self, event) -> None:
        if not self._closing.is_set():
            self._keys.handle(event.scan_code, event.event_type)

    def _finish_session(self) -> None:
        with self._lock:
            self._session_pending = False
        if not self._closing.is_set():
            self._status("Готово — удерживайте V + F6", "#32a852")

    def _work(self) -> None:
        try:
            try:
                recognizer = SpeechRecognizer(
                    self._model_name, self._device, self._directory / "models"
                )
                with self._lock:
                    if self._closing.is_set():
                        return
                    self._hook = keyboard.hook(self._key_event, suppress=False)
                    self._ready = True
                self._status(f"Готово — V + F6 ({recognizer.device})", "#32a852")
                self._notify("Готово. Удерживайте V + F6, произнесите фразу и отпустите обе клавиши.")
            except Exception:
                logger.exception("Ошибка инициализации")
                self._status("Ошибка запуска — см. журнал", "#d64545")
                self._notify("Не удалось загрузить модель или включить горячие клавиши. См. журнал app.log.")
                return

            while not self._closing.is_set():
                command = self._commands.get()
                if command == "exit" or self._closing.is_set():
                    break
                if command == "start":
                    try:
                        self._recorder.start()
                        self._status("Запись — отпустите обе клавиши", "#d64545")
                    except Exception as exc:
                        logger.exception("Не удалось начать запись")
                        self._recorder.discard()
                        self._notify(str(exc))
                        self._finish_session()
                elif command == "stop" and self._recorder.is_recording:
                    try:
                        self._status("Распознавание...", "#e0a12b")
                        audio = self._recorder.stop()
                        try:
                            text = recognizer.recognize(audio)
                        finally:
                            # Запись не попадает на диск; исходный буфер очищается сразу после STT.
                            audio.fill(0)
                            del audio
                        with self._lock:
                            if self._closing.is_set():
                                break
                            if text.strip():
                                url = open_site(text)
                                logger.info("Текст: %s | Открыто: %s", text, url)
                        if not text.strip():
                            self._notify("Речь не обнаружена. Повторите фразу ближе к микрофону.")
                    except Exception as exc:
                        logger.exception("Ошибка обработки речи")
                        self._notify(f"Не удалось обработать речь: {exc}")
                    finally:
                        self._finish_session()
        finally:
            try:
                self._recorder.discard()
            finally:
                with self._lock:
                    hook, self._hook = self._hook, None
                    self._ready = False
                try:
                    if hook is not None:
                        keyboard.unhook(hook)
                finally:
                    with self._lock:
                        self._worker_done = True
                        should_stop = self._closing.is_set()
                    if should_stop:
                        self._icon.stop()

    def _exit(self, icon=None, item=None) -> None:
        with self._lock:
            self._closing.set()
            self._ready = False
            should_stop = self._worker_done
        self._status("Завершение...", "#858585")
        self._commands.put("exit")
        if should_stop:
            self._icon.stop()

    def run(self) -> None:
        def setup(icon):
            icon.visible = True
            self._worker.start()

        try:
            self._icon.run(setup=setup)
        finally:
            with self._lock:
                self._closing.set()
                self._ready = False
            self._commands.put("exit")
            if self._worker.ident is not None:
                self._worker.join()
            logger.info("Приложение завершено.")


def main() -> int:
    parser = argparse.ArgumentParser(description="Офлайн-голосовое открытие сайтов: V + F6")
    parser.add_argument("--model", choices=("base", "small"), default="base")
    parser.add_argument("--device", choices=("auto", "cpu", "cuda"), default="auto")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--install-startup", action="store_true")
    group.add_argument("--remove-startup", action="store_true")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("Приложение предназначено только для Windows.")
    if args.install_startup or args.remove_startup:
        try:
            manage_startup(args.install_startup, args.model, args.device)
            return 0
        except Exception as exc:
            print(f"Ошибка настройки автозапуска: {exc}", file=sys.stderr)
            return 1
    if not ensure_administrator():
        return 0

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
    kernel.CreateMutexW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    mutex = kernel.CreateMutexW(None, False, "Local\\VoiceSiteLauncher")
    if not mutex:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        if ctypes.get_last_error() == 183:
            ctypes.windll.user32.MessageBoxW(None, "Приложение уже работает в трее.", APP_NAME, 0x40)
            return 0
        directory = data_directory()
        configure_logging(directory)
        Application(args.model, args.device, directory).run()
        return 0
    except Exception:
        logger.exception("Не удалось запустить приложение")
        ctypes.windll.user32.MessageBoxW(None, "Ошибка запуска. Проверьте журнал app.log.", APP_NAME, 0x10)
        return 1
    finally:
        kernel.CloseHandle(mutex)


if __name__ == "__main__":
    raise SystemExit(main())

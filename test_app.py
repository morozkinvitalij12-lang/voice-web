import queue
import threading
import unittest
from unittest.mock import MagicMock, patch
from urllib.parse import parse_qs, urlparse

import numpy as np

import audio_recorder
from audio_recorder import AudioRecorder, SAMPLE_RATE
import main
from main import F6_SCAN_CODE, PushToTalk, V_SCAN_CODE
from recognizer import SpeechRecognizer
from site_router import SITE_KEYWORDS, normalize_text, open_site, resolve_url


class RouterTests(unittest.TestCase):
    def test_all_aliases(self):
        for url, keywords in SITE_KEYWORDS.items():
            for keyword in keywords:
                with self.subTest(keyword=keyword):
                    self.assertEqual(resolve_url(f"Открой {keyword.upper()}!"), url)

    def test_normalization(self):
        self.assertEqual(normalize_text("  Open\n YOUTUBE\t "), "open youtube")

    def test_no_partial_matches(self):
        for text in ("включи музыку", "невконтакте", "vkservice", "mygithubaccount"):
            with self.subTest(text=text):
                self.assertTrue(resolve_url(text).startswith("https://www.google.com/search?"))

    def test_url_encoding(self):
        text = "Погода & новости + Python #1"
        parsed = urlparse(resolve_url(text))
        self.assertEqual(parse_qs(parsed.query)["q"], [normalize_text(text)])

    def test_first_site_wins(self):
        self.assertEqual(resolve_url("youtube или google"), "https://www.youtube.com")
        self.assertEqual(resolve_url("google или youtube"), "https://www.google.com")

    def test_empty_text(self):
        with self.assertRaises(ValueError):
            resolve_url(" \n\t")

    @patch("site_router.webbrowser.open", return_value=True)
    def test_browser(self, browser):
        self.assertEqual(open_site("youtube"), "https://www.youtube.com")
        browser.assert_called_once_with("https://www.youtube.com", new=2)

    @patch("site_router.webbrowser.open", return_value=False)
    def test_browser_error(self, browser):
        with self.assertRaises(RuntimeError):
            open_site("youtube")


class HotkeyTests(unittest.TestCase):
    def setUp(self):
        self.events = []
        self.keys = PushToTalk(
            lambda: self.events.append("start"), lambda: self.events.append("stop")
        )

    def down(self, code):
        self.keys.handle(code, "down")

    def up(self, code):
        self.keys.handle(code, "up")

    def test_one_key_does_not_start(self):
        self.down(V_SCAN_CODE)
        self.up(V_SCAN_CODE)
        self.down(F6_SCAN_CODE)
        self.up(F6_SCAN_CODE)
        self.assertEqual(self.events, [])

    def test_both_release_orders(self):
        for first, second in ((V_SCAN_CODE, F6_SCAN_CODE), (F6_SCAN_CODE, V_SCAN_CODE)):
            with self.subTest(first=first):
                self.events.clear()
                self.down(first)
                self.down(second)
                self.assertEqual(self.events, ["start"])
                self.up(first)
                self.assertEqual(self.events, ["start"])
                self.up(second)
                self.assertEqual(self.events, ["start", "stop"])

    def test_repeat_does_not_duplicate(self):
        self.down(V_SCAN_CODE)
        self.down(F6_SCAN_CODE)
        for _ in range(20):
            self.down(V_SCAN_CODE)
            self.down(F6_SCAN_CODE)
        self.up(V_SCAN_CODE)
        self.down(V_SCAN_CODE)
        self.up(F6_SCAN_CODE)
        self.up(V_SCAN_CODE)
        self.up(V_SCAN_CODE)
        self.assertEqual(self.events, ["start", "stop"])

    def test_unrelated_key(self):
        self.down(30)
        self.down(V_SCAN_CODE)
        self.down(F6_SCAN_CODE)
        self.up(V_SCAN_CODE)
        self.up(F6_SCAN_CODE)
        self.assertEqual(self.events, ["start", "stop"])

    def test_multiple_sessions(self):
        for _ in range(3):
            self.down(V_SCAN_CODE)
            self.down(F6_SCAN_CODE)
            self.up(V_SCAN_CODE)
            self.up(F6_SCAN_CODE)
        self.assertEqual(self.events, ["start", "stop"] * 3)


class RecorderTests(unittest.TestCase):
    def setUp(self):
        self.recorder = AudioRecorder()

    def feed(self, samples=SAMPLE_RATE // 2, status=None):
        data = np.ones((samples, 1), dtype=np.float32)
        self.recorder._callback(data, samples, None, status)
        return data

    @patch("audio_recorder.sd.InputStream")
    @patch("audio_recorder.sd.check_input_settings")
    @patch("audio_recorder.sd.query_devices", return_value={"max_input_channels": 1})
    def test_memory_capture(self, devices, settings, stream_factory):
        self.recorder.start()
        stream_factory.return_value.start.assert_called_once()
        source = self.feed()
        chunk = self.recorder._chunks[0]
        audio = self.recorder.stop()
        np.testing.assert_array_equal(audio, source[:, 0])
        self.assertTrue(np.all(chunk == 0))
        self.assertFalse(self.recorder.is_recording)
        self.assertEqual(self.recorder._chunks, [])
        stream_factory.return_value.stop.assert_called_once()
        stream_factory.return_value.close.assert_called_once()

    @patch("audio_recorder.sd.query_devices", side_effect=RuntimeError("no device"))
    def test_missing_microphone(self, devices):
        with self.assertRaisesRegex(RuntimeError, "микрофон"):
            self.recorder.start()
        self.assertFalse(self.recorder.is_recording)

    def test_short_recording(self):
        self.recorder._stream = MagicMock()
        self.feed(samples=100)
        with self.assertRaisesRegex(RuntimeError, "короткая"):
            self.recorder.stop()
        self.assertEqual(self.recorder._chunks, [])

    def test_capture_problem(self):
        self.recorder._stream = MagicMock()
        self.feed(status="overflow")
        with self.assertRaisesRegex(RuntimeError, "overflow"):
            self.recorder.stop()

    def test_limit(self):
        self.recorder._stream = MagicMock()
        self.recorder._limit = 100
        self.feed(samples=150)
        self.assertEqual(self.recorder._sample_count, 100)
        with self.assertRaises(audio_recorder.sd.CallbackStop):
            self.feed(samples=50)
        with self.assertRaisesRegex(RuntimeError, "превышает"):
            self.recorder.stop()

    def test_discard_clears_buffers(self):
        self.recorder._stream = MagicMock()
        self.feed()
        chunk = self.recorder._chunks[0]
        self.recorder.discard()
        self.assertTrue(np.all(chunk == 0))
        self.assertFalse(self.recorder.is_recording)
        self.assertEqual(self.recorder._chunks, [])

    def test_stream_cleanup_on_failure(self):
        stream = self.recorder._stream = MagicMock()
        stream.stop.side_effect = RuntimeError("lost device")
        self.feed()
        chunk = self.recorder._chunks[0]
        with self.assertRaises(RuntimeError):
            self.recorder.stop()
        stream.close.assert_called_once()
        self.assertTrue(np.all(chunk == 0))
        self.assertFalse(self.recorder.is_recording)


class RecognizerTests(unittest.TestCase):
    def bare_recognizer(self, device="cuda"):
        recognizer = SpeechRecognizer.__new__(SpeechRecognizer)
        recognizer._device = device
        recognizer._model = MagicMock()
        return recognizer

    def test_cpu_retry_on_lazy_cuda_failure(self):
        recognizer = self.bare_recognizer()

        def broken_segments():
            raise RuntimeError("cudnn DLL not found")
            yield

        recognizer._model.transcribe.return_value = (broken_segments(), None)
        cpu_model = MagicMock()
        cpu_model.transcribe.return_value = ([MagicMock(text=" hello ")], None)
        with patch.object(recognizer, "_load_model", return_value=cpu_model):
            self.assertEqual(recognizer.recognize(np.zeros(16000, np.float32)), "hello")
        self.assertEqual(recognizer.device, "cpu")
        self.assertIsNone(cpu_model.transcribe.call_args.kwargs["language"])
        self.assertTrue(cpu_model.transcribe.call_args.kwargs["vad_filter"])

    def test_cpu_error_is_not_hidden(self):
        recognizer = self.bare_recognizer("cpu")
        recognizer._model.transcribe.side_effect = ValueError("bad audio")
        with self.assertRaises(ValueError):
            recognizer.recognize(np.zeros(16000, np.float32))

    def test_load_uses_cache_before_network(self):
        from huggingface_hub.errors import LocalEntryNotFoundError

        recognizer = self.bare_recognizer("cpu")
        recognizer._model_name = "base"
        recognizer._cache_dir = None
        recognizer._model_class = MagicMock()
        recognizer._model_class.side_effect = [LocalEntryNotFoundError("missing"), "model"]
        self.assertEqual(recognizer._load_model(), "model")
        calls = recognizer._model_class.call_args_list
        self.assertTrue(calls[0].kwargs["local_files_only"])
        self.assertFalse(calls[1].kwargs["local_files_only"])
        self.assertEqual(calls[0].kwargs["compute_type"], "int8")


class ApplicationTests(unittest.TestCase):
    def bare_application(self):
        app = main.Application.__new__(main.Application)
        app._lock = threading.Lock()
        app._closing = threading.Event()
        app._commands = queue.Queue()
        app._ready = True
        app._session_pending = False
        app._worker_done = False
        app._hook = None
        app._icon = MagicMock()
        app._worker = MagicMock()
        app._status = MagicMock()
        return app

    def test_busy_session_is_not_queued(self):
        app = self.bare_application()
        app._on_start()
        app._on_start()
        self.assertEqual(app._commands.get_nowait(), "start")
        self.assertTrue(app._commands.empty())
        app._on_stop()
        self.assertEqual(app._commands.get_nowait(), "stop")
        app._finish_session()
        app._on_start()
        self.assertEqual(app._commands.get_nowait(), "start")

    def test_no_capture_before_ready_or_after_exit(self):
        app = self.bare_application()
        app._ready = False
        app._on_start()
        self.assertTrue(app._commands.empty())
        app._ready = True
        app._closing.set()
        app._on_start()
        self.assertTrue(app._commands.empty())

    @patch("main.keyboard.unhook")
    @patch("main.keyboard.hook", return_value=MagicMock())
    @patch("main.SpeechRecognizer")
    @patch("main.open_site")
    def test_audio_cleared_before_opening_browser(self, browser, recognizer_class, hook, unhook):
        app = self.bare_application()
        app._model_name = "base"
        app._device = "cpu"
        app._directory = main.Path(".")
        app._notify = MagicMock()
        app._key_event = MagicMock()
        audio = np.ones(SAMPLE_RATE, np.float32)
        app._recorder = MagicMock()
        app._recorder.is_recording = True
        app._recorder.stop.return_value = audio
        recognizer_class.return_value.recognize.return_value = "ютуб"
        app._commands.put("stop")
        app._commands.put("exit")

        def check_audio(text):
            self.assertTrue(np.all(audio == 0))
            return "https://www.youtube.com"

        browser.side_effect = check_audio
        app._work()
        browser.assert_called_once_with("ютуб")
        app._recorder.discard.assert_called_once()
        unhook.assert_called_once_with(hook.return_value)
        self.assertIsNone(app._hook)

    def test_icon_error_is_isolated(self):
        class BrokenIcon:
            @property
            def title(self):
                return ""

            @title.setter
            def title(self, value):
                raise RuntimeError("tray unavailable")

        app = self.bare_application()
        app._icon = BrokenIcon()
        with patch("main.logger.exception") as log:
            main.Application._status(app, "Запись", "red")
        log.assert_called_once()

    @patch("main.keyboard.unhook")
    @patch("main.keyboard.hook", return_value=MagicMock())
    @patch("main.SpeechRecognizer")
    def test_start_failure_discards_microphone(self, recognizer_class, hook, unhook):
        app = self.bare_application()
        app._model_name = "base"
        app._device = "cpu"
        app._directory = main.Path(".")
        app._notify = MagicMock()
        app._key_event = MagicMock()
        app._recorder = MagicMock()
        app._recorder.is_recording = False
        app._recorder.discard.side_effect = lambda: setattr(app._recorder, "is_recording", False)

        def start():
            app._recorder.is_recording = True
            raise RuntimeError("partial stream startup failure")

        app._recorder.start.side_effect = start
        app._commands.put("start")
        app._commands.put("stop")
        app._commands.put("exit")
        with patch("main.logger.exception"):
            app._work()
        self.assertFalse(app._recorder.is_recording)
        self.assertEqual(app._recorder.discard.call_count, 2)
        recognizer_class.return_value.recognize.assert_not_called()
        unhook.assert_called_once()

    @patch("main.keyboard.hook")
    @patch("main.SpeechRecognizer")
    def test_no_late_hook_when_exit_during_model_load(self, recognizer_class, hook):
        app = self.bare_application()
        app._model_name = "base"
        app._device = "cpu"
        app._directory = main.Path(".")
        app._notify = MagicMock()
        app._key_event = MagicMock()
        app._recorder = MagicMock()
        entered, release = threading.Event(), threading.Event()

        def load(*args):
            entered.set()
            if not release.wait(5):
                raise RuntimeError("test timed out")
            return MagicMock(device="cpu")

        recognizer_class.side_effect = load
        worker = app._worker = threading.Thread(target=app._work)
        worker.start()
        try:
            self.assertTrue(entered.wait(5))
            app._exit()
            app._icon.stop.assert_not_called()
        finally:
            release.set()
            worker.join(5)
        self.assertFalse(worker.is_alive())
        hook.assert_not_called()
        app._recorder.discard.assert_called_once()
        app._icon.stop.assert_called_once()

    @patch("main.keyboard.unhook")
    @patch("main.keyboard.hook", return_value=MagicMock())
    @patch("main.SpeechRecognizer")
    @patch("main.open_site")
    def test_exit_during_recognition_does_not_open_browser(self, browser, recognizer_class, hook, unhook):
        app = self.bare_application()
        app._model_name = "base"
        app._device = "cpu"
        app._directory = main.Path(".")
        app._notify = MagicMock()
        app._key_event = MagicMock()
        app._recorder = MagicMock()
        app._recorder.is_recording = True
        audio = np.ones(SAMPLE_RATE, dtype=np.float32)
        app._recorder.stop.return_value = audio
        app._commands.put("stop")

        def recognize(buffer):
            app._closing.set()
            return "ютуб"

        recognizer_class.return_value.recognize.side_effect = recognize
        app._work()
        browser.assert_not_called()
        self.assertTrue(np.all(audio == 0))
        unhook.assert_called_once()
        app._icon.stop.assert_called_once()

    @patch("main.SpeechRecognizer", side_effect=RuntimeError("model missing"))
    def test_exit_after_failed_worker_cleanup_stops_tray(self, recognizer_class):
        app = self.bare_application()
        app._model_name = "base"
        app._device = "cpu"
        app._directory = main.Path(".")
        app._notify = MagicMock()
        app._recorder = MagicMock()
        with patch("main.logger.exception"):
            app._work()
        self.assertTrue(app._worker_done)
        app._icon.stop.assert_not_called()
        app._worker.is_alive.return_value = True
        app._exit()
        app._icon.stop.assert_called_once()


class ElevationTests(unittest.TestCase):
    def test_absolute_script_path_and_pointer_sized_result(self):
        shell, user = MagicMock(), MagicMock()
        shell.IsUserAnAdmin.return_value = False
        shell.ShellExecuteW.return_value = 1 << 33
        user.MessageBoxW.return_value = 6
        with patch.object(main.ctypes.windll, "shell32", shell), patch.object(
            main.ctypes.windll, "user32", user
        ), patch.object(main.sys, "argv", ["relative/main.py", "--model", "small"]):
            self.assertFalse(main.ensure_administrator())
        args = shell.ShellExecuteW.call_args.args
        self.assertIn(str(main.Path(main.__file__).resolve()), args[3])
        self.assertIn("--model small", args[3])
        self.assertIs(shell.ShellExecuteW.restype, main.ctypes.c_ssize_t)
        user.MessageBoxW.assert_called_once()


if __name__ == "__main__":
    unittest.main()

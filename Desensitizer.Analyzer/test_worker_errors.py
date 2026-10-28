"""Unit tests for the error scrubbing in the model workers. They need no model pack.

gliner, numpy, onnxruntime and tokenizers are replaced by empty stand-ins, so only the pure Python
helpers run. Run from this folder:

    python -m unittest test_worker_errors -v
"""
import importlib
import io
import json
import sys
import types
import unittest
from unittest import mock


def _install_stubs():
    gliner = types.ModuleType("gliner")
    gliner.GLiNER = object
    numpy = types.ModuleType("numpy")
    onnx = types.ModuleType("onnxruntime")
    tokenizers = types.ModuleType("tokenizers")
    tokenizers.Tokenizer = object
    for module in (gliner, numpy, onnx, tokenizers):
        sys.modules.setdefault(module.__name__, module)


_install_stubs()
WORKERS = [importlib.import_module("business_context_analyzer"), importlib.import_module("privacy_filter_analyzer")]
SECRET = "Maya Lin account 4111-2222-3333 secret"


class ScrubErrorTests(unittest.TestCase):
    def test_foreign_exception_never_echoes_its_message(self):
        for worker in WORKERS:
            text = worker.scrub_error(ValueError("could not parse " + SECRET))
            self.assertNotIn("Maya", text)
            self.assertNotIn("4111", text)
            self.assertIn("ValueError", text)

    def test_safe_error_is_truncated_and_cleaned(self):
        for worker in WORKERS:
            text = worker.scrub_error(worker.SafeError("line one\nline two\x00" + "x" * 500))
            self.assertLessEqual(len(text), worker.MAX_ERROR_CHARS)
            self.assertNotIn("\n", text)
            self.assertNotIn("\x00", text)

    def test_main_reports_scrubbed_error_for_bad_input(self):
        for worker in WORKERS:
            bad = json.dumps({"texts": ["fine", 5, SECRET]})
            out = io.StringIO()
            with mock.patch.object(sys, "stdin", io.StringIO(bad)), mock.patch.object(sys, "stdout", out):
                code = worker.main()
            self.assertEqual(code, 1)
            self.assertNotIn("Maya", out.getvalue())
            self.assertIn("error", json.loads(out.getvalue()))

    def test_main_scrubs_an_exception_that_quotes_the_input(self):
        for worker in WORKERS:
            request = json.dumps({"text": SECRET})
            out = io.StringIO()
            with mock.patch.object(sys, "stdin", io.StringIO(request)), mock.patch.object(sys, "stdout", out), \
                    mock.patch.object(worker, "analyze", side_effect=RuntimeError("bad token near " + SECRET)):
                code = worker.main()
            self.assertEqual(code, 1)
            self.assertNotIn("Maya", out.getvalue())
            self.assertNotIn("4111", out.getvalue())

    def test_model_override_ignored_without_dev_flag(self):
        for worker, variable in zip(WORKERS, ["DESENSITIZER_GLINER_MODEL", "DESENSITIZER_PRIVACY_MODEL"]):
            with mock.patch.dict("os.environ", {variable: "C:\\elsewhere", "DESENSITIZER_DEV": ""}):
                self.assertNotEqual(worker.model_directory(), "C:\\elsewhere")
            with mock.patch.dict("os.environ", {variable: "C:\\elsewhere", "DESENSITIZER_DEV": "1"}):
                self.assertEqual(worker.model_directory(), "C:\\elsewhere")


if __name__ == "__main__":
    unittest.main()

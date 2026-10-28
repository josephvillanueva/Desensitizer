"""Worker stream contract tests with model stubs, not model accuracy tests."""
import io
import json
import sys
import unittest
from unittest import mock

# Reuse the dependency stubs already used by the no-download analyzer tests.
import test_analyzer
import test_worker_errors

WORKERS = [test_analyzer.analyzer] + test_worker_errors.WORKERS
UNICODE_TEXT = "Léa Peña, 株式会社, Δοκιμή, 😀"


class WorkerEncodingTests(unittest.TestCase):
    def run_request(self, worker, payload, failure=False):
        input_bytes = io.BytesIO(payload)
        output_bytes = io.BytesIO()
        error_bytes = io.BytesIO()
        stdin = io.TextIOWrapper(input_bytes, encoding="cp1252")
        stdout = io.TextIOWrapper(output_bytes, encoding="cp1252")
        stderr = io.TextIOWrapper(error_bytes, encoding="cp1252")
        def echo(*args):
            text = args[-1] if worker is test_analyzer.analyzer else args[0]
            if failure:
                raise RuntimeError("secret input: " + text)
            return [{"value": text, "category": "PERSON"}]
        model_name = "create_analyzer" if worker is test_analyzer.analyzer else "load_model"
        with mock.patch.object(sys, "stdin", stdin), mock.patch.object(sys, "stdout", stdout), mock.patch.object(sys, "stderr", stderr), \
                mock.patch.object(worker, model_name, return_value=object()), mock.patch.object(worker, "analyze", side_effect=echo):
            code = worker.main()
            self.assertEqual(stdin.encoding, "utf-8")
            self.assertEqual(stdout.encoding, "utf-8")
            self.assertEqual(stderr.encoding, "utf-8")
            stdout.flush()
            response = json.loads(output_bytes.getvalue().decode("utf-8"))
        return code, response

    def test_literal_unicode_single(self):
        for worker in WORKERS:
            with self.subTest(worker=worker.__name__):
                code, response = self.run_request(worker, json.dumps({"text": UNICODE_TEXT}, ensure_ascii=False).encode("utf-8"))
                self.assertEqual(code, 0)
                self.assertEqual(response["candidates"][0]["value"], UNICODE_TEXT)

    def test_escaped_unicode_remains_compatible(self):
        for worker in WORKERS:
            with self.subTest(worker=worker.__name__):
                code, response = self.run_request(worker, json.dumps({"text": UNICODE_TEXT}).encode("ascii"))
                self.assertEqual(code, 0)
                self.assertEqual(response["candidates"][0]["value"], UNICODE_TEXT)

    def test_literal_unicode_batch(self):
        for worker in WORKERS:
            with self.subTest(worker=worker.__name__):
                code, response = self.run_request(worker, json.dumps({"texts": [UNICODE_TEXT, "ordinary text"]}, ensure_ascii=False).encode("utf-8"))
                self.assertEqual(code, 0)
                self.assertEqual([batch[0]["value"] for batch in response["batches"]], [UNICODE_TEXT, "ordinary text"])

    def test_invalid_utf8_fails_without_payload(self):
        for worker in WORKERS:
            with self.subTest(worker=worker.__name__):
                code, response = self.run_request(worker, b'{"text":"secret \xff payload"}')
                self.assertEqual(code, 1)
                self.assertIn("UnicodeDecodeError", response["error"])
                self.assertNotIn("secret", response["error"])

    def test_unicode_exception_input_is_scrubbed(self):
        for worker in WORKERS:
            with self.subTest(worker=worker.__name__):
                code, response = self.run_request(worker, json.dumps({"text": UNICODE_TEXT}, ensure_ascii=False).encode("utf-8"), failure=True)
                self.assertEqual(code, 1)
                self.assertNotIn("Peña", response["error"])
                self.assertNotIn("secret", response["error"])

    def test_stringio_test_streams_supported(self):
        for worker in WORKERS:
            with self.subTest(worker=worker.__name__), mock.patch.object(sys, "stdin", io.StringIO()), mock.patch.object(sys, "stdout", io.StringIO()), mock.patch.object(sys, "stderr", io.StringIO()):
                worker.configure_utf8_stdio()


if __name__ == "__main__":
    unittest.main()

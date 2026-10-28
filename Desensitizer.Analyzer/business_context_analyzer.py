import json
import os
import sys

from gliner import GLiNER


LABELS = {
    "company": "ORGANIZATION",
    "client organization": "ORGANIZATION",
    "vendor": "VENDOR",
    "project codename": "PROJECT",
    "internal project": "PROJECT",
    "internal system": "SYSTEM",
    "business application": "SYSTEM",
    "product name": "PRODUCT",
    "department": "DEPARTMENT",
}


MAX_ERROR_CHARS = 160


class SafeError(Exception):
    """An error whose message was written by this program and never contains input text."""


def scrub_error(exc):
    """Text that may be returned to the app: it never echoes input fragments.

    Only SafeError messages (fixed wording written here) are passed on, truncated and
    stripped of control characters. Any other exception, including ones raised by the
    model libraries which can quote the text they choked on, is reduced to its type name.
    """
    if isinstance(exc, SafeError):
        message = "".join(" " if ord(ch) < 32 or ord(ch) == 127 else ch for ch in str(exc))
        return message.strip()[:MAX_ERROR_CHARS]
    return "The analyzer failed (" + type(exc).__name__ + ")."


def model_directory():
    # A model path override is honoured only in development (DESENSITIZER_DEV=1).
    configured = os.environ.get("DESENSITIZER_GLINER_MODEL") if os.environ.get("DESENSITIZER_DEV") == "1" else None
    if configured:
        return configured
    bundled = os.path.join(os.path.dirname(sys.executable), "model")
    if os.path.isdir(bundled):
        return bundled
    source_model = os.path.join(os.path.dirname(__file__), "models", "gliner")
    return source_model if os.path.isdir(source_model) else "urchade/gliner_small-v2.1"


def load_model():
    directory = model_directory()
    previous_directory = os.getcwd()
    try:
        # The packaged GLiNER configuration points its encoder at ".", where
        # its tokenizer/configuration assets are bundled alongside the weights.
        os.chdir(directory)
        model = GLiNER.from_pretrained(directory, local_files_only=True)
    finally:
        os.chdir(previous_directory)
    return model


def analyze(text, model=None):
    if model is None:
        model = load_model()
    found = model.predict_entities(text, list(LABELS), threshold=0.42)
    best = {}
    for entity in found:
        value = entity["text"].strip()
        label = entity["label"].lower()
        score = float(entity["score"])
        if len(value) < 2 or label not in LABELS:
            continue
        key = value.casefold()
        item = {
            "value": value,
            "category": LABELS[label],
            "confidence": round(score, 3),
            "source": "GLiNER",
            "explanation": f"Business-context match: {label}",
        }
        if key not in best or score > best[key]["confidence"]:
            best[key] = item
    return list(best.values())


def configure_utf8_stdio():
    """Use an explicit BOM-free worker protocol, independent of the Windows code page."""
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8", errors="strict")


def main():
    try:
        configure_utf8_stdio()
        request = json.load(sys.stdin)
        if "texts" in request:
            texts = request["texts"]
            if not isinstance(texts, list) or not all(isinstance(text, str) for text in texts):
                raise SafeError("texts must be a list of strings")
            model = load_model()
            print(json.dumps({"batches": [analyze(text, model) for text in texts]}))
            return 0
        text = request.get("text", "")
        if not isinstance(text, str):
            raise SafeError("text must be a string")
        print(json.dumps({"candidates": analyze(text)}))
    except Exception as exc:
        print(json.dumps({"error": scrub_error(exc)}))
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

import json
import os
import sys

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer


CATEGORY_MAP = {
    "private_person": "PERSON",
    "private_address": "ADDRESS",
    "private_email": "EMAIL",
    "private_phone": "PHONE",
    "private_url": "URL",
    "private_date": "DATE",
    "account_number": "IDENTIFIER",
    "secret": "SECRET",
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
    configured = os.environ.get("DESENSITIZER_PRIVACY_MODEL") if os.environ.get("DESENSITIZER_DEV") == "1" else None
    if configured:
        return configured
    bundled = os.path.join(os.path.dirname(sys.executable), "model")
    if os.path.isdir(bundled):
        return bundled
    source_model = os.path.join(os.path.dirname(__file__), "models", "privacy-filter")
    return source_model if os.path.isdir(source_model) else "openai/privacy-filter"


def load_model():
    directory = model_directory()
    tokenizer = Tokenizer.from_file(os.path.join(directory, "tokenizer.json"))
    tokenizer.enable_truncation(max_length=4096, stride=96)
    session = ort.InferenceSession(
        os.path.join(directory, "onnx", "model_q4.onnx"),
        providers=["CPUExecutionProvider"],
    )
    with open(os.path.join(directory, "config.json"), encoding="utf-8") as handle:
        id_to_label = json.load(handle)["id2label"]
    return tokenizer, session, id_to_label


def analyze(text, model=None):
    tokenizer, session, id_to_label = model if model is not None else load_model()
    first_encoding = tokenizer.encode(text)
    encoded = [first_encoding] + list(first_encoding.overflowing)
    best = {}
    for encoding in encoded:
        input_ids = np.asarray([encoding.ids], dtype=np.int64)
        attention = np.ones_like(input_ids, dtype=np.int64)
        logits = session.run(None, {"input_ids": input_ids, "attention_mask": attention})[0][0]
        shifted = logits - logits.max(axis=-1, keepdims=True)
        probabilities = np.exp(shifted) / np.exp(shifted).sum(axis=-1, keepdims=True)
        predicted = probabilities.argmax(axis=-1)
        offsets = encoding.offsets
        spans = decode_spans(text, offsets, predicted, probabilities, id_to_label)
        for span in spans:
            key = (span["value"].casefold(), span["category"])
            if key not in best or span["confidence"] > best[key]["confidence"]:
                best[key] = span
    return list(best.values())


def decode_spans(text, offsets, predicted, probabilities, id_to_label):
    results = []
    current = None
    for token_index, class_id in enumerate(predicted):
        start, end = offsets[token_index]
        tag = id_to_label[str(int(class_id))]
        if start == end or tag == "O":
            if current:
                results.append(finish_span(text, current))
                current = None
            continue
        boundary, label = tag.split("-", 1)
        score = float(probabilities[token_index, class_id])
        if boundary in ("B", "S") or not current or current["label"] != label:
            if current:
                results.append(finish_span(text, current))
            current = {"label": label, "start": start, "end": end, "scores": [score]}
        else:
            current["end"] = end
            current["scores"].append(score)
        if boundary in ("E", "S") and current:
            results.append(finish_span(text, current))
            current = None
    if current:
        results.append(finish_span(text, current))
    return [item for item in results if item]


def finish_span(text, span):
    label = span["label"]
    value = text[span["start"] : span["end"]].strip()
    if label not in CATEGORY_MAP or len(value) < 2:
        return None
    return {
        "value": value,
        "category": CATEGORY_MAP[label],
        "confidence": round(sum(span["scores"]) / len(span["scores"]), 3),
        "source": "Privacy Filter",
        "explanation": f"Context-aware privacy match: {label}",
    }


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

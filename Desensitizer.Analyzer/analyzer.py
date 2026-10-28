import json
import re
import sys
import unicodedata

import tldextract

# Force the bundled public-suffix snapshot. Never refresh from the internet.
tldextract.extract = tldextract.TLDExtract(suffix_list_urls=())

from presidio_analyzer import AnalyzerEngine, Pattern, PatternRecognizer
from presidio_analyzer.nlp_engine import NlpEngineProvider
from presidio_analyzer.predefined_recognizers import PhoneRecognizer


def create_tin_recognizer():
    """Philippine Tax Identification Number (the PH_TIN entity mapped in CATEGORY_MAP).

    The dashed 3-3-3 form (optionally with a 3 to 5 digit branch code) is format-anchored. A bare 9 or
    12 digit run scores below the 0.45 threshold on its own and only reaches it when a TIN label is
    within Presidio's context window, so a plain number is never a TIN without evidence.
    """
    patterns = [
        Pattern("PH TIN (dashed)", r"(?<![\w-])\d{3}-\d{3}-\d{3}(?:-\d{3,5})?(?![\w-])", 0.6),
        Pattern("PH TIN (digits, needs label)", r"(?<![\w-])\d{9}(?:\d{3})?(?![\w-])", 0.3),
    ]
    return PatternRecognizer(
        supported_entity="PH_TIN",
        name="PhilippineTinRecognizer",
        patterns=patterns,
        context=["tin", "tax identification", "tax id", "taxpayer", "bir"],
        supported_language="en",
    )


def create_analyzer():
    configuration = {
        "nlp_engine_name": "spacy",
        "models": [{"lang_code": "en", "model_name": "en_core_web_sm"}],
    }
    engine = NlpEngineProvider(nlp_configuration=configuration).create_engine()
    analyzer = AnalyzerEngine(nlp_engine=engine, supported_languages=["en"])
    analyzer.registry.add_recognizer(
        PhoneRecognizer(supported_regions=["PH"], supported_entity="PHONE_NUMBER")
    )
    analyzer.registry.add_recognizer(create_tin_recognizer())
    return analyzer


CATEGORY_MAP = {
    "EMAIL_ADDRESS": "EMAIL",
    "PHONE_NUMBER": "PHONE",
    "PERSON": "PERSON",
    "LOCATION": "ADDRESS",
    "PH_TIN": "IDENTIFIER",
    "IP_ADDRESS": "IDENTIFIER",
    "MEDICAL_LICENSE": "IDENTIFIER",
}

NON_PERSON_WORDS = {
    "analyst", "application", "business", "cloud", "computing", "developer",
    "engineer", "frontend", "information", "lead", "management", "services",
    "software", "specialist", "system", "technology", "university",
}

TECH_LOCATIONS = {"angular.js", "express.js", "graphql", "next.js", "node.js", "react.js"}


# Lowercase particles that are part of a Philippine, Spanish, Dutch or German surname. They are valid
# inside a name but never at its start or end.
NAME_PARTICLES = {"de", "del", "dela", "delos", "la", "las", "los", "van", "von", "der", "di", "da"}
NAME_SUFFIXES = {"jr", "sr", "ii", "iii", "iv", "v"}
MAX_NAME_WORDS = 6


def _fold(word):
    decomposed = unicodedata.normalize("NFKD", word)
    return "".join(ch for ch in decomposed if not unicodedata.combining(ch))


def _is_name_word(word):
    """A capitalized name part: Maria, PENA, Jose, T., O'Brien, Anne-Marie. Diacritics are allowed."""
    if not word or not word[0].isupper():
        return False
    return all(ch.isalpha() or ch in ".'’-" for ch in word)


def _valid_person_words(words, surname_first=False):
    if not 2 <= len(words) <= MAX_NAME_WORDS:
        return False
    # "delos Santos, Corazon K." starts with a particle, because the surname is written first.
    if (words[0] in NAME_PARTICLES and not surname_first) or words[-1] in NAME_PARTICLES:
        return False
    for word in words:
        if word in NAME_PARTICLES:
            continue
        if not _is_name_word(word):
            return False
    return True


def clean_person(value):
    words = value.split()
    commas = value.count(",")
    surname_first = commas > 0
    if commas == 0:
        pass
    elif commas == 1 or (commas == 2 and words and re.sub(r"[^a-z]", "", words[-1].lower()) in NAME_SUFFIXES):
        # "Santos, Maria T." and "Santos, Maria T., Jr." are one person. A bare two word "A, B" is
        # more likely a list of two people, so the comma form needs three or more words.
        if len(words) < 3 or any(not word.strip(",") for word in words):
            return None
        words = [word.rstrip(",") for word in words]
    else:
        return None
    normalized = {re.sub(r"[^a-z]", "", _fold(word).lower()) for word in words}
    if normalized & NON_PERSON_WORDS:
        return None
    return value if _valid_person_words(words, surname_first) else None


def clean_candidate(value, entity_type):
    value = value.strip()
    if entity_type == "PERSON":
        value = value.splitlines()[0].strip()
        value = value.split(" - ", 1)[0].strip()
        value = re.sub(r"^(?:Comm\.)\s+", "", value)
        return clean_person(value)
    elif entity_type == "LOCATION":
        if value.lower() in TECH_LOCATIONS or value.lower().endswith(".js"):
            return None
    return value


def analyze(analyzer, text):
    results = analyzer.analyze(
        text=text,
        language="en",
        entities=list(CATEGORY_MAP.keys()),
        score_threshold=0.45,
        return_decision_process=True,
    )
    best = {}
    for result in results:
        value = clean_candidate(text[result.start : result.end], result.entity_type)
        if not value:
            continue
        key = (value.casefold(), CATEGORY_MAP[result.entity_type])
        explanation = result.analysis_explanation
        recognizer = getattr(explanation, "recognizer", None) or "Presidio"
        context = getattr(explanation, "supportive_context_word", None)
        reason = recognizer if not context else f"{recognizer}; context: {context}"
        item = {
            "value": value,
            "category": CATEGORY_MAP[result.entity_type],
            "confidence": round(float(result.score), 3),
            "source": "Presidio",
            "explanation": reason,
        }
        if key not in best or item["confidence"] > best[key]["confidence"]:
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
                raise ValueError("texts must be a list of strings")
            analyzer = create_analyzer()
            print(json.dumps({"batches": [analyze(analyzer, text) for text in texts]}))
            return 0
        text = request.get("text", "")
        if not isinstance(text, str):
            raise ValueError("text must be a string")
        print(json.dumps({"candidates": analyze(create_analyzer(), text)}))
    except Exception as exc:
        print(json.dumps({"error": "The analyzer failed (" + type(exc).__name__ + ")."}))
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

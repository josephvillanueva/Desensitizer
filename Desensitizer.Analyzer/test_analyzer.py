"""Unit tests for analyzer.py that need no model pack.

The Presidio, spaCy and tldextract packages are replaced by small stand-ins, so only the pure Python
logic runs: candidate cleaning and the construction of the PH_TIN recognizer. Run from this folder:

    python -m unittest test_analyzer -v

Not covered (needs the real packs): how Presidio scores the recognizer, and the spaCy person model.
"""
import importlib
import re
import sys
import types
import unittest


def _install_stubs():
    tld = types.ModuleType("tldextract")
    tld.TLDExtract = lambda **kwargs: None
    tld.extract = None

    class Pattern:
        def __init__(self, name, regex, score):
            self.name, self.regex, self.score = name, regex, score

    class PatternRecognizer:
        def __init__(self, supported_entity, name, patterns, context, supported_language):
            self.supported_entity, self.name = supported_entity, name
            self.patterns, self.context, self.supported_language = patterns, context, supported_language

    class Registry:
        def __init__(self):
            self.recognizers = []

        def add_recognizer(self, recognizer):
            self.recognizers.append(recognizer)

    class AnalyzerEngine:
        def __init__(self, nlp_engine=None, supported_languages=None):
            self.registry = Registry()

    class NlpEngineProvider:
        def __init__(self, nlp_configuration=None):
            pass

        def create_engine(self):
            return object()

    class PhoneRecognizer:
        def __init__(self, supported_regions=None, supported_entity=None):
            self.supported_entity = supported_entity
            self.patterns = []

    presidio = types.ModuleType("presidio_analyzer")
    presidio.AnalyzerEngine, presidio.Pattern, presidio.PatternRecognizer = AnalyzerEngine, Pattern, PatternRecognizer
    nlp = types.ModuleType("presidio_analyzer.nlp_engine")
    nlp.NlpEngineProvider = NlpEngineProvider
    predefined = types.ModuleType("presidio_analyzer.predefined_recognizers")
    predefined.PhoneRecognizer = PhoneRecognizer
    sys.modules.update({
        "tldextract": tld,
        "presidio_analyzer": presidio,
        "presidio_analyzer.nlp_engine": nlp,
        "presidio_analyzer.predefined_recognizers": predefined,
    })


_install_stubs()
analyzer = importlib.import_module("analyzer")


def person(value):
    return analyzer.clean_candidate(value, "PERSON")


class CleanPersonTests(unittest.TestCase):
    def test_plain_names_still_accepted(self):
        for value in ["Maria Santos", "Celia R. Vale", "NNCI AGENT", "Anne-Marie O'Brien", "Joseph Tan"]:
            self.assertEqual(person(value), value, value)

    def test_lowercase_particles_are_allowed(self):
        for value in ["Maria de la Cruz", "Corazon K. delos Santos", "Juan del Rosario",
                      "Maria dela Cruz", "Hendrik van der Berg", "Anna von Eisen"]:
            self.assertEqual(person(value), value, value)

    def test_diacritics_are_allowed(self):
        for value in ["Maria Peña", "José Rizal", "Zoë García"]:
            self.assertEqual(person(value), value, value)

    def test_comma_form_with_three_or_more_words(self):
        self.assertEqual(person("Santos, Maria T."), "Santos, Maria T.")
        self.assertEqual(person("de la Cruz, Maria"), "de la Cruz, Maria")
        self.assertEqual(person("delos Santos, Corazon K."), "delos Santos, Corazon K.")
        self.assertEqual(person("Santos, Maria T., Jr."), "Santos, Maria T., Jr.")

    def test_negative_two_word_comma_form_is_a_list_not_a_name(self):
        self.assertIsNone(person("Santos, Maria"))
        self.assertIsNone(person("Maria, Juan"))

    def test_negative_too_many_commas(self):
        self.assertIsNone(person("Santos, Maria, Juan, Pedro"))
        self.assertIsNone(person("Santos, Maria T., Cruz"))

    def test_negative_lowercase_words_that_are_not_particles(self):
        for value in ["Maria the Cruz", "Maria santos", "the system", "Maria shall Cruz"]:
            self.assertIsNone(person(value), value)

    def test_negative_particle_at_the_edge(self):
        self.assertIsNone(person("de la Cruz"))
        self.assertIsNone(person("Maria de"))
        self.assertIsNone(person("la"))

    def test_negative_digits_and_symbols(self):
        for value in ["Maria S4ntos", "Maria Santos2", "Maria @Santos", "Maria Santos!"]:
            self.assertIsNone(person(value), value)

    def test_negative_role_words_and_size(self):
        self.assertIsNone(person("Software Engineer"))
        self.assertIsNone(person("Business Analyst"))
        self.assertIsNone(person("Maria"))
        self.assertIsNone(person("One Two Three Four Five Six Seven"))
        self.assertIsNone(person("José Software"))

    def test_existing_prefix_and_dash_rules_still_apply(self):
        self.assertEqual(person("Comm. Maria Santos"), "Maria Santos")
        self.assertEqual(person("Maria Santos - Director"), "Maria Santos")
        self.assertEqual(person("Maria Santos\nManager"), "Maria Santos")

    def test_location_rules_unchanged(self):
        self.assertIsNone(analyzer.clean_candidate("Node.js", "LOCATION"))
        self.assertEqual(analyzer.clean_candidate("Quezon City", "LOCATION"), "Quezon City")


class TinRecognizerTests(unittest.TestCase):
    def setUp(self):
        self.engine = analyzer.create_analyzer()
        self.tin = [r for r in self.engine.registry.recognizers if getattr(r, "supported_entity", "") == "PH_TIN"]

    def test_recognizer_is_registered_once_for_the_mapped_entity(self):
        self.assertEqual(len(self.tin), 1)
        self.assertIn("PH_TIN", analyzer.CATEGORY_MAP)
        self.assertEqual(analyzer.CATEGORY_MAP["PH_TIN"], "IDENTIFIER")
        self.assertEqual(self.tin[0].supported_language, "en")

    def _score(self, text):
        best = 0.0
        for pattern in self.tin[0].patterns:
            if re.search(pattern.regex, text):
                best = max(best, pattern.score)
        return best

    def test_dashed_forms_clear_the_score_threshold(self):
        for text in ["TIN 123-456-789", "TIN: 123-456-789-000", "123-456-789-00000", "no label 123-456-789"]:
            self.assertGreaterEqual(self._score(text), 0.45, text)

    def test_negative_bare_digits_need_a_label_to_reach_the_threshold(self):
        # The bare pattern scores 0.3; only Presidio's context boost over a TIN label lifts it.
        self.assertLess(self._score("order 123456789 shipped"), 0.45)
        self.assertGreater(self._score("order 123456789 shipped"), 0.0)
        self.assertTrue(any(word in self.tin[0].context for word in ("tin", "tax id")))

    def test_negative_near_misses_do_not_match(self):
        for text in ["123-45-6789", "12-345-6789", "1234-567-890", "123-456-78", "917-123-4567",
                     "2026-10-10", "123-456-789-1", "123-456-789-123456", "A123-456-789", "123-456-789A"]:
            self.assertEqual(self._score(text), 0.0, text)

    def test_negative_longer_digit_runs_are_not_cut(self):
        self.assertEqual(self._score("1234567890"), 0.0)
        self.assertEqual(self._score("12345678"), 0.0)


if __name__ == "__main__":
    unittest.main()

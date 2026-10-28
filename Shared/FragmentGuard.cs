using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Fragment guard (stage S13).
//
// An approved PERSON, ORGANIZATION or VENDOR mapping holds a FULL name. A document can also contain only a shortened
// form of it (a lone first name, a lone surname, the company stem). Replacing the full name leaves that fragment
// readable. The guard finds those standalone fragments and turns each into a finding that needs an analyst decision.
//
// Policy: a wrong merge of two different people or companies is worse than a miss. A fragment is therefore NEVER given
// the token of the full name automatically. It is a Review item (RequiresContextReview, not recommended for replacement),
// it blocks the export until decided, and Replace gives it its own token. Keep as-is is stored in the project like any
// other Keep decision, but it is scoped to the category family and to the evidence text (ContextReviewEvidence), so it
// cannot suppress a different finding. Explanations and messages never quote a value.
public sealed class FragmentHit
{
    public string Value = "";
    public string Category = "";
    // The approved mapping this word was taken from. One hit per (word, owning entity), so a surname shared by two
    // approved people yields two hits that each know the other exists.
    public Mapping Owner = null!;
    // How many different approved entities (different tokens) contain this word. More than one means ambiguous.
    public int Entities;
    public int Occurrences;
    // The mapping that already decides this span (replaced or kept), or null when the analyst still has to decide.
    public Mapping? DecidedBy;
    public Candidate Candidate = null!;
    public bool Decided => DecidedBy != null;
}

public sealed class FragmentGate
{
    public List<Candidate> Undecided = new();
    public bool Blocked => Undecided.Count > 0;
    public string Message = "";
}

public static class FragmentGuard
{
    public const string SourceName = "Fragment of approved name";
    public const int MinLetters = 4;
    private const int MaxComponentsPerMapping = 12;
    private const int MaxHitsPerCategory = 400;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // Letters with their combining marks, joined by hyphens (ordinary, U+2010, U+2011). An apostrophe splits a word.
    private static readonly Regex Word = new(@"\p{L}[\p{L}\p{M}]*(?:[-‐‑]\p{L}[\p{L}\p{M}]*)*", RegexOptions.Compiled);
    private static readonly char[] Hyphens = { '-', '‐', '‑' };

    private static readonly HashSet<string> Honorifics = new(StringComparer.Ordinal)
    { "mr", "ms", "mrs", "miss", "dr", "prof", "engr", "atty", "hon", "fr", "sir", "madam", "mx" };
    private static readonly HashSet<string> NameSuffixes = new(StringComparer.Ordinal) { "jr", "sr", "ii", "iii", "iv" };

    // Ordinary English words that also occur as names. A name part spelled like one of these is too noisy to guard.
    // The analyst can still add the exact value as a manual mapping. Kept deliberately short and unambiguous.
    private static readonly HashSet<string> OrdinaryWords = new(StringComparer.Ordinal)
    {
        "this", "that", "with", "from", "have", "will", "shall", "should", "would", "could", "their", "there", "these", "those",
        "about", "after", "before", "under", "over", "into", "onto", "upon", "when", "where", "which", "while", "what", "whom",
        "name", "names", "user", "users", "date", "time", "type", "code", "list", "text", "form", "page", "item", "file", "data",
        "none", "null", "true", "false", "open", "close", "save", "edit", "view", "send", "next", "back", "done", "test", "tests",
        "mark", "grant", "bill", "rose", "hope", "faith", "grace", "will", "page", "park", "hall", "mill", "ford", "wood", "field",
        "january", "february", "march", "april", "june", "july", "august", "september", "october", "november", "december",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "approval", "workflow", "threshold",
        "status", "amount", "number", "level", "stage", "review", "request", "process", "report", "account", "manager", "director"
    };

    // ------------------------------------------------------------------------------------------ public API

    // The findings that still need an analyst decision, one per distinct word and category.
    public static List<Candidate> Find(string text, IEnumerable<Mapping> mappings, string? fileId = null) =>
        Merge(Analyze(text, mappings, fileId).Where(h => !h.Decided));

    // The final-safety view: a text, the project mappings and the file. Blocked means the export must stop.
    public static FragmentGate Gate(string text, IEnumerable<Mapping> mappings, string? fileId = null)
    {
        var gate = new FragmentGate { Undecided = Find(text, mappings, fileId) };
        gate.Message = gate.Blocked ? BlockMessage(gate.Undecided) : "";
        return gate;
    }

    public static bool IsFragment(Mapping mapping) =>
        mapping.DetectionSources != null && mapping.DetectionSources.Contains(SourceName, StringComparer.OrdinalIgnoreCase)
        || string.Equals(mapping.DetectionSource, SourceName, StringComparison.OrdinalIgnoreCase);

    public static bool IsFragment(Candidate candidate) => string.Equals(candidate.Source, SourceName, StringComparison.OrdinalIgnoreCase);

    // Counts and categories only. Never a value.
    public static string BlockMessage(IEnumerable<Candidate> fragments)
    {
        var list = fragments.ToList();
        var parts = list.GroupBy(c => c.Category).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Count() + " " + g.Key).ToList();
        var ambiguous = list.Count(c => c.Explanation.Contains("ambiguous", StringComparison.Ordinal));
        return "Export stopped: " + list.Count + " shortened name" + (list.Count == 1 ? "" : "s") + " (" + string.Join(", ", parts) + ") still need" + (list.Count == 1 ? "s" : "") + " a decision. "
            + "Each is a word that belongs to an approved name but stands alone in this document, so it was not replaced. "
            + (ambiguous > 0 ? ambiguous + " could belong to more than one approved name. " : "")
            + "They are now in Review. Choose Replace to give a word its own token, or Keep as-is if it is not identifying. A word is never linked to the full name automatically.";
    }

    // ------------------------------------------------------------------------------------------ analysis

    public static List<FragmentHit> Analyze(string text, IEnumerable<Mapping> mappings, string? fileId = null)
    {
        var hits = new List<FragmentHit>();
        if (string.IsNullOrEmpty(text)) return hits;
        var all = mappings as IList<Mapping> ?? mappings.ToList();
        var owners = all.Where(IsOwner).ToList();
        if (owners.Count == 0) return hits;

        // word -> the approved mappings that contain it
        var components = new Dictionary<string, List<Mapping>>(StringComparer.Ordinal);
        foreach (var owner in owners)
            foreach (var word in Components(owner))
            {
                if (!components.TryGetValue(word, out var list)) components[word] = list = new List<Mapping>();
                list.Add(owner);
            }
        if (components.Count == 0) return hits;

        var allCaps = IsAllCapsDocument(text);
        var decided = all.Where(m => m.Approved && !m.RequiresContextReview || m.Ignored && !m.RequiresContextReview).ToList();
        var decidedByWord = new Dictionary<string, List<Mapping>>(StringComparer.Ordinal);
        foreach (var m in decided)
            foreach (var word in Words(m.OriginalValue).SelectMany(w => PartsOf(w, true)).Distinct())
            {
                if (!decidedByWord.TryGetValue(word, out var list)) decidedByWord[word] = list = new List<Mapping>();
                list.Add(m);
            }
        var spans = new Dictionary<Mapping, List<(int Start, int End)>>();

        // (word, category) -> aggregated state
        var found = new Dictionary<string, (string Value, string Category, List<Mapping> Owners, int Count, Mapping? DecidedBy)>(StringComparer.Ordinal);
        var perCategory = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in Word.Matches(text))
        {
            var folded = Fold(match.Value);
            if (folded.Length < MinLetters) continue;
            List<Mapping>? hitOwners = null;
            if (components.TryGetValue(folded, out var direct)) hitOwners = direct;
            else if (folded.IndexOfAny(Hyphens) >= 0)
                foreach (var part in folded.Split(Hyphens, StringSplitOptions.RemoveEmptyEntries))
                    if (components.TryGetValue(part, out var viaPart)) (hitOwners ??= new List<Mapping>()).AddRange(viaPart);
            if (hitOwners == null) continue;

            // A lone person word must be written like a name, unless the whole document is upper case.
            var capitalised = char.IsUpper(FirstLetter(match.Value));
            var usable = hitOwners.Where(o => o.Category != "PERSON" || capitalised || allCaps).Distinct().ToList();
            if (usable.Count == 0) continue;

            foreach (var group in usable.GroupBy(o => Family(o.Category)))
            {
                var category = group.First().Category;
                var key = category + "|" + Project.Normalize(match.Value);
                var decidedBy = CoveringMapping(match.Index, match.Length, folded, group.Key, text, decidedByWord, spans);
                if (found.TryGetValue(key, out var state))
                {
                    state.Count++;
                    if (state.DecidedBy != null && decidedBy == null) state.DecidedBy = null;
                    found[key] = state;
                    continue;
                }
                if (!perCategory.TryGetValue(category, out var seen)) seen = 0;
                if (seen >= MaxHitsPerCategory) continue;
                perCategory[category] = seen + 1;
                found[key] = (match.Value, category, group.Distinct().ToList(), 1, decidedBy);
            }
        }

        foreach (var (value, category, hitOwners, count, decidedBy) in found.Values)
        {
            var entities = hitOwners.Select(o => o.Token ?? "").Distinct(StringComparer.Ordinal).Count();
            foreach (var ownerGroup in hitOwners.GroupBy(o => o.Token ?? ""))
            {
                var candidate = new Candidate
                {
                    Value = value, Category = category, Confidence = 0.9, Source = SourceName,
                    DetectionSources = new List<string> { SourceName }, RequiresContextReview = true,
                    Explanation = Explain(category, entities)
                };
                var hit = new FragmentHit
                {
                    Value = value, Category = category, Owner = ownerGroup.First(), Entities = entities, Occurrences = count,
                    DecidedBy = decidedBy, Candidate = candidate
                };
                // A Keep decision made for exactly this fragment only counts while its evidence is unchanged.
                if (hit.DecidedBy != null && IsFragment(hit.DecidedBy) && hit.DecidedBy.Ignored
                    && !string.Equals(hit.DecidedBy.ContextReviewAcknowledgement, ContextReviewEvidence.Signature(candidate, fileId), StringComparison.Ordinal))
                    hit.DecidedBy = null;
                hits.Add(hit);
            }
        }
        return hits;
    }

    private static List<Candidate> Merge(IEnumerable<FragmentHit> hits) =>
        hits.GroupBy(h => h.Category + "|" + Project.Normalize(h.Value)).Select(g => g.First().Candidate).ToList();

    // Words of an approved mapping that deserve a guard. Never the whole value.
    private static bool IsOwner(Mapping m) =>
        m.Approved && !m.RequiresContextReview && !IsFragment(m) && m.Category is "PERSON" or "ORGANIZATION" or "VENDOR"
        && !string.IsNullOrWhiteSpace(m.OriginalValue);

    private static string Family(string category) => category == "PERSON" ? "PERSON" : "COMPANY";

    private static string Explain(string category, int entities)
    {
        var kind = category == "PERSON" ? "person" : category == "VENDOR" ? "vendor" : "company";
        return entities > 1
            ? "This word is part of " + entities + " different approved " + kind + " names in this project, so it is ambiguous which one it refers to. It stands alone here and was not replaced. It is never linked to any of them automatically. Replace gives it its own token; Keep as-is leaves it unchanged."
            : "This word is part of an approved " + kind + " name in this project but stands alone here, so it was not replaced. It may be the same " + kind + " or a different one. It is never linked to the full name automatically. Replace gives it its own token; Keep as-is leaves it unchanged.";
    }

    // ------------------------------------------------------------------------------------------ components

    public static IReadOnlyList<string> Components(Mapping mapping)
    {
        var result = new List<string>();
        void Add(string word)
        {
            if (word.Count(char.IsLetter) < MinLetters || result.Contains(word)) return;
            if (IsNoise(word, mapping.Category)) return;
            if (result.Count < MaxComponentsPerMapping) result.Add(word);
        }
        if (mapping.Category == "PERSON")
        {
            var words = Words(mapping.OriginalValue).Where(w => !Honorifics.Contains(w) && !NameSuffixes.Contains(w)).ToList();
            // A single plain word has no shorter form to guard. A single hyphenated surname still has parts.
            if (words.Count == 1 && words[0].IndexOfAny(Hyphens) < 0) return result;
            foreach (var word in words)
            {
                if (!(words.Count == 1)) Add(word);
                foreach (var part in PartsOf(word, false)) Add(part);
            }
            return result;
        }
        var (core, suffix) = CompanyIdentity.Split(mapping.OriginalValue);
        if (core.Count == 0 || core.Count == 1 && suffix.Length == 0) return result;
        // The first distinctive word is the company stem. Later words are usually descriptors.
        foreach (var raw in core)
        {
            var word = Fold(raw);
            if (word.Length < MinLetters || CompanyIdentity.IsCommonWord(raw) || CompanyIdentity.IsSmallWord(raw)) continue;
            Add(word);
            break;
        }
        return result;
    }

    private static bool IsNoise(string word, string category)
    {
        if (OrdinaryWords.Contains(word)) return true;
        if (PublicTechnologyPolicy.IsPublicTechnology(word)) return true;
        // The generic lists, without the side effect of recording a suppressed organization.
        if (DocumentPrivacyPolicy.IsGeneric(word, category == "PERSON" ? "PERSON" : "SYSTEM")) return true;
        if (category != "PERSON" && CompanyIdentity.IsCommonWord(word)) return true;
        return false;
    }

    // Folded, lower-case words of a value. Hyphenated words stay whole here.
    private static List<string> Words(string value) =>
        Word.Matches((value ?? "").Replace('’', ' ').Replace('\'', ' ')).Cast<Match>().Select(m => Fold(m.Value)).ToList();

    private static IEnumerable<string> PartsOf(string word, bool includeWhole)
    {
        if (word.IndexOfAny(Hyphens) < 0) { yield return word; yield break; }
        if (includeWhole) yield return word;
        foreach (var part in word.Split(Hyphens, StringSplitOptions.RemoveEmptyEntries)) yield return part;
    }

    // ------------------------------------------------------------------------------------------ text helpers

    // Diacritics folded, upper case folded, every hyphen form ordinary. Peña and PENA and pena are one word.
    public static string Fold(string value)
    {
        var decomposed = (value ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c is '‐' or '‑' ? '-' : char.ToLowerInvariant(c));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static char FirstLetter(string value)
    {
        foreach (var c in value) if (char.IsLetter(c)) return c;
        return 'a';
    }

    // Mostly upper case text (at least 40 letters, fewer than 3 percent lower case): capitalisation carries no meaning.
    public static bool IsAllCapsDocument(string text)
    {
        var letters = 0; var lower = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (char.IsLower(c)) lower++;
        }
        return letters >= 40 && lower * 33 <= letters;
    }

    // The decided mapping (approved or kept) whose own text covers this word, so it is not a standalone fragment.
    private static Mapping? CoveringMapping(int index, int length, string folded, string family, string text,
        Dictionary<string, List<Mapping>> decidedByWord, Dictionary<Mapping, List<(int Start, int End)>> spans)
    {
        var candidates = new List<Mapping>();
        foreach (var word in PartsOf(folded, true))
            if (decidedByWord.TryGetValue(word, out var list)) candidates.AddRange(list);
        foreach (var mapping in candidates.Distinct())
        {
            if (mapping.Ignored && !mapping.Approved && Family(mapping.Category) != family) continue;
            if (!spans.TryGetValue(mapping, out var list))
            {
                list = new List<(int, int)>();
                try
                {
                    foreach (Match m in Regex.Matches(text, DocumentPrivacyPolicy.Pattern(mapping.OriginalValue.Trim(), true), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
                        list.Add((m.Index, m.Index + m.Length));
                }
                catch (RegexMatchTimeoutException) { }
                spans[mapping] = list;
            }
            foreach (var (start, end) in list)
                if (start <= index && index + length <= end)
                {
                    // A longer mapped span covers the word. The span has to be longer, or exactly the word itself.
                    return mapping;
                }
        }
        return null;
    }
}

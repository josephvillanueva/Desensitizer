using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Stored inside the encrypted mapping, scoped to the document that actually
// declared the acronym. An empty list records a completed check with no declaration.
public sealed class CompanyIntroduction
{
    public string Name { get; set; } = "";
    public bool Quoted { get; set; }
}

// Per-document ORGANIZATION / VENDOR alias consolidation.
//
// Policy: a wrong merge (two different companies under one token) is worse than a missed alias,
// because one token for two entities corrupts meaning and leaks linkage. Every rule below links
// ONLY detected findings (never creates one) and only when the evidence is unique in the document.
//
//  (a) Acronym introduction: "Full Name (ABC)" or "Full Name (\"ABC\")". The acronym joins the name
//      printed directly before it when the letters are the initials of its significant words, or the
//      parenthesised form is an explicit definition (quoted, or three or more letters that are not a
//      region, currency or common business abbreviation). Two different introductions never link.
//  (b) Legal-suffix variants: identical word lists after stripping trailing legal suffixes share a
//      token when the suffix is the same kind ("Acme Inc" and "Acme Inc."; punctuation only). Different
//      suffixes on otherwise identical names ("Acme Inc" and "Acme LLC") stay separate. A bare form
//      ("Acme") joins a full form only when exactly one full form exists in the document.
//  (c) Unique-stem short form: a detected short form of 1 to 2 words (at least 5 letters, not a common
//      word, public technology or generic label) joins a longer name only when exactly one company in
//      the document starts with that stem.
//  (d) Never merged: names that only share a first word ("Brightmoor Logistics", "Brightmoor Holdings"),
//      anything with two candidates, common words, different categories, manual, kept-as-is and
//      RequiresContextReview mappings. Such mappings still count as candidates for the uniqueness tests.
public static class CompanyIdentity
{
    // ---- introduction evidence carried from the post-processor to the mapping ------------------

    // Candidate explanations carry provisional introduction notes until source
    // occurrences are refreshed. They are not project-wide identity evidence.
    private static readonly Regex IntroductionNote = new(
        "Introduced as the (?<kind>acronym|defined short name) of \"(?<name>[^\"]+)\"\\.", RegexOptions.Compiled);

    private static readonly Regex IntroductionAfterName = new(
        "^\\s*\\(\\s*(?<q>[\"“”‘’'])?(?<a>[A-Z][A-Z0-9&.-]{1,14})[\"“”’']?\\s*\\)",
        RegexOptions.Compiled);

    // Reads "(ABC)" or "(\"ABC\")" at the start of the text that follows a detected name.
    public static bool TryReadIntroduction(string textAfterName, out string acronym, out bool quoted)
    {
        var match = IntroductionAfterName.Match(textAfterName);
        acronym = match.Success ? match.Groups["a"].Value : "";
        quoted = match.Success && match.Groups["q"].Success;
        return match.Success;
    }

    public static string NoteFor(string fullName, bool quoted)
        => "Introduced as the " + (quoted ? "defined short name" : "acronym") + " of \"" + fullName.Replace('"', '\'').Trim() + "\".";

    // The document policy keeps one finding per value and drops the others' explanations, so the
    // post-processor remembers each introduction by acronym and re-attaches it to the surviving finding
    // (whichever reviewer or detector reported the acronym).
    public static void RememberIntroduction(Dictionary<string, List<string>> notes, string acronym, string fullName, bool quoted)
    {
        var key = Project.Normalize(acronym);
        if (!notes.TryGetValue(key, out var list)) notes[key] = list = new List<string>();
        var note = NoteFor(fullName, quoted);
        if (!list.Contains(note)) list.Add(note);
    }

    // Used when a document is (re)scanned into a project that already holds the acronym from another
    // document: the introduction is found again in this document's text and recorded on the mapping.
    public static void RecordIntroductions(string fileId, string text, Mapping acronym, IEnumerable<Mapping> mappings)
    {
        if (acronym.Category is not ("ORGANIZATION" or "VENDOR")) return;
        acronym.CompanyIntroductions ??= new Dictionary<string, List<CompanyIntroduction>>();
        var evidence = new List<CompanyIntroduction>();
        // Replace, never append: a rescan can remove or change an introduction.
        acronym.CompanyIntroductions[fileId] = evidence;
        var value = acronym.OriginalValue.Trim();
        if (!Regex.IsMatch(value, @"^[A-Z][A-Z0-9&.-]{1,14}$")) return;
        var pattern = new Regex(@"\(\s*(?<q>[""“”‘’'])?" + Regex.Escape(value) + @"[""“”’']?\s*\)", RegexOptions.None, TimeSpan.FromSeconds(1));
        var candidates = mappings.Where(m => m != acronym && m.Category == acronym.Category &&
                m.SourceFileIds.Contains(fileId) && !Regex.IsMatch(m.OriginalValue.Trim(), @"^[A-Z][A-Z0-9&.-]{1,14}$"))
            .OrderByDescending(m => m.OriginalValue.Length).ToList();
        MatchCollection found;
        try { found = pattern.Matches(text); _ = found.Count; }
        catch (RegexMatchTimeoutException) { return; }
        foreach (Match match in found)
        {
            var before = text.Substring(Math.Max(0, match.Index - 160), Math.Min(160, match.Index)).TrimEnd();
            foreach (var name in candidates)
            {
                var full = name.OriginalValue.Trim();
                if (!before.EndsWith(full, StringComparison.OrdinalIgnoreCase)) continue;
                var start = before.Length - full.Length;
                if (start > 0 && char.IsLetterOrDigit(before[start - 1])) continue;
                var quoted = match.Groups["q"].Success;
                if (!evidence.Any(e => e.Name == name.OriginalValue && e.Quoted == quoted))
                    evidence.Add(new CompanyIntroduction { Name = name.OriginalValue, Quoted = quoted });
                break;
            }
        }
    }

    public static void CarryIntroductions(Candidate best, Dictionary<string, List<string>> notes)
    {
        if (best.Category is not ("ORGANIZATION" or "VENDOR") || !notes.TryGetValue(Project.Normalize(best.Value), out var list)) return;
        var missing = list.Where(n => !(best.Explanation ?? "").Contains(n)).ToList();
        if (missing.Count > 0) best.Explanation = ((best.Explanation ?? "").TrimEnd() + " " + string.Join(" ", missing)).Trim();
    }

    // ---- parsing ---------------------------------------------------------------------------------

    private static readonly HashSet<string> SmallWords = new() { "THE", "OF", "AND", "FOR", "IN", "ON", "DE", "DEL", "LA", "AT" };
    private static readonly HashSet<string> LegalSuffixes = new()
    {
        "INC", "INCORPORATED", "CORP", "CORPORATION", "COMPANY", "CO", "LTD", "LIMITED", "LLC", "LLP", "LP", "PLC",
        "GMBH", "AG", "SA", "BV", "NV", "PTE", "PTY", "PLLC", "OPC", "SDN", "BHD", "SRL", "SARL", "KG"
    };
    private static readonly HashSet<string> CommonWords = new()
    {
        "GLOBAL", "NATIONAL", "INTERNATIONAL", "UNITED", "GENERAL", "FIRST", "NEW", "CENTRAL", "PACIFIC", "ASIA", "AMERICAN",
        "PHILIPPINE", "PHILIPPINES", "DIGITAL", "SYSTEM", "SYSTEMS", "SERVICE", "SERVICES", "SOLUTION", "SOLUTIONS", "TECHNOLOGY",
        "TECHNOLOGIES", "GROUP", "HOLDINGS", "BANK", "FINANCE", "FINANCIAL", "TREASURY", "OPERATIONS", "MARKETING", "SALES", "LEGAL",
        "ENGINEERING", "SUPPORT", "CUSTOMER", "ADMIN", "ADMINISTRATION", "DEPARTMENT", "DIVISION", "OFFICE", "BUREAU", "MINISTRY",
        "AGENCY", "AUTHORITY", "COMMISSION", "COUNCIL", "BOARD", "COMMITTEE", "TEAM", "UNIT", "PROJECT", "BUSINESS", "INDUSTRIES",
        "ENTERPRISES", "PARTNERS", "ASSOCIATES", "CLIENT", "VENDOR", "SUPPLIER", "PROVIDER", "COMPANY", "CORPORATION", "CAPITAL",
        "TRADING", "LOGISTICS", "ENERGY", "INSURANCE", "TELECOM", "SHIPPING", "FOODS", "MANUFACTURING", "PARTNER", "CONSULTING",
        "MANAGEMENT", "RESOURCES", "NETWORK", "NETWORKS", "SECURITY", "SOFTWARE", "PRODUCTS", "MEDIA", "HEALTH", "HEALTHCARE",
        "TRANSPORT", "TRANSPORTATION", "CONSTRUCTION", "PROPERTIES", "REALTY", "TRUST", "FUND", "LABS", "STUDIO", "STUDIOS",
        "GOLDEN", "BLUE", "GREEN", "RED", "SILVER", "NORTH", "SOUTH", "EAST", "WEST", "ROYAL", "PRIME", "ALPHA", "BETA", "OMEGA",
        "UNIVERSAL", "STANDARD", "ADVANCED", "APPLIED", "INTEGRATED", "PREMIER", "SUPREME", "MODERN", "METRO", "CITY", "STATE",
        "TRIBAL", "GOVERNMENT", "PUBLIC", "PRIVATE", "OPEN", "SMART", "RAPID", "EXPRESS", "DIRECT", "SECURE", "TRUSTED"
    };
    // Parenthesised terms that are not company abbreviations: places, currencies, document words.
    private static readonly HashSet<string> NotAnAcronym = new()
    {
        "USA", "UK", "US", "EU", "UAE", "PHL", "PHI", "SGP", "NCR", "EMEA", "APAC", "LATAM", "ASEAN", "HQ", "USD", "PHP", "EUR", "GBP",
        "SGD", "JPY", "CAD", "AUD", "CNY", "TBD", "NDA", "SLA", "KPI", "API", "SQL", "SSO", "VPN", "UAT", "SMS", "PDF", "ERP", "CRM",
        "FAQ", "URL", "SDK", "CSV", "MVP", "POC", "SOW", "MSA", "RFP", "BRD", "FDS", "FRS", "SRS", "BPO", "NEW", "OLD", "ALL", "FYI",
        "DRAFT", "FINAL", "TEST", "PROD", "DEV", "QA", "HR", "IT", "ID", "INC", "LLC", "LTD", "LLP", "PLC", "PTE", "CORP", "GMBH", "OPC"
    };

    private sealed class Item
    {
        public Mapping M = null!;
        public int Index;
        public string Category = "";
        public string[] Docs = Array.Empty<string>();
        public List<string> Core = new();
        public string CoreKey = "";
        public string Suffix = "";
        public bool Blocked;
        public List<(string Name, bool Quoted)> Intro = new();
        public bool IsFull => Suffix.Length > 0;
    }

    // Used by the fragment guard (FragmentGuard.cs): words that never make a company distinctive on their own.
    public static bool IsCommonWord(string word) => CommonWords.Contains((word ?? "").ToUpperInvariant());
    public static bool IsSmallWord(string word) => SmallWords.Contains((word ?? "").ToUpperInvariant());

    private static string Fold(string value)
    {
        var decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
        var kept = decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string(kept.ToArray()).Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();
    }

    private static string LegalFamily(string word) => word switch
    {
        "INCORPORATED" => "INC", "CORPORATION" => "CORP", "LIMITED" => "LTD", "COMPANY" => "CO", _ => word
    };

    // Every word of a company name, plus the kind of trailing legal suffix.
    public static (List<string> Core, string Suffix) Split(string value)
    {
        // Keep every name word, including non-Latin scripts and small words. Removing
        // them can make two legally different names look identical. Small words are
        // omitted only when constructing acronym initials, not for name equality.
        var words = Regex.Matches(Fold(value), @"[\p{L}\p{N}]+(?:['-][\p{L}\p{N}]+)?").Cast<Match>().Select(m => m.Value).ToList();
        var suffix = new List<string>();
        while (words.Count > 1 && LegalSuffixes.Contains(words[^1]))
        {
            suffix.Insert(0, LegalFamily(words[^1]));
            words.RemoveAt(words.Count - 1);
        }
        return (words, string.Join(" ", suffix));
    }

    private static bool IsCommonOrGeneric(List<string> core, string category, string originalValue)
    {
        if (core.Count == 1 && CommonWords.Contains(core[0])) return true;
        if (core.All(w => CommonWords.Contains(w))) return true;
        var text = string.Join(" ", core.Select(w => w.Length > 1 ? w[0] + w.Substring(1).ToLowerInvariant() : w));
        if (PublicTechnologyPolicy.IsPublicTechnology(text) || PublicTechnologyPolicy.IsPublicTechnology(originalValue)) return true;
        if (LocalAiReview.IsClearlyGenericCandidate(originalValue, category) || LocalAiReview.IsClearlyGenericCandidate(text, category)) return true;
        return false;
    }

    private static string[] DocsOf(Mapping m)
    {
        var docs = (m.SourceFileIds ?? new List<string>()).Concat(m.IdentityFileIds ?? new List<string>()).Distinct().ToArray();
        return docs.Length == 0 ? new[] { "" } : docs;
    }

    private static bool Shares(Item a, Item b) => a.Docs.Intersect(b.Docs).Any();

    private static IEnumerable<(string Name, bool Quoted)> IntroductionsOf(Mapping mapping)
    {
        var activeDocs = (mapping.SourceFileIds ?? new List<string>()).Distinct(StringComparer.Ordinal).ToList();
        if (mapping.CompanyIntroductions != null)
        {
            // A mapping is project-wide. Evidence in one file must not silently
            // resolve the same acronym in another, unchecked or conflicting file.
            if (activeDocs.Count == 0) yield break;
            foreach (var doc in activeDocs)
                if (!mapping.CompanyIntroductions.TryGetValue(doc, out var evidence) || evidence == null ||
                    evidence.Count == 0 || evidence.Any(e => e == null || string.IsNullOrWhiteSpace(e.Name)))
                    yield break;
            foreach (var doc in activeDocs)
                foreach (var evidence in mapping.CompanyIntroductions[doc])
                    yield return (evidence.Name, evidence.Quoted);
            yield break;
        }
        // Legacy/provisional notes have no document provenance. They can support
        // the original single-document workflow only, never a cross-file merge.
        if (DocsOf(mapping).Length != 1) yield break;
        foreach (Match note in IntroductionNote.Matches(mapping.Explanation ?? ""))
            yield return (note.Groups["name"].Value, note.Groups["kind"].Value != "acronym");
    }

    // ---- the consolidation -----------------------------------------------------------------------

    public static void Consolidate(List<Mapping> mappings)
    {
        var items = new List<Item>();
        foreach (var category in new[] { "ORGANIZATION", "VENDOR" })
        {
            var tokenPattern = new Regex("^\\[" + category + "_\\d+\\]$");
            foreach (var m in mappings.Where(x => x.Category == category))
            {
                var (core, suffix) = Split(m.OriginalValue);
                var item = new Item { M = m, Index = items.Count, Category = category, Docs = DocsOf(m), Core = core, Suffix = suffix, CoreKey = string.Join(" ", core) };
                item.Blocked = m.IsManual || m.Ignored || m.RequiresContextReview || FragmentGuard.IsFragment(m) || !tokenPattern.IsMatch(m.Token ?? "")
                    || core.Count == 0 || (core.Count == 1 && LegalSuffixes.Contains(core[0]));
                item.Intro.AddRange(IntroductionsOf(m));
                items.Add(item);
            }
        }
        if (items.Count == 0) return;

        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }

        // (b) same words and the same kind of legal suffix: punctuation or spelling only.
        foreach (var group in items.Where(i => !i.Blocked && i.IsFull).GroupBy(i => i.Category + "|" + i.CoreKey + "|" + i.Suffix))
            foreach (var i in group.Skip(1)) Union(group.First().Index, i.Index);

        // A longer detected company that starts with these words, in a shared document.
        bool HasLongerCompany(Item s) => items.Any(t => t != s && t.Core.Count > s.Core.Count && Shares(s, t)
            && t.Core.Take(s.Core.Count).SequenceEqual(s.Core));
        bool SingleWordOk(Item s) => (s.Core.Count > 1 || s.Core[0].Count(char.IsLetter) >= 3) && !IsCommonOrGeneric(s.Core, s.Category, s.M.OriginalValue);

        // (b) bare forms: equal word lists join each other, then the one full form of the document.
        var bareGroups = items.Where(i => !i.Blocked && !i.IsFull && SingleWordOk(i)).GroupBy(i => i.Category + "|" + i.CoreKey).ToList();
        foreach (var group in bareGroups)
        {
            var first = group.First();
            foreach (var i in group.Skip(1)) Union(first.Index, i.Index);
            if (group.Any(HasLongerCompany)) continue; // two candidates: leave the bare form alone
            var targets = new HashSet<string>();
            var ambiguous = false;
            foreach (var doc in group.SelectMany(i => i.Docs).Distinct())
            {
                var fullCandidates = items.Where(t => t.IsFull && t.CoreKey == first.CoreKey && t.Docs.Contains(doc) && (t.Category == "ORGANIZATION" || t.Category == "VENDOR")).ToList();
                // A protected or unresolved spelling is still evidence against an
                // automatic link, even when its legal suffix matches another spelling.
                if (fullCandidates.Any(t => t.Blocked)) { ambiguous = true; break; }
                var fulls = fullCandidates.Select(t => t.Category + "|" + t.Suffix).Distinct().ToList();
                if (fulls.Count == 0) continue;
                if (fulls.Count > 1) { ambiguous = true; break; }
                targets.Add(fulls[0]);
            }
            if (ambiguous || targets.Count != 1) continue;
            var key = targets.First();
            var target = items.Where(t => !t.Blocked && t.IsFull && t.CoreKey == first.CoreKey && t.Category + "|" + t.Suffix == key).FirstOrDefault();
            if (target != null && target.Category == first.Category) Union(first.Index, target.Index);
        }

        // (a) acronym introduced directly after a detected full name.
        foreach (var a in items.Where(i => !i.Blocked && i.Intro.Count > 0))
        {
            var names = a.Intro.Select(x => Project.Normalize(x.Name)).Distinct().ToList();
            if (names.Count != 1) continue;
            var value = a.M.OriginalValue.Trim();
            if (!Regex.IsMatch(value, @"^[A-Z][A-Z0-9&.-]{1,14}$")) continue;
            var targets = items.Where(t => t != a && !t.Blocked && t.Category == a.Category && Project.Normalize(t.M.OriginalValue) == names[0] && Shares(a, t)).ToList();
            if (targets.Count != 1) continue;
            var t0 = targets[0];
            var letters = Regex.Replace(value, "[^A-Z0-9]", "");
            var initials = string.Concat(t0.Core.Where(w => !SmallWords.Contains(w)).Select(w => w[0]));
            var quoted = a.Intro.All(x => x.Quoted);
            var explicitDefinition = letters.Length >= 3 && !NotAnAcronym.Contains(letters) && !LegalSuffixes.Contains(letters)
                && !LocalAiReview.IsClearlyGenericCandidate(value, a.Category);
            var accepted = (letters.Length >= 2 && letters == initials) || (quoted && letters.Length >= 2 && !NotAnAcronym.Contains(letters)) || explicitDefinition;
            if (!accepted) continue;
            // The acronym must not already be a different company's own name in the same document.
            if (items.Any(t => t != a && t.Category == a.Category && Find(t.Index) != Find(a.Index) && t.CoreKey == a.CoreKey && Shares(a, t))) continue;
            Union(a.Index, t0.Index);
        }

        // (c) unique-stem short form, judged on a frozen snapshot so the result does not depend on order.
        var frozen = items.Select(i => Find(i.Index)).ToArray();
        var stemUnions = new List<(int A, int B)>();
        foreach (var s in items.Where(i => !i.Blocked && !i.IsFull && i.Core.Count <= 2 && i.Intro.Count == 0))
        {
            if (s.Core.Sum(w => w.Count(char.IsLetter)) < 5 || IsCommonOrGeneric(s.Core, s.Category, s.M.OriginalValue)) continue;
            if (items.Any(t => t != s && frozen[t.Index] == frozen[s.Index] && t.IsFull)) continue; // already joined to a full form
            var longer = items.Where(t => t != s && frozen[t.Index] != frozen[s.Index] && t.Core.Count >= s.Core.Count && Shares(s, t)
                && t.Core.Take(s.Core.Count).SequenceEqual(s.Core)).ToList();
            var roots = longer.Select(t => frozen[t.Index]).Distinct().ToList();
            if (roots.Count != 1) continue;
            var members = longer.Where(t => frozen[t.Index] == roots[0]).ToList();
            if (members.Any(t => t.Blocked || t.Category != s.Category)) continue;
            stemUnions.Add((s.Index, members[0].Index));
        }
        foreach (var (a, b) in stemUnions) Union(a, b);

        AssignTokens(items, Find, mappings);
    }

    // ---- token assignment ------------------------------------------------------------------------

    private static void AssignTokens(List<Item> items, Func<int, int> find, List<Mapping> mappings)
    {
        var reserved = new HashSet<string>(items.Where(i => i.Blocked).Select(i => i.M.Token ?? "")
            .Concat(mappings.Where(m => m.IsManual || m.Ignored || m.RequiresContextReview || FragmentGuard.IsFragment(m)
                || !Regex.IsMatch(m.Token ?? "", "^\\[" + Regex.Escape(m.Category) + "_\\d+\\]$"))
                .Select(m => m.Token ?? "")).Where(t => t.Length > 0), StringComparer.Ordinal);
        var existingTokens = new HashSet<string>(mappings.Select(m => m.Token ?? "").Where(t => t.Length > 0), StringComparer.Ordinal);
        foreach (var category in new[] { "ORGANIZATION", "VENDOR" })
        {
            var inCategory = items.Where(i => i.Category == category).ToList();
            if (inCategory.Count == 0) continue;
            var numbers = inCategory.Select(i => Regex.Match(i.M.Token ?? "", "_(\\d+)\\]$"))
                .Where(m => m.Success).Select(m => int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? n : 0);
            long next = (long)numbers.DefaultIfEmpty(0).Max() + 1;
            if (next > int.MaxValue) next = 1;
            // Protected mappings are not automatic identity decisions. Preserve their
            // tokens verbatim and reserve those tokens before assigning automatic groups.
            // Otherwise a newly inserted earlier group can steal a manual/Keep token.
            var owned = new HashSet<string>(reserved, StringComparer.Ordinal);
            string AllocateToken()
            {
                // Existing projects can contain the maximum token number. Wrap to
                // a free positive slot, never emit a value Models cannot parse.
                for (var attempts = 0; attempts <= owned.Count + existingTokens.Count; attempts++)
                {
                    var value = "[" + category + "_" + next.ToString("000") + "]";
                    next = next == int.MaxValue ? 1 : next + 1;
                    if (!owned.Contains(value) && !existingTokens.Contains(value)) return value;
                }
                throw new InvalidOperationException("No replacement token is available.");
            }
            var groups = inCategory.Where(i => !i.Blocked).GroupBy(i => find(i.Index)).Select(g => g.ToList())
                .OrderBy(g => g.Min(i => i.M.CreatedAt)).ThenBy(g => g.Min(i => i.Index)).ToList();
            foreach (var group in groups)
            {
                var ordered = group.OrderByDescending(i => i.IsFull && !i.Blocked).ThenBy(i => i.M.CreatedAt).ThenBy(i => i.Index).ToList();
                var token = ordered.Select(i => i.M.Token ?? "").FirstOrDefault(t => t.Length > 0 && !owned.Contains(t));
                if (string.IsNullOrEmpty(token)) token = AllocateToken();
                owned.Add(token);
                foreach (var item in group) item.M.Token = token;
            }
        }
    }
}

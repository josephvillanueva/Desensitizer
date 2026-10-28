using System.Text.RegularExpressions;

namespace SyntheticBenchmark;

public sealed class Metrics
{
    public int Truths { get; set; }
    public int TruthsMatched { get; set; }
    public int Findings { get; set; }
    public int FindingsMatched { get; set; }
    public double Precision => Findings == 0 ? 0 : (double)FindingsMatched / Findings;
    public double Recall => Truths == 0 ? 0 : (double)TruthsMatched / Truths;
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
}

public sealed class FpItem { public string Doc { get; set; } = ""; public string Value { get; set; } = ""; public string Category { get; set; } = ""; public string Reason { get; set; } = ""; }
public sealed class MissItem { public string Doc { get; set; } = ""; public string Value { get; set; } = ""; public string Category { get; set; } = ""; public string EntityId { get; set; } = ""; public string AliasKind { get; set; } = ""; public string Reason { get; set; } = ""; }
public sealed class PairRate { public int Pairs { get; set; } public int SameToken { get; set; } public double Rate => Pairs == 0 ? 0 : (double)SameToken / Pairs; }
public sealed class SplitEntity { public string Doc { get; set; } = ""; public string EntityId { get; set; } = ""; public Dictionary<string, string> FormToToken { get; set; } = new(); }
public sealed class SharedToken { public string Doc { get; set; } = ""; public string Token { get; set; } = ""; public List<string> Values { get; set; } = new(); }

public sealed class ConsistencyResult
{
    public bool Evaluated { get; set; }
    public int MultiFormEntities { get; set; }
    public int ConsistentEntities { get; set; }
    public double EntityRate => MultiFormEntities == 0 ? 0 : (double)ConsistentEntities / MultiFormEntities;
    // For each alias kind: how often the alias received the same token as the entity's canonical form.
    public Dictionary<string, PairRate> ByAliasKind { get; set; } = new();
    public int SharedTokenViolations { get; set; }
    public List<SplitEntity> SplitEntities { get; set; } = new();
    public List<SharedToken> SharedTokens { get; set; } = new();
}

public sealed class PreservationResult
{
    public bool Evaluated { get; set; }
    public int Total { get; set; }
    public int Retained { get; set; }
    public double Rate => Total == 0 ? 0 : (double)Retained / Total;
    public Dictionary<string, PairRate> ByKind { get; set; } = new();
    public List<string> Missing { get; set; } = new();
    public int SensitiveTotal { get; set; }
    public int SensitiveRemoved { get; set; }
    public double RemovalRate => SensitiveTotal == 0 ? 0 : (double)SensitiveRemoved / SensitiveTotal;
    public List<string> Leaked { get; set; } = new();
}

public sealed class ScoreResult
{
    public string Scope { get; set; } = "Synthetic benchmark with fictional ground truth. Does not equal real-world accuracy.";
    public int Documents { get; set; }
    public int SensitiveSpans { get; set; }
    public int PreserveSpans { get; set; }
    // strict: exact normalized value and category class. lenient: overlapping span and category class.
    // anyCategory: overlapping span, category ignored.
    public Dictionary<string, Metrics> Overall { get; set; } = new();
    public Dictionary<string, Dictionary<string, Metrics>> PerCategory { get; set; } = new();
    public List<FpItem> FalsePositives { get; set; } = new();
    public List<MissItem> Misses { get; set; } = new();
    public ConsistencyResult Consistency { get; set; } = new();
    public PreservationResult Preservation { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public static class Scorer
{
    static readonly string[] Levels = { "strict", "lenient", "anyCategory" };

    static bool Overlap(string a, string b)
    {
        if (a == b) return true;
        var (s, l) = a.Length <= b.Length ? (a, b) : (b, a);
        return s.Length >= 2 && (double)s.Length / l.Length >= 0.5 && Text.ContainsWord(l, s);
    }

    static bool Rel(string tn, string tl, string fn, string fl, int level) => level switch
    {
        0 => tn == fn && tl == fl,
        1 => tl == fl && Overlap(tn, fn),
        _ => Overlap(tn, fn),
    };

    // protectedText: docId -> protected document text, or null when unavailable.
    public static ScoreResult Score(TruthSet truth, FindingsSet findings, Func<string, string?> protectedText)
    {
        var res = new ScoreResult { Documents = truth.Documents.Count };
        var tallies = new Dictionary<string, Metrics>(); // key: category|level ; "*" = overall
        Metrics Tally(string cat, string level) { var k = cat + "|" + level; if (!tallies.TryGetValue(k, out var m)) tallies[k] = m = new Metrics(); return m; }
        var anyTokens = findings.Documents.SelectMany(d => d.Findings).Any(f => !string.IsNullOrEmpty(f.Token));
        var cons = res.Consistency; cons.Evaluated = anyTokens;
        var pres = res.Preservation;
        var byId = findings.Documents.ToDictionary(d => d.Id, d => d);

        foreach (var doc in truth.Documents)
        {
            res.SensitiveSpans += doc.Sensitive.Count; res.PreserveSpans += doc.Preserve.Count;
            if (!byId.TryGetValue(doc.Id, out var df)) { res.Warnings.Add($"No findings supplied for document {doc.Id}; treated as empty."); df = new DocFindings { Id = doc.Id }; }
            var T = doc.Sensitive;
            var tn = T.Select(s => Text.Norm(s.Value)).ToArray();
            var tl = T.Select(s => Text.Label(s.Category)).ToArray();
            // Deduplicate findings by normalized value and category class.
            var F = new List<Finding>(); var seen = new HashSet<string>();
            foreach (var f in df.Findings) if (!string.IsNullOrWhiteSpace(f.Value) && seen.Add(Text.Norm(f.Value) + "|" + Text.Label(f.Category))) F.Add(f);
            var fnm = F.Select(f => Text.Norm(f.Value)).ToArray();
            var fl = F.Select(f => Text.Label(f.Category)).ToArray();
            var neutral = doc.Neutral.Select(Text.Norm).ToArray();
            var preserve = doc.Preserve.Select(p => (n: Text.Norm(p.Value), kind: p.Kind)).ToArray();

            // Findings that are purely neutral (and match no truth) are ignored.
            var ignored = new bool[F.Count];
            for (var j = 0; j < F.Count; j++)
            {
                var matchesTruth = Enumerable.Range(0, T.Count).Any(i => Rel(tn[i], tl[i], fnm[j], fl[j], 2));
                if (!matchesTruth && neutral.Any(n => n == fnm[j] || Text.ContainsWord(n, fnm[j]) || Text.ContainsWord(fnm[j], n))) ignored[j] = true;
            }

            for (var level = 0; level < 3; level++)
            {
                var tm = new bool[T.Count]; var fm = new bool[F.Count];
                for (var i = 0; i < T.Count; i++)
                    for (var j = 0; j < F.Count; j++)
                        if (!ignored[j] && Rel(tn[i], tl[i], fnm[j], fl[j], level)) { tm[i] = true; fm[j] = true; }
                var name = Levels[level];
                for (var i = 0; i < T.Count; i++)
                {
                    foreach (var key in new[] { tl[i], "*" }) { var m = Tally(key, name); m.Truths++; if (tm[i]) m.TruthsMatched++; }
                }
                for (var j = 0; j < F.Count; j++)
                {
                    if (ignored[j]) continue;
                    foreach (var key in new[] { fl[j], "*" }) { var m = Tally(key, name); m.Findings++; if (fm[j]) m.FindingsMatched++; }
                }
            }

            // Strict diagnostics: classify every strict miss and strict false positive.
            var strictTruth = new bool[T.Count]; var strictFind = new bool[F.Count];
            var tokenOf = new string?[T.Count];
            for (var i = 0; i < T.Count; i++)
            {
                for (var j = 0; j < F.Count; j++)
                    if (!ignored[j] && Rel(tn[i], tl[i], fnm[j], fl[j], 0)) { strictTruth[i] = true; strictFind[j] = true; tokenOf[i] = F[j].Token; }
                if (!strictTruth[i])
                {
                    for (var j = 0; j < F.Count; j++)
                        if (!ignored[j] && Rel(tn[i], tl[i], fnm[j], fl[j], 1)) { tokenOf[i] = F[j].Token; break; }
                }
            }
            for (var i = 0; i < T.Count; i++)
            {
                if (strictTruth[i]) continue;
                var same = Enumerable.Range(0, F.Count).FirstOrDefault(j => !ignored[j] && fnm[j] == tn[i] && fl[j] != tl[i], -1);
                var ov = Enumerable.Range(0, F.Count).FirstOrDefault(j => !ignored[j] && Rel(tn[i], tl[i], fnm[j], fl[j], 1), -1);
                var any = Enumerable.Range(0, F.Count).FirstOrDefault(j => !ignored[j] && Rel(tn[i], tl[i], fnm[j], fl[j], 2), -1);
                var reason = same >= 0 ? $"wrong category (found as {F[same].Category})"
                    : ov >= 0 ? $"boundary (found '{F[ov].Value}')"
                    : any >= 0 ? $"boundary and category (found '{F[any].Value}' as {F[any].Category})" : "not detected";
                res.Misses.Add(new MissItem { Doc = doc.Id, Value = T[i].Value, Category = T[i].Category, EntityId = T[i].EntityId, AliasKind = T[i].AliasKind, Reason = reason });
            }
            for (var j = 0; j < F.Count; j++)
            {
                if (ignored[j] || strictFind[j]) continue;
                var ti = Enumerable.Range(0, T.Count).FirstOrDefault(i => Rel(tn[i], tl[i], fnm[j], fl[j], 2), -1);
                string reason;
                if (ti >= 0) reason = tn[ti] == fnm[j] ? $"wrong category (truth {T[ti].Category})" : $"boundary (truth '{T[ti].Value}')";
                else
                {
                    var pv = preserve.FirstOrDefault(p => p.n == fnm[j] || Text.ContainsWord(p.n, fnm[j]) || Text.ContainsWord(fnm[j], p.n));
                    reason = pv.n != null ? $"decoy:{pv.kind}" : "unlisted";
                }
                res.FalsePositives.Add(new FpItem { Doc = doc.Id, Value = F[j].Value, Category = F[j].Category, Reason = reason });
            }

            // Token consistency.
            if (anyTokens)
            {
                var groups = Enumerable.Range(0, T.Count).Where(i => tokenOf[i] != null).GroupBy(i => T[i].EntityId + "|" + tl[i]);
                foreach (var g in groups)
                {
                    var members = g.ToList();
                    if (members.Count < 2) continue;
                    cons.MultiFormEntities++;
                    if (members.Select(i => tokenOf[i]).Distinct().Count() == 1) cons.ConsistentEntities++;
                    else cons.SplitEntities.Add(new SplitEntity { Doc = doc.Id, EntityId = g.Key, FormToToken = members.ToDictionary(i => T[i].Value, i => tokenOf[i]!) });
                    var canon = members.FirstOrDefault(i => T[i].AliasKind is "canonical" or "full" or "street");
                    if (canon >= 0 && members.Contains(canon))
                        foreach (var i in members.Where(i => i != canon))
                        {
                            if (!cons.ByAliasKind.TryGetValue(T[i].AliasKind, out var pr)) cons.ByAliasKind[T[i].AliasKind] = pr = new PairRate();
                            pr.Pairs++; if (tokenOf[i] == tokenOf[canon]) pr.SameToken++;
                        }
                }
                foreach (var g in Enumerable.Range(0, T.Count).Where(i => tokenOf[i] != null).GroupBy(i => tokenOf[i]!))
                {
                    var ents = g.Select(i => T[i].EntityId).Distinct().ToList();
                    if (ents.Count > 1) { cons.SharedTokenViolations++; cons.SharedTokens.Add(new SharedToken { Doc = doc.Id, Token = g.Key, Values = g.Select(i => T[i].Value).ToList() }); }
                }
            }

            // Preservation and residual leakage in the protected text.
            var prot = protectedText(doc.Id);
            if (prot != null)
            {
                pres.Evaluated = true;
                var pn = Text.Norm(prot);
                foreach (var p in doc.Preserve)
                {
                    pres.Total++;
                    if (!pres.ByKind.TryGetValue(p.Kind, out var pr)) pres.ByKind[p.Kind] = pr = new PairRate();
                    pr.Pairs++;
                    if (pn.Contains(Text.Norm(p.Value))) { pres.Retained++; pr.SameToken++; } else pres.Missing.Add($"{doc.Id}: {p.Value} [{p.Kind}]");
                }
                foreach (var s in doc.Sensitive)
                {
                    pres.SensitiveTotal++;
                    if (Text.ContainsWord(pn, Text.Norm(s.Value))) pres.Leaked.Add($"{doc.Id}: {s.Value} [{s.Category}]"); else pres.SensitiveRemoved++;
                }
            }
        }

        foreach (var kv in tallies)
        {
            var parts = kv.Key.Split('|'); var cat = parts[0]; var level = parts[1];
            if (cat == "*") res.Overall[level] = kv.Value;
            else { if (!res.PerCategory.TryGetValue(cat, out var d)) res.PerCategory[cat] = d = new(); d[level] = kv.Value; }
        }
        return res;
    }

    // Minimal reader for .docx or .txt output of any engine. Paragraphs are joined with newlines.
    public static string ReadProtected(string path)
    {
        if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return File.ReadAllText(path);
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var sb = new System.Text.StringBuilder();
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("word/") && e.FullName.EndsWith(".xml") && !e.FullName.Contains("/_rels/") && Regex.IsMatch(e.FullName, @"^word/(document|header\d*|footer\d*|footnotes|endnotes)\.xml$")))
        {
            using var s = e.Open();
            var x = System.Xml.Linq.XDocument.Load(s);
            System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            foreach (var p in x.Descendants(w + "p"))
            {
                foreach (var n in p.Descendants())
                {
                    if (n.Name == w + "t") sb.Append(n.Value);
                    else if (n.Name == w + "tab") sb.Append('\t');
                }
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }
}

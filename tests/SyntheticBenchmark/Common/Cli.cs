using System.Globalization;
using System.Text;

namespace SyntheticBenchmark;

public static class Cli
{
    public static string? Opt(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++) if (args[i] == "--" + name) return args[i + 1];
        return null;
    }
    public static bool Flag(string[] args, string name) => args.Contains("--" + name);

    public static int[] Seeds(string[] args) =>
        (Opt(args, "seeds") ?? Opt(args, "seed") ?? "1").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

    // Entities per seed. Each document holds at most 20 entities (about half people).
    public static TruthSet GenerateAll(int[] seeds, int entitiesPerSeed, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var set = new TruthSet();
        foreach (var seed in seeds)
        {
            var remaining = entitiesPerSeed; var index = 0;
            while (remaining > 0)
            {
                var n = Math.Min(20, remaining);
                if (remaining - n is > 0 and < 6) n = remaining; // avoid a tiny trailing document
                if (n < 6) n = Math.Min(6, Math.Max(n, 6));
                var doc = Generator.Generate(seed, index++, n);
                DocxWriter.Write(doc, Path.Combine(outDir, doc.Truth.File), seed);
                set.Documents.Add(doc.Truth);
                remaining -= n;
            }
        }
        File.WriteAllText(Path.Combine(outDir, "truth.json"), Json.Write(set));
        return set;
    }

    public static int Generate(string[] args)
    {
        var seeds = Seeds(args);
        var entities = int.Parse(Opt(args, "entities") ?? "60", CultureInfo.InvariantCulture);
        var outDir = Path.GetFullPath(Opt(args, "out") ?? "synthetic-docs");
        var set = GenerateAll(seeds, entities, outDir);
        Console.WriteLine($"Generated {set.Documents.Count} documents, {set.Documents.Sum(d => d.Sensitive.Count)} sensitive spans, {set.Documents.Sum(d => d.Preserve.Count)} preserve spans into {outDir}");
        return 0;
    }

    // score --truth truth.json --findings findings.json [--protected-dir dir] [--out dir]
    // The protected directory holds <docId>.docx or <docId>.txt, the engine's output for each document.
    public static int Score(string[] args)
    {
        var truthPath = Opt(args, "truth") ?? throw new ArgumentException("--truth is required");
        var findingsPath = Opt(args, "findings") ?? throw new ArgumentException("--findings is required");
        var protectedDir = Opt(args, "protected-dir");
        var outDir = Opt(args, "out");
        var truth = Json.Read<TruthSet>(truthPath);
        var findings = Json.Read<FindingsSet>(findingsPath);
        var result = Scorer.Score(truth, findings, id => ReadProtected(protectedDir, id));
        Console.WriteLine(Report.Markdown(result, "Synthetic benchmark score"));
        if (outDir != null)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "score.json"), Json.Write(result));
            File.WriteAllText(Path.Combine(outDir, "score.md"), Report.Markdown(result, "Synthetic benchmark score"));
        }
        return 0;
    }

    // Sanity checks for the generator and scorer themselves, using an oracle engine and deliberately broken engines.
    public static int SelfTest()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        var a = Generator.Generate(7, 0, 20); var b = Generator.Generate(7, 0, 20);
        Check("Generator is deterministic", Json.Write(a.Truth) == Json.Write(b.Truth) && Generator.Flatten(a.Blocks).SequenceEqual(Generator.Flatten(b.Blocks)));
        Check("Different seed changes the document", Json.Write(Generator.Generate(8, 0, 20).Truth) != Json.Write(a.Truth));
        var truth = new TruthSet { Documents = { a.Truth } };
        var text = string.Join("\n", Generator.Flatten(a.Blocks));
        Check("Every must-preserve span is in the source text", a.Truth.Preserve.All(p => Text.Norm(text).Contains(Text.Norm(p.Value))));
        Check("No must-preserve span contains a sensitive value", a.Truth.Preserve.All(p => !a.Truth.Sensitive.Any(s => Text.Norm(p.Value).Contains(Text.Norm(s.Value)))));

        string Protect(Func<SensitiveSpan, string> token)
        {
            var t = text;
            foreach (var s in a.Truth.Sensitive.OrderByDescending(s => s.Value.Length))
                t = System.Text.RegularExpressions.Regex.Replace(t, "(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(s.Value) + "(?![A-Za-z0-9])", token(s));
            return t;
        }
        FindingsSet Engine(Func<SensitiveSpan, string> token) => new()
        {
            Documents = { new DocFindings { Id = a.Truth.Id, Findings = a.Truth.Sensitive.Select(s => new Finding { Value = s.Value, Category = s.Category, Token = token(s) }).ToList() } },
        };
        var oracleTokens = a.Truth.Sensitive.Select(s => s.EntityId).Distinct().Select((e, i) => (e, i)).ToDictionary(x => x.e, x => x.i);
        string Oracle(SensitiveSpan s) => $"[{Text.Label(s.Category)}_{oracleTokens[s.EntityId]:000}]";
        var good = Scorer.Score(truth, Engine(Oracle), _ => Protect(Oracle));
        Check("Oracle scores 100% strict precision and recall", good.Overall["strict"].Precision == 1 && good.Overall["strict"].Recall == 1);
        Check("Oracle has fully consistent tokens", good.Consistency.ConsistentEntities == good.Consistency.MultiFormEntities && good.Consistency.SharedTokenViolations == 0 && good.Consistency.MultiFormEntities > 0);
        Check("Oracle preserves everything and removes everything", good.Preservation.Retained == good.Preservation.Total && good.Preservation.SensitiveRemoved == good.Preservation.SensitiveTotal);

        var split = Scorer.Score(truth, Engine(s => $"[{Text.Label(s.Category)}_{s.Value.GetHashCode(StringComparison.Ordinal):x}]"), _ => text);
        Check("One token per form is reported as split entities", split.Consistency.ConsistentEntities < split.Consistency.MultiFormEntities);
        var merged = Scorer.Score(truth, Engine(s => "[X_001]"), _ => text);
        Check("A single shared token is reported as a cross-entity violation", merged.Consistency.SharedTokenViolations > 0);
        var half = new FindingsSet { Documents = { new DocFindings { Id = a.Truth.Id, Findings = a.Truth.Sensitive.Take(a.Truth.Sensitive.Count / 2).Select(s => new Finding { Value = s.Value, Category = s.Category }).ToList() } } };
        var halfScore = Scorer.Score(truth, half, _ => null);
        Check("Half the findings gives about half recall and full precision", halfScore.Overall["strict"].Precision == 1 && Math.Abs(halfScore.Overall["strict"].Recall - 0.5) < 0.02);
        var decoy = a.Truth.Preserve.First();
        var withDecoy = Engine(Oracle); withDecoy.Documents[0].Findings.Add(new Finding { Value = decoy.Value, Category = "PERSON" });
        var decoyScore = Scorer.Score(truth, withDecoy, _ => null);
        Check("A finding on a decoy is a false positive tagged as decoy", decoyScore.FalsePositives.Any(f => f.Reason.StartsWith("decoy:")) && decoyScore.Overall["strict"].Precision < 1);
        var damaged = Scorer.Score(truth, Engine(Oracle), _ => Protect(Oracle).Replace(decoy.Value, "[REMOVED]"));
        Check("Removing a must-preserve span lowers preservation", damaged.Preservation.Retained < damaged.Preservation.Total);
        Console.WriteLine(failures == 0 ? "Self-test passed." : $"{failures} self-test check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    public static string? ReadProtected(string? dir, string id)
    {
        if (dir == null) return null;
        foreach (var ext in new[] { ".docx", ".txt" })
        {
            var p = Path.Combine(dir, id + ext);
            if (File.Exists(p)) return Scorer.ReadProtected(p);
        }
        return null;
    }

    // Results go to a new timestamped folder; an existing folder is never reused.
    public static string NewResultsFolder(string root, string label)
    {
        Directory.CreateDirectory(root);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(root, $"{stamp}-{label}");
        for (var i = 2; Directory.Exists(path); i++) path = Path.Combine(root, $"{stamp}-{label}-{i}");
        Directory.CreateDirectory(path);
        return path;
    }
}

public static class Report
{
    static string Pct(double v) => (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static string Markdown(ScoreResult r, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine("Synthetic benchmark with fictional ground truth. These numbers are not real-world accuracy.");
        sb.AppendLine();
        sb.AppendLine($"Documents: {r.Documents}. Sensitive spans: {r.SensitiveSpans}. Must-preserve spans: {r.PreserveSpans}.");
        sb.AppendLine();
        sb.AppendLine("## Detection (exact normalized span and category class = strict)");
        sb.AppendLine();
        sb.AppendLine("| Level | Precision | Recall | F1 | TP (truths) | Findings | Truths |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var level in new[] { "strict", "lenient", "anyCategory" })
            if (r.Overall.TryGetValue(level, out var m))
                sb.AppendLine($"| {level} | {Pct(m.Precision)} | {Pct(m.Recall)} | {Pct(m.F1)} | {m.TruthsMatched} | {m.Findings} | {m.Truths} |");
        sb.AppendLine();
        sb.AppendLine("Lenient accepts a finding that overlaps a truth span (one contains the other, length ratio at least 0.5) in the same category class. anyCategory also ignores the category.");
        sb.AppendLine();
        sb.AppendLine("## Per category (strict, then lenient)");
        sb.AppendLine();
        sb.AppendLine("| Category | Strict P | Strict R | Strict F1 | Lenient P | Lenient R | Truths | Findings |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var kv in r.PerCategory.OrderBy(k => k.Key))
        {
            kv.Value.TryGetValue("strict", out var s); kv.Value.TryGetValue("lenient", out var l);
            s ??= new Metrics(); l ??= new Metrics();
            sb.AppendLine($"| {kv.Key} | {Pct(s.Precision)} | {Pct(s.Recall)} | {Pct(s.F1)} | {Pct(l.Precision)} | {Pct(l.Recall)} | {s.Truths} | {s.Findings} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Token consistency");
        sb.AppendLine();
        var c = r.Consistency;
        if (!c.Evaluated) sb.AppendLine("Not evaluated (findings carried no tokens).");
        else
        {
            sb.AppendLine($"Entities with two or more detected forms: {c.MultiFormEntities}. All forms share one token: {c.ConsistentEntities} ({Pct(c.EntityRate)}).");
            sb.AppendLine($"Different entities sharing one token (must be 0): {c.SharedTokenViolations}.");
            sb.AppendLine();
            sb.AppendLine("| Alias kind (vs canonical form) | Pairs | Same token | Rate |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var kv in c.ByAliasKind.OrderBy(k => k.Key)) sb.AppendLine($"| {kv.Key} | {kv.Value.Pairs} | {kv.Value.SameToken} | {Pct(kv.Value.Rate)} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Business-content preservation and residual leakage");
        sb.AppendLine();
        var p = r.Preservation;
        if (!p.Evaluated) sb.AppendLine("Not evaluated (no protected output supplied).");
        else
        {
            sb.AppendLine($"Must-preserve spans still present: {p.Retained} of {p.Total} ({Pct(p.Rate)}).");
            sb.AppendLine($"Sensitive spans no longer present in the protected output: {p.SensitiveRemoved} of {p.SensitiveTotal} ({Pct(p.RemovalRate)}).");
            sb.AppendLine();
            sb.AppendLine("| Preserve kind | Spans | Retained | Rate |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var kv in p.ByKind.OrderBy(k => k.Key)) sb.AppendLine($"| {kv.Key} | {kv.Value.Pairs} | {kv.Value.SameToken} | {Pct(kv.Value.Rate)} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Strict false positives by reason");
        sb.AppendLine();
        foreach (var g in r.FalsePositives.GroupBy(f => f.Reason.Split(' ', '(')[0]).OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"- {g.Key}: {g.Count()}");
            foreach (var v in g.GroupBy(f => f.Value + " [" + f.Category + "] " + f.Reason).OrderByDescending(x => x.Count()).Take(8))
                sb.AppendLine($"    - {v.Key} x{v.Count()}");
        }
        sb.AppendLine();
        sb.AppendLine("## Strict misses by category and reason");
        sb.AppendLine();
        foreach (var g in r.Misses.GroupBy(m => m.Category + " / " + m.Reason.Split(' ', '(')[0] + " / " + m.AliasKind).OrderByDescending(g => g.Count()).Take(25))
            sb.AppendLine($"- {g.Key}: {g.Count()} (e.g. {string.Join("; ", g.Select(x => x.Value).Distinct().Take(3))})");
        if (r.Warnings.Count > 0) { sb.AppendLine(); sb.AppendLine("## Warnings"); foreach (var w in r.Warnings) sb.AppendLine("- " + w); }
        return sb.ToString();
    }
}

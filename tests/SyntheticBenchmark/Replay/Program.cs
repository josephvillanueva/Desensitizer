using System.Diagnostics;
using System.Text;
using Desensitizer;
using SyntheticBenchmark;

// Offline replay harness (stage S14). Feeds the SAVED per-stage candidates of a live Full-mode benchmark run through the
// CURRENT post-processing code and scores the result exactly as the benchmark runner does. No worker, no model, no network.
//
//   replay  --results DIR[,DIR...] [--out DIR] [--label NAME]     replay one or more saved full-* result folders
//   compare A/summary.json B/summary.json                          per-category deltas between two replays
//
// What is replayed: FindingPostProcessor.Complete over (DetectorStage + ReviewStage) as saved in full-details.json, then
// mapping creation (all approved, like the benchmark), ConsolidatePersonAliases (person and company identity) and the
// real Word export, then Scorer.Score. What is NOT replayed: the workers and reviewers themselves, LocalAiReview's own
// filtering and AiConsensus.Resolve (the saved review stage is already the consensus output; the raw per-model lists were not
// saved). Saved candidates carry value, category, source, detection sources and confidence only. In the AI build the fields
// ConsensusVotes, RecommendedReplace, RequiresContextReview and EntityKey are reconstructed (see Reconstruct below).
// Synthetic fictional documents only: the numbers are not real-world accuracy.
#if AI_APP
const string AppName = "AI";
#else
const string AppName = "Regular";
#endif

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage:\n  replay --results DIR[,DIR...] [--out DIR] [--label NAME]\n  compare A/summary.json B/summary.json");
    return 0;
}
switch (args[0])
{
    case "replay": return Replay(args);
    case "compare": return Compare(args[1], args[2]);
    default: throw new ArgumentException("Unknown command " + args[0]);
}

static Candidate Revive(StageCandidate c) => new()
{
    Value = c.Value, Category = c.Category, Source = c.Source, Confidence = c.Confidence,
    DetectionSources = c.DetectionSources.ToList(),
    Explanation = "Replayed from saved candidate",
};

#if AI_APP
// The saved review stage is the output of AiConsensus.Resolve. Rebuild the fields the saved JSON does not hold so that the
// current mapping, token and review-flag logic sees the same shape it saw live. This is an approximation:
//  - ConsensusVotes: number of AI models among the saved detection sources for consensus rows, 1 for single-reviewer rows.
//  - RecommendedReplace: same rule as AiConsensus (two votes, or one vote plus a detector for a direct identifier).
//  - EntityKey: category plus the longest member of the alias cluster (same-category rows with equal detection sources that
//    are aliases of each other), which is how AiConsensus builds it; entity-graph rows share their cluster's key.
//  - RequiresContextReview: the two ReviewDisagreement.Mark passes AiConsensus ran over reviewer plus detector findings.
static void Reconstruct(string document, List<Candidate> detectors, List<Candidate> review)
{
    // REPLAY_RECON (comma list of votes,key,mark) restricts which reconstructions run; default all. Used to see which
    // missing field explains a difference from the live run.
    var enabled = (Environment.GetEnvironmentVariable("REPLAY_RECON") ?? "votes,key,mark").Split(',');
    var models = new[] { "AI Qwen", "AI NuExtract", "AI Gemma" };
    bool IsAi(string s) => models.Contains(s, StringComparer.OrdinalIgnoreCase);
    static bool Direct(string c) => c.ToUpperInvariant() is "PERSON" or "ADDRESS" or "EMAIL" or "PHONE" or "URL" or "IDENTIFIER";
    if (enabled.Contains("votes")) foreach (var c in review)
    {
        var aiCount = c.DetectionSources.Count(IsAi);
        var detectorSupport = c.DetectionSources.Any(s => !IsAi(s) && !s.StartsWith("AI ", StringComparison.OrdinalIgnoreCase));
        c.ConsensusVotes = c.Source is "AI consensus" or "AI entity graph" ? Math.Max(aiCount, 2) : 1;
        if (c.Source == "AI entity graph") c.ConsensusVotes = Math.Max(aiCount, 1);
        c.RecommendedReplace = c.ConsensusVotes >= 2 || (c.ConsensusVotes >= 1 && detectorSupport && Direct(c.Category));
    }
    var clusters = new List<List<Candidate>>();
    foreach (var c in review.Where(x => x.Source != "AI entity graph"))
    {
        var key = string.Join("|", c.DetectionSources.OrderBy(x => x, StringComparer.Ordinal));
        var cluster = clusters.FirstOrDefault(k => k[0].Category.Equals(c.Category, StringComparison.OrdinalIgnoreCase) && string.Join("|", k[0].DetectionSources.OrderBy(x => x, StringComparer.Ordinal)) == key && k.All(m => AiConsensus.AreAliases(c.Category, c.Value, m.Value)));
        if (cluster == null) clusters.Add(cluster = new List<Candidate>());
        cluster.Add(c);
    }
    if (enabled.Contains("key")) foreach (var cluster in clusters)
    {
        var rep = cluster.OrderByDescending(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length).ThenByDescending(x => x.Value.Length).First();
        var entityKey = rep.Category.ToUpperInvariant() + "|" + Project.Normalize(rep.Value);
        foreach (var m in cluster) m.EntityKey = entityKey;
    }
    if (enabled.Contains("key")) foreach (var g in review.Where(x => x.Source == "AI entity graph"))
    {
        var home = clusters.FirstOrDefault(k => k[0].Category.Equals(g.Category, StringComparison.OrdinalIgnoreCase) && k.Any(m => AiConsensus.AreAliases(g.Category, g.Value, m.Value)));
        if (home != null) g.EntityKey = home[0].EntityKey;
    }
    if (!enabled.Contains("mark")) return;
    var eligibleDetectors = detectors.Where(d => !LocalAiReview.IsClearlyGenericCandidate(d.Value, d.Category)).ToList();
    var resolved = review.Where(x => x.Source != "AI entity graph").ToList();
    // Mirror AiConsensus.Resolve as it stands in the source under test: category resolution (when present) runs before Mark.
    var surviving = new HashSet<Candidate>(ReviewNoiseCategory.Resolve(document, resolved.Concat(eligibleDetectors), true), ReferenceEqualityComparer.Instance);
    resolved.RemoveAll(x => !surviving.Contains(x));
    review.RemoveAll(x => x.Source != "AI entity graph" && !surviving.Contains(x));
    eligibleDetectors.RemoveAll(x => !surviving.Contains(x));
    foreach (var reversed in ReviewNoise.ReversedNames(document, resolved.Concat(eligibleDetectors).ToList()))
        if (!resolved.Concat(eligibleDetectors).Any(x => Project.Normalize(x.Value) == Project.Normalize(reversed)))
        {
            var add = new Candidate { Value = reversed, Category = "PERSON", Confidence = 0.9, Source = "Reversed name", DetectionSources = new List<string> { "Reversed name" }, EntityKey = "PERSON|" + Project.Normalize(reversed) };
            resolved.Add(add); review.Add(add);
        }
    var covered = new HashSet<Candidate>(ReviewNoise.DropRedundantFragments(document, resolved.Concat(eligibleDetectors).ToList()), ReferenceEqualityComparer.Instance);
    resolved.RemoveAll(x => !covered.Contains(x));
    review.RemoveAll(x => x.Source != "AI entity graph" && !covered.Contains(x));
    eligibleDetectors.RemoveAll(x => !covered.Contains(x));
    ReviewDisagreement.Mark(document, resolved.Concat(eligibleDetectors));
    foreach (var c in resolved.Where(x => x.RequiresContextReview)) c.RecommendedReplace = false;
    ReviewDisagreement.Mark(document, review);
    foreach (var c in review.Where(x => x.RequiresContextReview)) c.RecommendedReplace = false;
}
#endif

static int Replay(string[] args)
{
    var folders = (Cli.Opt(args, "results") ?? throw new ArgumentException("--results is required")).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFullPath).ToList();
    var label = Cli.Opt(args, "label") ?? "replay";
    var outRoot = Path.GetFullPath(Cli.Opt(args, "out") ?? Path.Combine(Directory.GetCurrentDirectory(), "work", "Eval-S14", "replays"));
    var outDir = Cli.NewResultsFolder(outRoot, $"{label}-{AppName}");
    var protDir = Path.Combine(outDir, "protected"); Directory.CreateDirectory(protDir);
    var timer = Stopwatch.StartNew();

    var truth = new TruthSet(); var findings = new FindingsSet();
    var protectedText = new Dictionary<string, string>();
    var notes = new List<string>(); var failures = new List<string>();
    var finalDiffs = new List<string>();
    var flagged = new List<string>(); var total = 0;
    int savedFinalTotal = 0, replayFinalTotal = 0, identical = 0;

    foreach (var folder in folders)
    {
        var folderTruth = Json.Read<TruthSet>(Path.Combine(folder, "docs", "truth.json"));
        var stages = Json.Read<List<StageDoc>>(Path.Combine(folder, "full-details.json"));
        foreach (var doc in folderTruth.Documents)
        {
            var stage = stages.FirstOrDefault(s => s.Doc == doc.Id);
            if (stage == null) { failures.Add($"{doc.Id}: no saved stage in {folder}"); continue; }
            truth.Documents.Add(doc);
            var src = Path.Combine(folder, "docs", doc.File);
            var text = DocumentProcessor.ReadText(src);
            var detectors = stage.DetectorStage.Select(Revive).ToList();
            var review = stage.ReviewStage.Select(Revive).ToList();
#if AI_APP
            Reconstruct(text, detectors, review);
#endif
            var cands = FindingPostProcessor.Complete(text, detectors.Concat(review));

            // Reproduction check: the saved Final list is what the code of that day returned.
            var savedKeys = stage.Final.Select(c => c.Category.ToUpperInvariant() + "|" + Project.Normalize(c.Value)).ToHashSet();
            var newKeys = cands.Select(c => c.Category.ToUpperInvariant() + "|" + Project.Normalize(c.Value)).ToHashSet();
            savedFinalTotal += savedKeys.Count; replayFinalTotal += newKeys.Count; identical += savedKeys.Intersect(newKeys).Count();
            foreach (var k in savedKeys.Except(newKeys)) finalDiffs.Add($"{doc.Id} only in saved final: {k}");
            foreach (var k in newKeys.Except(savedKeys)) finalDiffs.Add($"{doc.Id} only in replay: {k}");

            total += cands.Count;
            flagged.AddRange(cands.Where(c => c.RequiresContextReview).Select(c => $"{doc.Id} [{c.Category}] {c.Value}"));
            var project = Project.New("Synthetic benchmark replay", "test only");
            var pairs = cands.Select(c => (c, m: project.EnsureMapping(c, "doc", approved: true))).ToList();
            foreach (var m in project.Mappings) m.Approved = true;
            project.ConsolidatePersonAliases();
            var protectedPath = Path.Combine(protDir, doc.Id + ".docx");
            try
            {
                try { DocumentProcessor.Desensitize(src, protectedPath, project.Mappings); }
                catch (InvalidDataException blocked) when (blocked.Message.StartsWith("Export blocked", StringComparison.Ordinal))
                {
                    notes.Add($"{doc.Id}: export gate blocked (re-exported with unapplied replacements allowed): {blocked.Message}");
                    DocumentProcessor.Desensitize(src, protectedPath, project.Mappings, allowUnappliedMappings: true);
                }
                protectedText[doc.Id] = DocumentProcessor.ReadText(protectedPath);
            }
            catch (Exception ex) { failures.Add($"{doc.Id}: export failed: {ex.Message}"); }
            findings.Documents.Add(new DocFindings
            {
                Id = doc.Id,
                Findings = pairs.Select(p => new Finding { Value = p.c.Value, Category = p.c.Category, Token = p.m.Token }).ToList(),
            });
        }
    }

    string? Prot(string id) => protectedText.TryGetValue(id, out var t) ? t : null;
    var all = Scorer.Score(truth, findings, Prot);
    all.Warnings.AddRange(failures); all.Warnings.AddRange(notes);
    File.WriteAllText(Path.Combine(outDir, "score.json"), Json.Write(all));
    File.WriteAllText(Path.Combine(outDir, "score.md"), Report.Markdown(all, $"Replay of saved live candidates through current post-processing, {AppName} app"));
    File.WriteAllText(Path.Combine(outDir, "findings.json"), Json.Write(findings));
    var summary = Summarize(all, AppName, label);
    summary.FlaggedForReview = flagged.Count; summary.FlaggedSamples = flagged.OrderBy(x => x).ToList(); summary.FinalFindings = total;
    File.WriteAllText(Path.Combine(outDir, "summary.json"), Json.Write(summary));
    var sb = new StringBuilder();
    sb.AppendLine($"Final list vs the saved Final list of the live run: saved {savedFinalTotal} unique findings, replay {replayFinalTotal}, identical {identical}.");
    foreach (var d in finalDiffs.Take(80)) sb.AppendLine("  " + d);
    File.WriteAllText(Path.Combine(outDir, "final-diff.txt"), sb.ToString());
    File.WriteAllText(Path.Combine(outDir, "run.json"), Json.Write(new { scope = "Offline replay of saved synthetic-benchmark candidates; not real-world accuracy", app = AppName, folders, label, elapsedSeconds = timer.Elapsed.TotalSeconds }));
    Console.WriteLine(Table(summary));
    Console.Write(sb.ToString().Split('\n').Take(1).First() + "\n");
    Console.WriteLine("Results: " + outDir);
    return failures.Count == 0 ? 0 : 1;
}

static Summary Summarize(ScoreResult r, string app, string label)
{
    var s = new Summary { App = app, Label = label };
    foreach (var lvl in new[] { "strict", "lenient", "anyCategory" })
        if (r.Overall.TryGetValue(lvl, out var m)) s.Overall[lvl] = new Row(m);
    foreach (var kv in r.PerCategory)
    {
        var d = new Dictionary<string, Row>();
        foreach (var lvl in new[] { "strict", "lenient" }) if (kv.Value.TryGetValue(lvl, out var m)) d[lvl] = new Row(m);
        s.PerCategory[kv.Key] = d;
    }
    foreach (var g in r.FalsePositives.GroupBy(f => f.Reason.Split(' ', '(')[0])) s.FalsePositiveReasons[g.Key] = g.Count();
    foreach (var g in r.Misses.GroupBy(m => m.Reason.Split(' ', '(')[0])) s.MissReasons[g.Key] = g.Count();
    s.FalsePositiveSamples = r.FalsePositives.Select(f => $"{f.Doc} [{f.Category}] {f.Value} :: {f.Reason}").OrderBy(x => x).ToList();
    s.MissSamples = r.Misses.Select(m => $"{m.Doc} [{m.Category}] {m.Value} :: {m.Reason} ({m.AliasKind})").OrderBy(x => x).ToList();
    s.PreservedRetained = r.Preservation.Retained; s.PreservedTotal = r.Preservation.Total;
    s.SensitiveRemoved = r.Preservation.SensitiveRemoved; s.SensitiveTotal = r.Preservation.SensitiveTotal;
    foreach (var kv in r.Preservation.ByKind) s.PreservedByKind[kv.Key] = $"{kv.Value.SameToken}/{kv.Value.Pairs}";
    s.MultiFormEntities = r.Consistency.MultiFormEntities; s.ConsistentEntities = r.Consistency.ConsistentEntities; s.SharedTokenViolations = r.Consistency.SharedTokenViolations;
    foreach (var kv in r.Consistency.ByAliasKind) s.AliasKindRates[kv.Key] = $"{kv.Value.SameToken}/{kv.Value.Pairs}";
    s.Leaked = r.Preservation.Leaked.ToList();
    s.PreservedMissing = r.Preservation.Missing.ToList();
    return s;
}

static string Table(Summary s)
{
    var sb = new StringBuilder();
    string P(double v) => (v * 100).ToString("0.0");
    sb.AppendLine($"== {s.App} / {s.Label}");
    sb.AppendLine("Category        truths findings | strict P    R | lenient P    R");
    foreach (var kv in s.PerCategory.OrderBy(k => k.Key))
    {
        kv.Value.TryGetValue("strict", out var st); kv.Value.TryGetValue("lenient", out var le);
        st ??= new Row(); le ??= new Row();
        sb.AppendLine($"{kv.Key,-15} {st.Truths,6} {st.Findings,8} | {P(st.Precision),8} {P(st.Recall),5} | {P(le.Precision),8} {P(le.Recall),5}");
    }
    var o = s.Overall["strict"]; var l = s.Overall["lenient"];
    sb.AppendLine($"{"ALL",-15} {o.Truths,6} {o.Findings,8} | {P(o.Precision),8} {P(o.Recall),5} | {P(l.Precision),8} {P(l.Recall),5}   anyCategory R {P(s.Overall["anyCategory"].Recall)}");
    sb.AppendLine("FP reasons: " + string.Join(", ", s.FalsePositiveReasons.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
    sb.AppendLine("Miss reasons: " + string.Join(", ", s.MissReasons.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
    sb.AppendLine($"Preserved {s.PreservedRetained}/{s.PreservedTotal} ({string.Join(", ", s.PreservedByKind.OrderBy(x => x.Key).Select(x => x.Key + " " + x.Value))})");
    sb.AppendLine($"Sensitive removed {s.SensitiveRemoved}/{s.SensitiveTotal}; leaked: {string.Join(", ", s.Leaked.Take(12))}");
    sb.AppendLine($"Flagged RequiresContextReview {s.FlaggedForReview} of {s.FinalFindings} final findings");
    sb.AppendLine($"Alias consistency {s.ConsistentEntities}/{s.MultiFormEntities}; shared-token violations {s.SharedTokenViolations}; by kind: {string.Join(", ", s.AliasKindRates.OrderBy(x => x.Key).Select(x => x.Key + " " + x.Value))}");
    return sb.ToString();
}

static int Compare(string a, string b)
{
    var x = Json.Read<Summary>(a); var y = Json.Read<Summary>(b);
    string D(double v) => (v >= 0 ? "+" : "") + (v * 100).ToString("0.0");
    Console.WriteLine($"{x.Label} -> {y.Label} ({y.App})");
    Console.WriteLine("Category        strict P (delta)  strict R (delta) | findings");
    foreach (var cat in y.PerCategory.Keys.Union(x.PerCategory.Keys).OrderBy(k => k))
    {
        x.PerCategory.TryGetValue(cat, out var xa); y.PerCategory.TryGetValue(cat, out var ya);
        var xs = xa != null && xa.TryGetValue("strict", out var q) ? q : new Row(); var ys = ya != null && ya.TryGetValue("strict", out var w) ? w : new Row();
        Console.WriteLine($"{cat,-15} {ys.Precision * 100,6:0.0} ({D(ys.Precision - xs.Precision)})  {ys.Recall * 100,6:0.0} ({D(ys.Recall - xs.Recall)}) | {xs.Findings} -> {ys.Findings}");
    }
    var xo = x.Overall["strict"]; var yo = y.Overall["strict"];
    Console.WriteLine($"{"ALL",-15} {yo.Precision * 100,6:0.0} ({D(yo.Precision - xo.Precision)})  {yo.Recall * 100,6:0.0} ({D(yo.Recall - xo.Recall)}) | {xo.Findings} -> {yo.Findings}");
    Console.WriteLine($"preserved {x.PreservedRetained}/{x.PreservedTotal} -> {y.PreservedRetained}/{y.PreservedTotal}; removed {x.SensitiveRemoved} -> {y.SensitiveRemoved}; consistent {x.ConsistentEntities}/{x.MultiFormEntities} -> {y.ConsistentEntities}/{y.MultiFormEntities}; shared-token {x.SharedTokenViolations} -> {y.SharedTokenViolations}");
    foreach (var k in x.FalsePositiveReasons.Keys.Union(y.FalsePositiveReasons.Keys).OrderBy(k => k))
        Console.WriteLine($"  FP {k}: {x.FalsePositiveReasons.GetValueOrDefault(k)} -> {y.FalsePositiveReasons.GetValueOrDefault(k)}");
    Console.WriteLine($"flagged for review {x.FlaggedForReview} -> {y.FlaggedForReview}");
    var fpNew = y.FalsePositiveSamples.Except(x.FalsePositiveSamples).ToList(); var fpGone = x.FalsePositiveSamples.Except(y.FalsePositiveSamples).ToList();
    var msNew = y.MissSamples.Except(x.MissSamples).ToList(); var msGone = x.MissSamples.Except(y.MissSamples).ToList();
    Console.WriteLine($"false positives removed {fpGone.Count}, added {fpNew.Count}; misses fixed {msGone.Count}, new misses {msNew.Count}");
    foreach (var m in msNew) Console.WriteLine("  NEW MISS " + m);
    foreach (var m in fpNew.Take(30)) Console.WriteLine("  NEW FP   " + m);
    return 0;
}

sealed class Row
{
    public int Truths { get; set; } public int TruthsMatched { get; set; } public int Findings { get; set; } public int FindingsMatched { get; set; }
    public double Precision { get; set; } public double Recall { get; set; }
    public Row() { }
    public Row(Metrics m) { Truths = m.Truths; TruthsMatched = m.TruthsMatched; Findings = m.Findings; FindingsMatched = m.FindingsMatched; Precision = m.Precision; Recall = m.Recall; }
}

sealed class Summary
{
    public string App { get; set; } = ""; public string Label { get; set; } = "";
    public Dictionary<string, Row> Overall { get; set; } = new();
    public Dictionary<string, Dictionary<string, Row>> PerCategory { get; set; } = new();
    public Dictionary<string, int> FalsePositiveReasons { get; set; } = new(); public Dictionary<string, int> MissReasons { get; set; } = new();
    public List<string> FalsePositiveSamples { get; set; } = new(); public List<string> MissSamples { get; set; } = new();
    public int PreservedRetained { get; set; } public int PreservedTotal { get; set; } public int SensitiveRemoved { get; set; } public int SensitiveTotal { get; set; }
    public Dictionary<string, string> PreservedByKind { get; set; } = new();
    public int MultiFormEntities { get; set; } public int ConsistentEntities { get; set; } public int SharedTokenViolations { get; set; }
    public Dictionary<string, string> AliasKindRates { get; set; } = new();
    public int FlaggedForReview { get; set; } public int FinalFindings { get; set; } public List<string> FlaggedSamples { get; set; } = new();
    public List<string> Leaked { get; set; } = new(); public List<string> PreservedMissing { get; set; } = new();
}

sealed class StageCandidate
{
    public string Value { get; set; } = ""; public string Category { get; set; } = ""; public string Source { get; set; } = "";
    public List<string> DetectionSources { get; set; } = new(); public double Confidence { get; set; }
}

sealed class StageDoc
{
    public string Doc { get; set; } = ""; public int Chars { get; set; }
    public List<StageCandidate> DetectorStage { get; set; } = new(); public List<StageCandidate> ReviewStage { get; set; } = new(); public List<StageCandidate> Final { get; set; } = new();
}

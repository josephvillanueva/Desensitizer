using System.Diagnostics;
using Desensitizer;
using SyntheticBenchmark;

// Runner linked into both apps (Regular and AI). Commands:
//   run      --mode detector|oracle|full --seeds 1,2,3 --entities 60 [--results DIR]
//   generate --seeds ... --entities ... --out DIR
//   score    --truth ... --findings ... [--protected-dir DIR] [--out DIR]
// detector mode: deterministic layers only (Detector, FindingPostProcessor, DocumentPrivacyPolicy). No model, no LLM.
// oracle mode: the exact ground truth is offered as model output to the shared policy, post-processor and identity layers.
// Full mode: local Presidio/GLiNER/Privacy Filter workers and generative reviewers.
//   It needs the DESENSITIZER_* / AI_DESENSITIZER_* model path variables; use Run.ps1 -Mode Full. Not run by default.
#if AI_APP
const string AppName = "AI";
#else
const string AppName = "Regular";
#endif

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage:\n  run --mode detector|oracle|full --seeds 1,2,3 --entities 60 [--results DIR]\n  generate --seeds 1,2,3 --entities 60 --out DIR\n  score --truth truth.json --findings findings.json [--protected-dir DIR] [--out DIR]");
    return 0;
}
switch (args[0])
{
    case "generate": return Cli.Generate(args);
    case "score": return Cli.Score(args);
    case "selftest": return Cli.SelfTest();
    case "run": return Run(args);
    default: throw new ArgumentException("Unknown command " + args[0]);
}

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null) { if (Directory.Exists(Path.Combine(dir.FullName, "tests", "SyntheticBenchmark"))) return dir.FullName; dir = dir.Parent; }
    dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null) { if (Directory.Exists(Path.Combine(dir.FullName, "tests", "SyntheticBenchmark"))) return dir.FullName; dir = dir.Parent; }
    return null;
}

static string Git(string root, string arguments)
{
    try
    {
        var psi = new ProcessStartInfo("git", arguments) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return o;
    }
    catch { return "unknown"; }
}

static List<Candidate> Detect(string mode, string text, DocTruth truth)
{
    Stage.Last = null;
    if (mode == "oracle")
    {
        // Policy ceiling: hand the shared policy, post-processor and identity layers the exact ground truth as if a perfect
        // model had proposed it. Measures what the deterministic filtering and token assignment do to correct input.
        var perfect = truth.Sensitive.Select(x => new Candidate { Value = x.Value, Category = x.Category, Confidence = 0.95, Source = "Oracle" }).ToList();
        return FindingPostProcessor.Complete(text, perfect);
    }
    if (mode == "detector")
    {
        // Same layers the apps always run first, without any worker process or model.
        var raw = Detector.Find(text).ToList();
        return FindingPostProcessor.Complete(text, raw).Where(PresidioDetector.IsEligibleCandidate).ToList();
    }
    // Full mode: record the stages separately so the report can attribute findings to engines and timings to sections.
    var sw = Stopwatch.StartNew();
    var detectors = PresidioDetector.Find(text);
    var detSec = sw.Elapsed.TotalSeconds;
    if (!detectors.Complete) Console.WriteLine("Detector warning: " + detectors.Warning);
    sw.Restart();
    var ai = LocalAiReview.FindDetailed(text, detectorFindings: detectors.Candidates);
    var aiSec = sw.Elapsed.TotalSeconds;
    if (!ai.Complete) Console.WriteLine("Reviewer warning: " + ai.Warning);
    sw.Restart();
    var final = FindingPostProcessor.Complete(text, detectors.Candidates.Concat(ai.Candidates));
    var postSec = sw.Elapsed.TotalSeconds;
    Stage.Last = new FullStage
    {
        Doc = truth.Id, Chars = text.Length, DetectorSeconds = detSec, ReviewSeconds = aiSec, PostSeconds = postSec,
        Engines = detectors.Engines, DetectorWarning = detectors.Warning, ReviewWarning = ai.Warning,
        SectionsSkippedByTriage = ai.SectionsSkippedByTriage, FailedSections = ai.FailedSections.Count,
        DetectorStage = detectors.Candidates.Select(Slim).ToList(), ReviewStage = ai.Candidates.Select(Slim).ToList(), Final = final.Select(Slim).ToList(),
    };
    return final;
}

static StageCandidate Slim(Candidate c) => new() { Value = c.Value, Category = c.Category, Source = c.Source, DetectionSources = c.DetectionSources.ToList(), Confidence = c.Confidence };

static int Run(string[] args)
{
    var mode = Cli.Opt(args, "mode") ?? "detector";
    if (mode is not ("detector" or "full" or "oracle")) throw new ArgumentException("--mode must be detector, oracle or full");
    var seeds = Cli.Seeds(args);
    var entities = int.Parse(Cli.Opt(args, "entities") ?? "60");
    var repo = FindRepoRoot();
    var root = Cli.Opt(args, "results") ?? Path.Combine(repo ?? Directory.GetCurrentDirectory(), "tests", "SyntheticBenchmark", "results");
    var outDir = Cli.NewResultsFolder(Path.GetFullPath(root), $"{mode}-{AppName}");
    var timer = Stopwatch.StartNew();
    var docsDir = Path.Combine(outDir, "docs"); var protDir = Path.Combine(outDir, "protected");
    Directory.CreateDirectory(protDir);
    var truth = Cli.GenerateAll(seeds, entities, docsDir);
    var findings = new FindingsSet();
    var protectedText = new Dictionary<string, string>();
    var failures = new List<string>(); var notes = new List<string>();
    var fullStages = new List<FullStage>();

    foreach (var doc in truth.Documents)
    {
        var src = Path.Combine(docsDir, doc.File);
        var text = DocumentProcessor.ReadText(src);
        var docTimer = Stopwatch.StartNew();
        List<Candidate> cands;
        try { cands = Detect(mode, text, doc); }
        catch (Exception ex) { failures.Add($"{doc.Id}: detection failed: {ex.GetType().Name}: {ex.Message}"); cands = new List<Candidate>(); }
        var detectSeconds = docTimer.Elapsed.TotalSeconds;
        if (Stage.Last != null) { fullStages.Add(Stage.Last); Stage.Last = null; }
        if (mode == "full") Console.WriteLine($"{doc.Id}: {text.Length} chars, detect+review {detectSeconds:F1}s, {cands.Count} findings");
        var project = Project.New("Synthetic benchmark", "test only");
        var pairs = cands.Select(c => (c, m: project.EnsureMapping(c, "doc", approved: true))).ToList();
        foreach (var m in project.Mappings) m.Approved = true; // simulate the analyst choosing Replace for every finding
        project.ConsolidatePersonAliases();
        var protectedPath = Path.Combine(protDir, doc.Id + ".docx");
        try
        {
            try { DocumentProcessor.Desensitize(src, protectedPath, project.Mappings); }
            catch (InvalidDataException blocked) when (blocked.Message.StartsWith("Export blocked", StringComparison.Ordinal))
            {
                // The export gate refuses approved replacements of values the policy treats as common words. A real analyst would
                // reject those findings; the benchmark approves everything, so export again allowing unapplied mappings to keep
                // measuring preservation, and report the block as a warning.
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

    File.WriteAllText(Path.Combine(outDir, "findings.json"), Json.Write(findings));
    var all = Scorer.Score(truth, findings, id => protectedText.TryGetValue(id, out var t) ? t : null);
    all.Warnings.AddRange(failures); all.Warnings.AddRange(notes);
    File.WriteAllText(Path.Combine(outDir, "score.json"), Json.Write(all));
    File.WriteAllText(Path.Combine(outDir, "score.md"), Report.Markdown(all, $"Synthetic benchmark: {mode} mode, {AppName} app"));
    foreach (var seed in seeds)
    {
        var sub = new TruthSet { Documents = truth.Documents.Where(d => d.Seed == seed).ToList() };
        var s = Scorer.Score(sub, findings, id => protectedText.TryGetValue(id, out var t) ? t : null);
        File.WriteAllText(Path.Combine(outDir, $"score-seed{seed}.json"), Json.Write(s));
        var st = s.Overall["strict"]; var le = s.Overall["lenient"];
        Console.WriteLine($"seed {seed}: strict P {st.Precision:P1} R {st.Recall:P1} F1 {st.F1:P1} | lenient P {le.Precision:P1} R {le.Recall:P1} | docs {s.Documents}");
    }
    var o = all.Overall["strict"]; var l = all.Overall["lenient"];
    Console.WriteLine($"ALL   : strict P {o.Precision:P1} R {o.Recall:P1} F1 {o.F1:P1} | lenient P {l.Precision:P1} R {l.Recall:P1} F1 {l.F1:P1}");
    if (all.Preservation.Evaluated) Console.WriteLine($"preserved {all.Preservation.Retained}/{all.Preservation.Total}; sensitive removed {all.Preservation.SensitiveRemoved}/{all.Preservation.SensitiveTotal}");
    if (all.Consistency.Evaluated) Console.WriteLine($"entities with >=2 forms {all.Consistency.MultiFormEntities}, consistent {all.Consistency.ConsistentEntities}, shared-token violations {all.Consistency.SharedTokenViolations}");

    if (mode == "full") WriteFullReports(outDir, truth, fullStages, protectedText, timer.Elapsed.TotalSeconds);

    var meta = new
    {
        scope = "Synthetic benchmark with fictional ground truth, not real-world accuracy",
        mode, app = AppName, seeds, entitiesPerSeed = entities, documents = truth.Documents.Count,
        generatorVersion = Generator.Version, date = DateTime.Now.ToString("o"),
        gitCommit = repo == null ? "unknown" : Git(repo, "rev-parse HEAD"),
        gitDirty = repo == null ? "unknown" : (Git(repo, "status --porcelain -- . :!tests/SyntheticBenchmark/results").Length > 0).ToString(),
        elapsedSeconds = timer.Elapsed.TotalSeconds,
        command = Environment.CommandLine,
    };
    File.WriteAllText(Path.Combine(outDir, "run.json"), Json.Write(meta));
    Console.WriteLine("Results: " + outDir);
    return failures.Count == 0 ? 0 : 1;
}


static void WriteFullReports(string outDir, TruthSet truth, List<FullStage> stages, Dictionary<string, string> protectedText, double elapsed)
{
    File.WriteAllText(Path.Combine(outDir, "full-details.json"), Json.Write(stages));
    string[] Names(StageCandidate c) => c.DetectionSources.Append(c.Source).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
    var sources = stages.SelectMany(st => st.DetectorStage.Concat(st.ReviewStage)).SelectMany(Names).Distinct().OrderBy(x => x).ToList();
    FindingsSet Subset(Func<StageCandidate, bool> keep, Func<FullStage, IEnumerable<StageCandidate>> pick) => new()
    {
        Documents = stages.Select(st => new DocFindings { Id = st.Doc, Findings = pick(st).Where(keep).Select(c => new Finding { Value = c.Value, Category = c.Category }).ToList() }).ToList(),
    };
    ScoreResult Sc(FindingsSet f) => Scorer.Score(truth, f, _ => null);
    var cats = new[] { "PERSON", "ORGANIZATION", "PROJECT", "IDENTIFIER", "PHONE", "EMAIL", "ADDRESS" };
    var md = new System.Text.StringBuilder();
    md.AppendLine("# Per-source contribution (strict, before policy and post-processing merges)\n");
    md.AppendLine("Each row scores only the candidates that source proposed or co-detected (DetectionSources), against the same truth. Recall columns are per category strict recall of that source alone. Candidates are the stage outputs of PresidioDetector.Find (rules plus workers, already deduplicated and post-processed by the detector) and LocalAiReview (reviewers). Synthetic data only.\n");
    md.AppendLine("| Source | Findings | Strict P | Strict R | " + string.Join(" | ", cats.Select(c => c + " R")) + " |");
    md.AppendLine("| --- | --- | --- | --- | " + string.Join(" | ", cats.Select(_ => "---")) + " |");
    var perSource = new Dictionary<string, ScoreResult>();
    foreach (var src in sources)
    {
        var r = Sc(Subset(c => Names(c).Contains(src), st => st.DetectorStage.Concat(st.ReviewStage)));
        perSource[src] = r; var o = r.Overall["strict"];
        string Rc(string cat) => r.PerCategory.TryGetValue(cat, out var d) && d.TryGetValue("strict", out var m) ? m.Recall.ToString("P0") : "n/a";
        md.AppendLine($"| {src} | {o.Findings} | {o.Precision:P1} | {o.Recall:P1} | " + string.Join(" | ", cats.Select(Rc)) + " |");
    }
    var union = Sc(Subset(_ => true, st => st.DetectorStage.Concat(st.ReviewStage)));
    var fin = Sc(Subset(_ => true, st => st.Final));
    md.AppendLine();
    md.AppendLine("## Union of all candidates before the final policy pass versus the final list\n");
    md.AppendLine("| Stage | Findings | Strict P | Strict R | Lenient R | anyCategory R |");
    md.AppendLine("| --- | --- | --- | --- | --- | --- |");
    foreach (var (name, r) in new[] { ("Detector + reviewer union", union), ("Final (after FindingPostProcessor.Complete)", fin) })
        md.AppendLine($"| {name} | {r.Overall["strict"].Findings} | {r.Overall["strict"].Precision:P1} | {r.Overall["strict"].Recall:P1} | {r.Overall["lenient"].Recall:P1} | {r.Overall["anyCategory"].Recall:P1} |");
    // Truth spans found in the union but absent from the final list = dropped by the policy pass.
    var finalMissingFromUnion = fin.Misses.Select(m => m.Doc + "|" + m.Value).ToHashSet();
    var dropped = union.Misses.Select(m => m.Doc + "|" + m.Value).ToHashSet();
    var lost = fin.Misses.Where(m => !dropped.Contains(m.Doc + "|" + m.Value)).ToList();
    md.AppendLine($"\nTrue spans present in the union but missing from the final list (dropped or altered by the policy pass): {lost.Count}\n");
    foreach (var m in lost.Take(60)) md.AppendLine($"- {m.Doc} {m.Category} `{m.Value}` ({m.Reason})");
    md.AppendLine("\n## Timing per document (seconds)\n");
    md.AppendLine("| Doc | Chars | Detectors | Reviewers | Post | Engines | Failed review sections | Skipped by triage |");
    md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
    foreach (var st in stages) md.AppendLine($"| {st.Doc} | {st.Chars} | {st.DetectorSeconds:F1} | {st.ReviewSeconds:F1} | {st.PostSeconds:F2} | {string.Join("+", st.Engines)} | {st.FailedSections} | {st.SectionsSkippedByTriage} |");
    md.AppendLine($"\nTotals: detectors {stages.Sum(x => x.DetectorSeconds):F0}s, reviewers {stages.Sum(x => x.ReviewSeconds):F0}s, post {stages.Sum(x => x.PostSeconds):F1}s, whole run {elapsed:F0}s.");
    var warns = stages.SelectMany(x => new[] { x.DetectorWarning, x.ReviewWarning }).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
    if (warns.Count > 0) { md.AppendLine("\nWarnings:"); foreach (var w in warns) md.AppendLine("- " + w); }
    File.WriteAllText(Path.Combine(outDir, "sources.md"), md.ToString());
    Console.WriteLine("Per-source report: " + Path.Combine(outDir, "sources.md"));
}

sealed class StageCandidate
{
    public string Value { get; set; } = ""; public string Category { get; set; } = ""; public string Source { get; set; } = "";
    public List<string> DetectionSources { get; set; } = new(); public double Confidence { get; set; }
}

sealed class FullStage
{
    public string Doc { get; set; } = ""; public int Chars { get; set; }
    public double DetectorSeconds { get; set; } public double ReviewSeconds { get; set; } public double PostSeconds { get; set; }
    public List<string> Engines { get; set; } = new(); public string DetectorWarning { get; set; } = ""; public string ReviewWarning { get; set; } = "";
    public int SectionsSkippedByTriage { get; set; } public int FailedSections { get; set; }
    public List<StageCandidate> DetectorStage { get; set; } = new(); public List<StageCandidate> ReviewStage { get; set; } = new(); public List<StageCandidate> Final { get; set; } = new();
}

static class Stage { public static FullStage? Last; }

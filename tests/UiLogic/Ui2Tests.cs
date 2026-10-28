using Desensitizer;

// Stage S7: performance settings, the scan-time estimate, the final-review policy (U8) and the support info (D2).
// Logic only; the dialogs themselves are built in SmokeTests. All names and values here are fictional.
static class Ui2Tests
{
    public static void Run(CheckFn Check, string work)
    {
        // ---- performance settings -------------------------------------------------------------------
        Check("perf: 0 and below are shown as Automatic", UiPerfSettings.Display(0) == "Automatic" && UiPerfSettings.Display(-3) == "Automatic" && UiPerfSettings.Display(6) == "6");
        foreach (var automatic in new[] { "", "  ", "Automatic", "automatic", "AUTO", "0", null })
            Check("perf: '" + (automatic ?? "null") + "' means Automatic", UiPerfSettings.TryParse(automatic, 8, out var v, out var p) && v == 0 && p == null);
        Check("perf: a number inside the range is kept", UiPerfSettings.TryParse(" 4 ", 8, out var four, out _) && four == 4);
        Check("perf: a number above the limit is lowered to the limit", UiPerfSettings.TryParse("99", 8, out var clamped, out var clampProblem) && clamped == 8 && clampProblem == null);
        foreach (var bad in new[] { "abc", "-1", "2.5", "1e3", "4 threads", "99999999999999", "٣", "+2" })
        {
            var ok = true;
            string? problem = null;
            var value = -1;
            try { ok = UiPerfSettings.TryParse(bad, 8, out value, out problem); } catch (Exception) { ok = true; problem = "THROWN"; }
            Check("perf: bad input '" + bad + "' is refused with a message and does not throw", !ok && value == 0 && problem != null && problem != "THROWN");
        }
        Check("perf: idle minutes default to 5 when blank, 0 or Automatic", UiPerfSettings.TryParseIdle("", out var idleBlank, out _) && idleBlank == 5 && UiPerfSettings.TryParseIdle("0", out var idleZero, out _) && idleZero == 5);
        Check("perf: idle minutes are limited to 60", UiPerfSettings.TryParseIdle("500", out var idleBig, out _) && idleBig == 60 && UiPerfSettings.TryParseIdle("7", out var idleSeven, out _) && idleSeven == 7);

        var target = new AppSettings { LlamaThreads = 1, MaxResidentModels = 1, DetectorConcurrency = 1, WorkerIdleMinutes = 9 };
        var problemText = UiPerfSettings.Apply(target, "4", "x", "2", "8", 8, 3, out var badField);
        Check("perf: one bad field refuses the whole save and leaves the settings unchanged", problemText != null && badField == 1 && target.LlamaThreads == 1 && target.WorkerIdleMinutes == 9);
        problemText = UiPerfSettings.Apply(target, "4", "2", "Automatic", "8", 8, 3, out badField);
        Check("perf: valid fields are written (Automatic becomes 0)", problemText == null && badField == -1 && target.LlamaThreads == 4 && target.MaxResidentModels == 2 && target.DetectorConcurrency == 0 && target.WorkerIdleMinutes == 8);
        UiPerfSettings.Apply(target, "100", "3", "3", "1", 8, 1, out _);
        Check("perf: threads are limited to the core count and models to the number of models in the app", target.LlamaThreads == 8 && target.MaxResidentModels == 1 && target.DetectorConcurrency == 3);
        var copy = new AppSettings();
        UiPerfSettings.CopyPerformance(new AppSettings { LlamaThreads = -4, MaxResidentModels = 77, DetectorConcurrency = 5, WorkerIdleMinutes = 0 }, copy);
        Check("perf: copied values from a damaged settings file are clamped", copy.LlamaThreads == 0 && copy.MaxResidentModels == 3 && copy.DetectorConcurrency == 3 && copy.WorkerIdleMinutes == 5);
        var summary = UiPerfSettings.EffectiveSummary(17179869184, 8, 6, 1, 2, 5);
        Check("perf: the read-only summary shows memory, cores and values in use", summary.Contains("16.0 GB") && summary.Contains("8 logical cores") && summary.Contains("6 AI threads") && summary.Contains("1 AI model kept loaded") && summary.Contains("2 detector engines"));
        Check("perf: the tooltips explain memory on a 16 GB laptop and the note says changes apply to the next scan", UiPerfSettings.ResidentTip.Contains("16 GB") && UiPerfSettings.DetectorsTip.Contains("16 GB") && UiPerfSettings.AppliesNote.Contains("next scan"));
        Check("perf: no interface text has a dash used as punctuation", new[] { UiPerfSettings.ThreadsTip, UiPerfSettings.ResidentTip, UiPerfSettings.DetectorsTip, UiPerfSettings.IdleTip, UiPerfSettings.AppliesNote, summary }.All(t => !t.Contains('—') && !t.Contains('–')));

        var settingsFolder = Path.Combine(work, "ui2-settings");
        AppSettingsStore.UseFolder(settingsFolder);
        try
        {
            AppSettingsStore.Save(new AppSettings { LlamaThreads = 3, MaxResidentModels = 2, DetectorConcurrency = 1, WorkerIdleMinutes = 12 });
            Check("perf: saving applies the values to the running policy at once", PerfConfig.LlamaThreads == 3 && PerfConfig.MaxResidentModels == 2 && PerfConfig.DetectorConcurrency == 1 && PerfConfig.WorkerIdleMinutes == 12);
            PerfConfig.Apply(0, 0, 0);
            var loaded = AppSettingsStore.Load();
            Check("perf: the saved values are read back from settings.json", loaded.LlamaThreads == 3 && loaded.MaxResidentModels == 2 && loaded.DetectorConcurrency == 1 && loaded.WorkerIdleMinutes == 12 && PerfConfig.LlamaThreads == 3);
            File.WriteAllText(Path.Combine(settingsFolder, "settings.json"), "{ \"LlamaThreads\": \"not a number\" ");
            var damaged = AppSettingsStore.Load();
            Check("perf: a damaged settings file loads as Automatic without throwing", damaged.LlamaThreads == 0 && PerfConfig.LlamaThreads == 0);
        }
        finally { AppSettingsStore.UseFolder(null); PerfConfig.Apply(0, 0, 0); }

        // ---- scan-time estimate ---------------------------------------------------------------------
        var sizes = Enumerable.Repeat(1000, 10).ToList();
        var estimator = new UiScanEstimator(new PerfTimingTracker());
        var first = estimator.Begin(sizes, 3);
        Check("estimate: the first estimate names sections, reviewers and 'may differ'", first.StartsWith("about 10 sections x 3 reviewers, roughly ") && first.Contains("may differ"), first);
        Check("estimate: the display text starts with a capital", UiScanEstimator.ForDisplay(first).StartsWith("About 10 sections"));
        var clock = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var timed = new UiScanEstimator(new PerfTimingTracker(), () => clock);
        timed.Begin(sizes, 1);
        string? updated = null;
        for (var section = 1; section <= 6; section++)
        {
            updated = timed.Report("Local AI review", "Understanding section " + section + " of 10 in context. Nothing is uploaded.") ?? updated;
            clock = clock.AddSeconds(section == 1 ? 40 : 5);
        }
        Check("estimate: after timed sections the estimate is measured and no longer says 'may differ'", updated != null && !updated.Contains("may differ") && updated.Contains("1 reviewer"), updated);
        Check("estimate: the first section (model load) is not counted in the average", timed.Tracker.AverageSecondsPerPass is double avg && Math.Abs(avg - 5) < 0.01, timed.Tracker.AverageSecondsPerPass?.ToString());
        Check("estimate: a later estimate by the same session starts from the measurement", new UiScanEstimator(timed.Tracker).Begin(sizes, 1).Contains("may differ") == false);
        var other = new UiScanEstimator(new PerfTimingTracker(), () => clock);
        other.Begin(sizes, 3);
        Check("estimate: detector progress without a reviewer name changes nothing", other.Report("Presidio", "Checking conventional identity.") == null && other.Report(null, null) == null);
        other.End();
        Check("estimate: after End nothing is timed", other.Report("AI reviewer 1 of 3: Qwen", "Reviewing section 3 of 10 locally.") == null && other.Tracker.Count == 0);
        Check("estimate: no text gives 'Nothing to review' without throwing", new UiScanEstimator().Begin(UiScanEstimator.SectionSizes(""), 3).Contains("Nothing to review"));
        Check("estimate: sections follow the AI reviewers' split of the text", UiScanEstimator.SectionSizes(new string('a', 5000) + " b").Count >= 5);

        // ---- U8 final review policy ----------------------------------------------------------------
        Check("U8: with the final AI review off every export asks", UiFinalReview.NeedsChoice(false) && !UiFinalReview.NeedsChoice(true));
        Check("U8: closing the question never exports", !UiFinalReview.MayExport(false, FinalReviewChoice.Cancel) && UiFinalReview.MayExport(false, FinalReviewChoice.ExportWithoutAi) && UiFinalReview.MayExport(true, FinalReviewChoice.Cancel));
        Check("U8: the AI review runs when it is on or when the user chose it", UiFinalReview.AiReviewWillRun(true, FinalReviewChoice.Cancel) && UiFinalReview.AiReviewWillRun(false, FinalReviewChoice.RunAiReview) && !UiFinalReview.AiReviewWillRun(false, FinalReviewChoice.ExportWithoutAi) && !UiFinalReview.AiReviewWillRun(false, FinalReviewChoice.Cancel));
        var explanation = UiFinalReview.Explanation(3, "about 10 sections x 3 reviewers, roughly 30 min (first estimate, may differ)");
        Check("U8: the explanation says only the rule-based detectors check the copy and the AI will not look for names, companies or systems", explanation.Contains("only the rule-based detectors") && explanation.Contains("names, companies or systems") && explanation.Contains("30 min"));
        Check("U8: the buttons are worded as specified", UiFinalReview.RunButton == "Run the AI review too" && UiFinalReview.SkipButton == "Export without the AI review");
        Check("U8: the completion text says when the AI review was skipped", UiFinalReview.CompletionText(false, 0).Contains("final AI review was skipped") && !UiFinalReview.CompletionText(true, 0).Contains("skipped"));

        Mapping Make(string value, string category, string source, string file = "f1", bool approved = false, bool ignored = false, bool manual = false) =>
            new Mapping { OriginalValue = value, NormalizedValue = Project.Normalize(value), Category = category, DetectionSource = source, DetectionSources = new List<string> { source }, SourceFileIds = new List<string> { file }, Approved = approved, Ignored = ignored, IsManual = manual };
        var mappings = new List<Mapping>
        {
            Make("Orion Portal", "SYSTEM", "Local AI"),
            Make("Atlas", "PROJECT", "AI Gemma"),
            Make("Bluebird", "PRODUCT", "AI Qwen"),
            Make("Kept Tool", "SYSTEM", "Local AI", ignored: true),
            Make("Replaced Tool", "SYSTEM", "Local AI", approved: true),
            Make("Rules System", "SYSTEM", "Rules"),
            Make("Both Sources", "SYSTEM", "Local AI"),
            Make("Mary Santos", "PERSON", "Local AI"),
            Make("Other File", "PROJECT", "Local AI", file: "f2"),
            Make("Typed By Hand", "SYSTEM", "Manual", manual: true),
        };
        mappings[6].DetectionSources.Add("GLiNER");
        Check("advisory: only open AI-only project, system and product findings of this file are counted", UiAdvisory.CountOpenMappings(mappings, "f1") == 3, UiAdvisory.CountOpenMappings(mappings, "f1").ToString());
        var finalScan = new List<Candidate>
        {
            new Candidate { Value = "Orion Portal", Category = "SYSTEM", Source = "Local AI" },
            new Candidate { Value = "Zephyr Suite", Category = "PRODUCT", Source = "AI Qwen" },
            new Candidate { Value = "Replaced Tool", Category = "SYSTEM", Source = "Local AI" },
            new Candidate { Value = "Kept Tool", Category = "SYSTEM", Source = "Local AI" },
            new Candidate { Value = "Rules Only", Category = "SYSTEM", Source = "Rules" },
            new Candidate { Value = "Someone Else", Category = "PERSON", Source = "Local AI" },
        };
        Check("advisory: final-scan AI findings add to the count without double counting a mapping", UiAdvisory.Count(mappings, "f1", finalScan) == 4, UiAdvisory.Count(mappings, "f1", finalScan).ToString());
        Check("advisory: no mappings and no candidates give zero and null candidates are accepted", UiAdvisory.Count(new List<Mapping>(), "f1", null) == 0);
        Check("advisory: the line gives the count only", UiAdvisory.Line(4).StartsWith("4 AI suggestions were not applied") && UiAdvisory.Line(1).StartsWith("1 AI suggestion was not applied") && UiAdvisory.Line(0) == "");
        var line = UiAdvisory.Line(4) + UiFinalReview.CompletionText(false, 4) + UiFinalReview.Explanation(1, "");
        Check("advisory: the text never contains a finding value", !new[] { "Orion", "Atlas", "Bluebird", "Zephyr" }.Any(line.Contains));
        Check("advisory: Local AI and AI consensus names are AI sources, detectors are not", UiAdvisory.IsAiSource("Local AI") && UiAdvisory.IsAiSource("AI NuExtract") && !UiAdvisory.IsAiSource("GLiNER") && !UiAdvisory.IsAiSource("Rules") && !UiAdvisory.IsAiSource(null));

        // ---- D2 support info, collected from the real app state ------------------------------------
        SupportTelemetry.Reset();
        var project = Project.New("Zebra Merger Project", "Violet-Harbor-Lantern-42");
        var secretFile = new FileRecord { Id = "f9", SourcePath = Path.Combine(work, "Zebra Payroll Mary Santos.docx"), Display = "Zebra Payroll Mary Santos.docx" };
        project.Files.Add(secretFile);
        project.EnsureMapping(new Candidate { Value = "Mary Santos", Category = "PERSON", Confidence = 0.9, Source = "Rules" }, "f9");
        project.EnsureMapping(new Candidate { Value = "Zebra Holdings", Category = "ORGANIZATION", Confidence = 0.9, Source = "Rules" }, "f9");
        project.EnsureMapping(new Candidate { Value = "Quartz Gateway", Category = "weird category with Mary Santos", Confidence = 0.9, Source = "Rules" }, "f9");
        SupportTelemetry.RecordEngines(new[] { "Presidio", "GLiNER" }, new[] { "Presidio", "GLiNER", "Privacy Filter" });
        SupportTelemetry.RecordPhase("scan", 12.5);
        SupportTelemetry.RecordSections(14, 1);
        var text = SupportCollector.CollectText(project, "Desensitizer", 3);
        var leaks = new[] { "Mary", "Santos", "Zebra", "Quartz", "Violet", "Lantern", "Payroll", ".docx", work, "weird category" };
        Check("support: the collected report holds none of the project's names, values, file names, paths or passphrase", leaks.All(l => !text.Contains(l, StringComparison.OrdinalIgnoreCase)), text);
        Check("support: it counts findings per category and unknown categories as OTHER", text.Contains("ORGANIZATION 1") && text.Contains("PERSON 1") && text.Contains("OTHER 1"), text);
        Check("support: it shows engines, timings and sections from the last check", text.Contains("GLiNER: ran") && text.Contains("Privacy Filter: did not run") && text.Contains("scan: 12.5") && text.Contains("Sections: 14 checked, 1 failed"), text);
        Check("support: it shows the version, memory, cores and performance values", text.Contains("App: Desensitizer ") && text.Contains("Total memory:") && text.Contains("Logical CPU cores: " + Environment.ProcessorCount) && text.Contains("AI threads:"), text);
        var user = Environment.UserName;
        Check("support: the Windows user name and computer name are not in the report", (user.Length < 4 || !text.Contains(user, StringComparison.OrdinalIgnoreCase)) && (Environment.MachineName.Length < 4 || !text.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase)));
        Check("support: the report is plain ASCII with no dashes used as punctuation", text.All(c => c < 128));
        Check("support: collecting with no project works", SupportCollector.CollectText(null, "AI Desensitizer", 3).Contains("none recorded yet"));
        SupportTelemetry.Reset();
    }
}

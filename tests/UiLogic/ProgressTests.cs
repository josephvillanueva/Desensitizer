using Desensitizer;

static class ProgressTests
{
    public static void Run(CheckFn Check)
    {
        // ---- recognizing reviewer reports -----------------------------------------------------------
        Check("parse: an AI reviewer report", UiProgressTracker.TryParseReviewer("AI reviewer 2 of 3: NuExtract", "Reviewing section 5 of 20 locally. Nothing is uploaded.", out var name, out var section, out var count) && name == "NuExtract" && section == 5 && count == 20);
        Check("parse: the single model report is Qwen", UiProgressTracker.TryParseReviewer("Local AI review", "Understanding section 3 of 12 in context. Nothing is uploaded.", out name, out section, out count) && name == "Qwen" && section == 3 && count == 12);
        Check("parse: detector stages are not reviewers", !UiProgressTracker.TryParseReviewer("Final detector scan", "Checking the protected copy with local detectors.", out _, out _, out _) && !UiProgressTracker.TryParseReviewer(null, null, out _, out _, out _));
        Check("parse: a reviewer without a section still counts", UiProgressTracker.TryParseReviewer("AI reviewer 1 of 3: Qwen", "Starting", out name, out section, out count) && name == "Qwen" && section == 0 && count == 0);

        // ---- rows -----------------------------------------------------------------------------------
        var now = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        DateTime Clock() => now;
        var tracker = new UiProgressTracker(Clock, new[] { "Qwen", "NuExtract", "Gemma" });
        tracker.Start();
        Check("rows: the three reviewers wait before they start", tracker.Rows.Count == 3 && tracker.Rows.All(r => r.State == UiRowState.Waiting && r.StateText == "Waiting"));
        tracker.Report("AI reviewer 1 of 3: Qwen", 1, 60, "Reviewing section 1 of 20 locally.");
        Check("rows: the first reviewer is running at section 1 of 20", tracker.Rows[0].State == UiRowState.Running && tracker.Rows[0].StateText == "Section 1 of 20");
        now = now.AddSeconds(5);
        tracker.Report("AI reviewer 1 of 3: Qwen", 20, 60, "Reviewing section 20 of 20 locally.");
        Check("rows: percent follows the section", tracker.Rows[0].Percent > 90 && tracker.Rows[0].Percent < 100);
        now = now.AddSeconds(5);
        tracker.Report("AI reviewer 2 of 3: NuExtract", 21, 60, "Reviewing section 1 of 20 locally.");
        Check("rows: starting the next reviewer finishes the previous one", tracker.Rows[0].State == UiRowState.Done && tracker.Rows[0].StateText == "Done" && tracker.Rows[0].Percent == 100 && tracker.Rows[1].State == UiRowState.Running && tracker.Rows[2].State == UiRowState.Waiting);
        tracker.Finish();
        Check("rows: finishing marks running rows done and leaves waiting rows", tracker.Rows[1].State == UiRowState.Done && tracker.Rows[2].State == UiRowState.Waiting);

        var single = new UiProgressTracker(Clock);
        single.Start();
        Check("rows: the single reviewer app starts with no rows", single.Rows.Count == 0);
        single.Report("Local AI review", 1, 12, "Understanding section 1 of 12 in context.");
        Check("rows: a row is created when the reviewer reports", single.Rows.Count == 1 && single.Rows[0].Title == "Qwen");
        single.Report("Local detector scan", 2, 6, "Checking exact patterns.");
        Check("rows: detector reports create no row", single.Rows.Count == 1);

        // ---- elapsed and estimate ----------------------------------------------------------------------
        now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var timed = new UiProgressTracker(Clock);
        timed.Start();
        Check("time: starts at zero", timed.ElapsedText == "Elapsed 0:00" && timed.EtaText == null);
        timed.Report("Local AI review", 1, 40, "Understanding section 1 of 40 in context.");
        for (var step = 2; step <= 4; step++)
        {
            now = now.AddSeconds(10);
            timed.Report("Local AI review", step, 40, "Understanding section " + step + " of 40 in context.");
        }
        Check("time: elapsed is shown as minutes and seconds", timed.ElapsedText == "Elapsed 0:30");
        Check("time: three finished sections give a rolling average estimate (36 left at 10 s)", timed.EtaText == "About 6 minutes left", timed.EtaText);
        now = now.AddSeconds(120);
        Check("time: elapsed can pass an hour", UiProgressTracker.FormatElapsed(TimeSpan.FromSeconds(3725)) == "1:02:05");
        Check("time: the combined line has both parts", timed.TimeText.StartsWith("Elapsed 2:30  |  About "));

        var early = new UiProgressTracker(Clock);
        early.Start();
        early.Report("Local AI review", 1, 40, "Understanding section 1 of 40 in context.");
        now = now.AddSeconds(10);
        early.Report("Local AI review", 2, 40, "Understanding section 2 of 40 in context.");
        Check("time: no estimate from fewer than three samples", early.EtaText == null && early.TimeText.StartsWith("Elapsed") && !early.TimeText.Contains("left"));

        // The window average is rolling: old slow sections stop counting.
        var rolling = new UiProgressTracker(Clock);
        rolling.Start();
        rolling.Report("x", 0, 100, "");
        var step2 = 0;
        for (var i = 0; i < 5; i++) { now = now.AddSeconds(60); step2++; rolling.Report("x", step2, 100, ""); }
        for (var i = 0; i < 10; i++) { now = now.AddSeconds(2); step2++; rolling.Report("x", step2, 100, ""); }
        Check("time: the estimate follows the last ten sections, not the slow start", rolling.EtaText == "About 3 minutes left", rolling.EtaText);

        // Units can arrive in jumps (several sections between two reports).
        var jumpy = new UiProgressTracker(Clock);
        jumpy.Start();
        jumpy.Report("x", 0, 100, "");
        for (var i = 1; i <= 4; i++) { now = now.AddSeconds(20); jumpy.Report("x", i * 2, 100, ""); }
        Check("time: a jump of two sections counts as two units", jumpy.EtaText == "About 15 minutes left", jumpy.EtaText);

        // A restarted stage resets the estimate.
        jumpy.Report("y", 0, 6, "");
        Check("time: a new stage with another size clears the old speed", jumpy.EtaText == null);

        Check("eta format: under a minute", UiProgressTracker.FormatEta(TimeSpan.FromSeconds(20)) == "Less than a minute left");
        Check("eta format: about one minute", UiProgressTracker.FormatEta(TimeSpan.FromSeconds(60)) == "About 1 minute left");
        Check("eta format: minutes", UiProgressTracker.FormatEta(TimeSpan.FromMinutes(7.4)) == "About 7 minutes left");
        Check("eta format: hours", UiProgressTracker.FormatEta(TimeSpan.FromMinutes(95)) == "About 1 h 35 min left");

        // ---- model loading notice -----------------------------------------------------------------------
        now = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
        var loading = new UiProgressTracker(Clock, new[] { "Qwen", "NuExtract", "Gemma" });
        loading.Start();
        Check("model load: no notice before any reviewer starts", loading.ModelLoadNotice == null);
        loading.Report("AI reviewer 1 of 3: Qwen", 1, 30, "Reviewing section 1 of 10 locally.");
        Check("model load: no notice in the first seconds", loading.ModelLoadNotice == null);
        now = now.AddSeconds(6);
        Check("model load: notice after a few seconds on section 1", (loading.ModelLoadNotice ?? "").StartsWith("Loading the Qwen model"));
        now = now.AddSeconds(1);
        loading.Report("AI reviewer 1 of 3: Qwen", 2, 30, "Reviewing section 2 of 10 locally.");
        now = now.AddSeconds(30);
        Check("model load: no notice once section 2 is reached", loading.ModelLoadNotice == null);
        loading.Report("AI reviewer 2 of 3: NuExtract", 11, 30, "Reviewing section 1 of 10 locally.");
        now = now.AddSeconds(5);
        Check("model load: the next reviewer loads its own model", (loading.ModelLoadNotice ?? "").StartsWith("Loading the NuExtract model"));
        loading.Finish();
        Check("model load: no notice after the run", loading.ModelLoadNotice == null && loading.EtaText == null);
    }
}

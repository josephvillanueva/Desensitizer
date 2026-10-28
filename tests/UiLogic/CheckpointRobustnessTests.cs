using System.Text.Json;
using Desensitizer;

static class CheckpointRobustnessTests
{
    public static void Run(CheckFn Check, string work)
    {
        var loaded = JsonSerializer.Deserialize<ReviewCheckpoint>("{\"Entries\":null,\"Failed\":null}")!;
        Check("checkpoint robustness: null collections deserialize as empty", loaded.Entries.Count == 0 && loaded.Failed.Count == 0 && !UiResume.HasWork(loaded));
        using (var cache = new ExportReviewCache())
        {
            var recorder = new UiCheckpointRecorder();
            Check("checkpoint robustness: null collections restore without throwing", recorder.Restore(loaded, cache) == 0 && recorder.Commit().Entries.Count == 0);
        }
        loaded = JsonSerializer.Deserialize<ReviewCheckpoint>("{\"Failed\":[null,{}, {\"Reviewer\":null,\"SectionIndex\":1,\"SectionCount\":2,\"Reason\":null}]}")!;
        Check("checkpoint robustness: null and invalid failure rows are discarded", loaded.Failed.Count == 1 && loaded.Failed[0].Reviewer == "Local AI" && loaded.Failed[0].Reason == "");
        Check("checkpoint robustness: normalized failures remain resumable", UiResume.HasWork(loaded) && UiResume.FailedSummary(loaded.Failed).Contains("Local AI: section 1 of 2"));
        loaded.Failed.Add(null!);
        Check("checkpoint robustness: later null failure does not break summaries", UiResume.FailedLines(loaded.Failed).Count == 1 && UiResume.FailedSummary(loaded.Failed).StartsWith("1 section was"));
        var privateFailure = new SectionFailure { Reviewer = "private\nsource text", SectionIndex = 1, SectionCount = 2, Reason = "Sensitive dependency text must not escape" };
        var safeFailure = new UiCheckpointRecorder().Commit(new[] { privateFailure }).Failed.Single();
        Check("checkpoint robustness: failure metadata cannot echo arbitrary legacy diagnostics", safeFailure.Reviewer == "Local AI" && !safeFailure.Reason.Contains("Sensitive") && safeFailure.Reason.Contains("repeated"));

        const string marker = "Local AI review is incomplete. These sections could not be checked: ";
        Check("checkpoint robustness: oversized section index is rejected", UiResume.ParseFailures(marker + "Qwen section 999999999999999999999999 of 20.").Count == 0);
        Check("checkpoint robustness: oversized section count is rejected", UiResume.ParseFailures(marker + "Qwen section 1 of 999999999999999999999999.").Count == 0);
        Check("checkpoint robustness: zero and out-of-range sections are rejected", UiResume.ParseFailures(marker + "Qwen sections 0, 2 of 4.").Count == 0 && UiResume.ParseFailures(marker + "Qwen section 5 of 4.").Count == 0);
        Check("checkpoint robustness: explicit zero section total is rejected", UiResume.ParseFailures(marker + "Qwen section 1 of 0.").Count == 0);
        Check("checkpoint robustness: a malformed reviewer group cannot hide valid groups", UiResume.ParseFailures(marker + "Qwen section 999999999999999999999; Gemma section 2 of 3.").Single().Reviewer == "Gemma");
        Check("checkpoint robustness: duplicate failure references collapse", UiResume.ParseFailures(marker + "Qwen sections 1, 1 of 4.").Count == 1);
        Check("checkpoint robustness: oversized diagnostic text is not parsed", UiResume.ParseFailures(marker + new string('9', 70000)).Count == 0);
        Check("checkpoint robustness: valid failure text still parses", UiResume.ParseFailures(marker + "Qwen sections 1, 3 of 4; NuExtract section 2 of 4. Retry locally.").Count == 3);

        var rejected = new ReviewCheckpoint { SectionsStarted = -4, SectionsTotal = -2 };
        rejected.Entries[new string('A', 64)] = "invalid cached JSON";
        using (var cache = new ExportReviewCache())
        {
            var recorder = new UiCheckpointRecorder();
            Check("checkpoint robustness: rejected cache data is not retained outside the cache", recorder.Restore(rejected, cache) == 0 && recorder.Count == 0 && recorder.Commit().Entries.Count == 0);
        }
        const string config = "Fictional checkpoint configuration";
        const string section = "The payment threshold is 50000.";
        using (var cache = new ExportReviewCache())
        {
            cache.Review(config, section, () => new List<Candidate>(), CancellationToken.None);
            var future = new ReviewCheckpoint { Version = 999, Entries = cache.Export() };
            var recorder = new UiCheckpointRecorder();
            Check("checkpoint robustness: future checkpoint cannot borrow a cache's existing entries", recorder.Restore(future, cache) == 0 && recorder.Count == 0 && !UiResume.HasWork(future));
            var saved = new ReviewCheckpoint { Entries = cache.Export(), SectionsTotal = -3, SectionsStarted = -4 };
            Check("checkpoint robustness: valid cached empty review remains reusable", recorder.Restore(saved, cache) == 1 && recorder.Count == 1 && recorder.Commit().SectionsTotal == 0 && recorder.Commit().SectionsStarted == 0);
        }

        var project = Project.New("Fictional checkpoint cleanup", "not saved");
        var removed = new FileRecord { Id = "file-one" };
        var kept = new FileRecord { Id = "file-one-more" };
        project.Files.AddRange(new[] { removed, kept });
        foreach (var key in new[] { "file-one", "file-one#deep", "file-one#export", "file-one-more#deep", "file-one#deep-extra" })
            project.ReviewCheckpoints[key] = new ReviewCheckpoint();
        project.RemoveFile(removed);
        Check("checkpoint cleanup: removing a file deletes legacy and both current checkpoints", !project.ReviewCheckpoints.ContainsKey("file-one") && !project.ReviewCheckpoints.ContainsKey("file-one#deep") && !project.ReviewCheckpoints.ContainsKey("file-one#export"));
        Check("checkpoint cleanup: similar ids and unrelated suffixes are not deleted", project.ReviewCheckpoints.ContainsKey("file-one-more#deep") && project.ReviewCheckpoints.ContainsKey("file-one#deep-extra") && project.Files.Single() == kept);
        project.RemoveFile(kept);
        Check("checkpoint cleanup: final removed file's review text is removed", !project.ReviewCheckpoints.ContainsKey("file-one-more#deep"));
        Retention(Check);
    }

    private static ReviewCheckpoint Snapshot(int day = 1, int valueLength = 1)
        => new() { UpdatedAt = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
            Entries = new() { ["entry"] = new string('x', valueLength) } };

    private static void Retention(CheckFn Check)
    {
        var progress = new Dictionary<string, ReviewCheckpoint>
        {
            ["doc"] = Snapshot(), ["doc#deep"] = Snapshot(), ["doc#export"] = Snapshot(),
            ["doc-other#deep"] = Snapshot(), ["doc#unknown"] = Snapshot(),
            ["orphan#deep"] = Snapshot(), ["future#deep"] = new() { Version = 99, Entries = new() { ["entry"] = "x" } },
            ["empty#deep"] = new(), ["null#deep"] = null!
        };
        var result = UiCheckpointPruner.Prune(progress, new[] { "doc", "doc-other", "future", "empty", "null" });
        Check("checkpoint retention: exact current and legacy file keys remain", progress.ContainsKey("doc") && progress.ContainsKey("doc#deep") && progress.ContainsKey("doc#export"));
        Check("checkpoint retention: similar file id is not an orphan", progress.ContainsKey("doc-other#deep"));
        Check("checkpoint retention: unknown, orphan, future, empty and null snapshots are removed", result.InvalidRemoved == 5 && result.RetainedCount == 4 && !progress.ContainsKey("future#deep"));
        var again = UiCheckpointPruner.Prune(progress, new[] { "doc", "doc-other" });
        Check("checkpoint retention: repeated cleanup is stable", again.RemovedCount == 0 && again.RetainedCount == 4 && again.RetainedCharacters == result.RetainedCharacters);

        var ids = Enumerable.Range(1, 35).Select(i => "doc" + i).ToArray();
        progress = ids.ToDictionary(id => id + "#deep", _ => Snapshot());
        result = UiCheckpointPruner.Prune(progress, ids);
        Check("checkpoint retention: default count bound is 32", progress.Count == UiCheckpointPruner.MaxCheckpoints && result.Evicted == 3 && result.InvalidRemoved == 0);
        Check("checkpoint retention: equal-date eviction order is deterministic", !progress.ContainsKey("doc1#deep") && !progress.ContainsKey("doc10#deep") && !progress.ContainsKey("doc11#deep"));

        progress = new() { ["old#deep"] = Snapshot(1), ["middle#deep"] = Snapshot(2), ["new#deep"] = Snapshot(3) };
        result = UiCheckpointPruner.Prune(progress, new[] { "old", "middle", "new" }, maxCheckpoints: 2);
        Check("checkpoint retention: oldest checkpoint is evicted first", !progress.ContainsKey("old#deep") && progress.ContainsKey("middle#deep") && progress.ContainsKey("new#deep"));
        progress = new() { ["old#deep"] = Snapshot(1), ["middle#deep"] = Snapshot(2), ["new#deep"] = Snapshot(3) };
        result = UiCheckpointPruner.Prune(progress, new[] { "old", "middle", "new" }, "old#deep", maxCheckpoints: 2);
        Check("checkpoint retention: active checkpoint survives count pressure", progress.ContainsKey("old#deep") && !progress.ContainsKey("middle#deep") && !result.ActiveEvicted);

        progress = new() { ["old#deep"] = Snapshot(1, 20), ["new#deep"] = Snapshot(2, 20) };
        // One snapshot uses 8 checkpoint-key + 5 entry-key + 20 entry-value characters.
        result = UiCheckpointPruner.Prune(progress, new[] { "old", "new" }, maxCharacters: 33);
        Check("checkpoint retention: aggregate entry character budget evicts oldest", result.Evicted == 1 && progress.ContainsKey("new#deep") && result.RetainedCharacters == 33);
        progress = new() { ["old#deep"] = Snapshot(1, 20), ["new#deep"] = Snapshot(2, 20) };
        result = UiCheckpointPruner.Prune(progress, new[] { "old", "new" }, "old#deep", maxCharacters: 33);
        Check("checkpoint retention: active checkpoint survives aggregate budget pressure", progress.ContainsKey("old#deep") && !progress.ContainsKey("new#deep") && !result.ActiveEvicted);
        progress = new() { ["old#deep"] = Snapshot(1, 80), ["new#deep"] = Snapshot(2, 20) };
        result = UiCheckpointPruner.Prune(progress, new[] { "old", "new" }, "old#deep", maxCharacters: 33);
        Check("checkpoint retention: over-budget active cache cannot defeat the hard bound", result.ActiveEvicted && result.Evicted == 1 && progress.ContainsKey("new#deep") && result.RetainedCharacters == 33);
        progress = new() { ["old#deep"] = Snapshot(1) };
        result = UiCheckpointPruner.Prune(progress, new[] { "old" }, "old#deep", maxCharacters: 0);
        Check("checkpoint retention: zero character budget discards optional cached progress", result.ActiveEvicted && progress.Count == 0 && result.RetainedCharacters == 0);
        progress = new() { ["orphan#deep"] = Snapshot() };
        result = UiCheckpointPruner.Prune(progress, Array.Empty<string>(), "orphan#deep");
        Check("checkpoint retention: active orphan is not retained", result.ActiveEvicted && result.InvalidRemoved == 1 && progress.Count == 0);

        var project = Project.New("Fictional retention safety", "not saved");
        var file = new FileRecord { Id = "doc", DeepReviewComplete = false };
        project.Files.Add(file); project.ReviewCheckpoints["doc#deep"] = Snapshot();
        UiCheckpointPruner.Prune(project.ReviewCheckpoints, project.Files.Select(f => f.Id), maxCharacters: 0);
        Check("checkpoint retention: evicting progress never completes a document check", !file.DeepReviewComplete && project.ReviewCheckpoints.Count == 0);
        progress = new() { ["old#deep"] = Snapshot() };
        result = UiCheckpointPruner.Prune(progress, new[] { "old" }, "absent#deep");
        Check("checkpoint retention: absent active key does not evict valid state", result.RemovedCount == 0 && !result.ActiveEvicted && progress.Count == 1);
        progress = new() { ["old#deep"] = new() { Failed = new() { new() { Reviewer = "Qwen", SectionIndex = 1, SectionCount = 2 } } } };
        result = UiCheckpointPruner.Prune(progress, new[] { "old" });
        Check("checkpoint retention: failure-only checkpoints remain useful", result.RemovedCount == 0 && progress.Count == 1);
        Check("checkpoint retention: default aggregate bound is sixteen Mi characters", UiCheckpointPruner.MaxRetainedCharacters == 16 * 1024 * 1024);
    }
}

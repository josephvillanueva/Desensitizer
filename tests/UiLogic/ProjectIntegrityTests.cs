using System.Text.Json;
using Desensitizer;

// UI-8 (source fingerprint), UI-3 (decisions survive a rescan), UI-6 (one window per project),
// UI-9 and UI-10 (checkpoints) and the smaller UI-12 items.
static class ProjectIntegrityTests
{
    private const string Pass = "Violet-Harbor-Lantern-42";

    private static Candidate Find(string value, string category) => new() { Value = value, Category = category, Confidence = 0.9, Source = "Rules" };

    // UI-3: a rescan keeps the decisions of what it finds again and only drops the rest once the new results exist.
    private static void Rescan(CheckFn Check, string folder)
    {
        Project Build(out Mapping replace, out Mapping keep, out Mapping review, out Mapping gone, out Mapping shared)
        {
            var p = Project.New("Rescan project", Pass);
            p.Files.Add(new FileRecord { Id = "f1" }); p.Files.Add(new FileRecord { Id = "f2" });
            replace = p.EnsureMapping(Find("Zorvex Holdings", "ORGANIZATION"), "f1"); replace.Decision = "Replace";
            keep = p.EnsureMapping(Find("Orion Portal", "SYSTEM"), "f1"); keep.Decision = "Keep as-is";
            review = p.EnsureMapping(Find("Maple Group", "ORGANIZATION"), "f1");
            gone = p.EnsureMapping(Find("Old Finding", "ORGANIZATION"), "f1"); gone.Decision = "Replace";
            shared = p.EnsureMapping(Find("Shared Corp", "ORGANIZATION"), "f1"); shared.Decision = "Replace"; p.EnsureMapping(Find("Shared Corp", "ORGANIZATION"), "f2");
            return p;
        }

        var project = Build(out var replace, out var keep, out var review, out var gone, out var shared);
        var tokenBefore = replace.Token;
        project.BeginRescan("f1");
        Check("rescan: nothing is deleted or detached when the rescan starts", project.Mappings.Count == 5 && project.Mappings.All(m => m.SourceFileIds.Contains("f1")));
        var reloaded = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(project))!;
        Check("rescan: the saved state in the middle of a rescan still holds every mapping and decision", reloaded.Mappings.Count == 5 && reloaded.Mappings.Single(m => m.OriginalValue == "Zorvex Holdings").Approved);
        var again = project.EnsureMapping(Find("zorvex  holdings", "ORGANIZATION"), "f1");
        project.EnsureMapping(Find("Orion Portal", "SYSTEM"), "f1");
        project.EnsureMapping(Find("Maple Group", "ORGANIZATION"), "f1");
        var fresh = project.EnsureMapping(Find("Brand New Ltd", "ORGANIZATION"), "f1");
        Check("rescan: a value found again is the same mapping with the same token and decision", ReferenceEquals(again, replace) && replace.Token == tokenBefore && replace.Approved);
        Check("rescan: Keep as-is and Review decisions carry over", keep.Ignored && !keep.Approved && !review.Approved && !review.Ignored);
        project.CommitRescan("f1");
        Check("rescan: a finding the new results do not contain is dropped only after they are complete", !project.Mappings.Contains(gone) && project.Mappings.Contains(fresh));
        Check("rescan: a mapping shared with another file stays and loses only this file", project.Mappings.Contains(shared) && !shared.SourceFileIds.Contains("f1") && shared.SourceFileIds.Contains("f2") && shared.Approved);

        var stopped = Build(out _, out _, out _, out var goneStopped, out _);
        stopped.BeginRescan("f1");
        stopped.EnsureMapping(Find("Zorvex Holdings", "ORGANIZATION"), "f1");
        stopped.AbandonRescan();
        Check("rescan: a stopped rescan keeps every earlier mapping and decision", stopped.Mappings.Contains(goneStopped) && goneStopped.Approved && stopped.Mappings.Count == 5);
        stopped.EnsureMapping(Find("Late Finding", "ORGANIZATION"), "f1");
        stopped.CommitRescan("f1");
        Check("rescan: committing without a started rescan removes nothing that has a file", stopped.Mappings.Contains(goneStopped));
        var manual = Build(out _, out _, out _, out _, out _);
        var typed = manual.EnsureMapping("Typed By Hand", "ORGANIZATION", true, 1, "Manual", "", "f1", true);
        manual.BeginRescan("f1"); manual.CommitRescan("f1");
        Check("rescan: a manual mapping is never dropped by a rescan", manual.Mappings.Contains(typed) && typed.Approved);
    }

    public static void Run(CheckFn Check, string work)
    {
        var folder = Path.Combine(work, "integrity");
        Directory.CreateDirectory(folder);

        // ---- UI-8: content fingerprint -------------------------------------------------------------------------
        var doc = Path.Combine(folder, "scan-me.txt");
        File.WriteAllText(doc, "Mary Santos works at Orion Portal.");
        var record = new FileRecord { Id = "f1", SourcePath = doc, Display = "scan-me.txt" };
        Check("fingerprint: a file that was never scanned is unknown", UiFileFingerprint.Check(record, doc) == FingerprintState.Unknown);
        Check("fingerprint: recording stores a SHA-256 and the length", UiFileFingerprint.Record(record, doc) && record.ContentSha256!.Length == 64 && record.ContentLength == new FileInfo(doc).Length);
        Check("fingerprint: the unchanged file matches", UiFileFingerprint.Check(record, doc) == FingerprintState.Match);
        File.WriteAllText(doc, "Mary Santos works at Orion Portal!");
        Check("fingerprint: a changed file is a mismatch (a rescan is required)", UiFileFingerprint.Check(record, doc) == FingerprintState.Mismatch);
        File.WriteAllText(doc, "Mary Santos works at Orion Portal.");
        Check("fingerprint: putting the same content back matches again", UiFileFingerprint.Check(record, doc) == FingerprintState.Match);
        Check("fingerprint: a missing file is reported as missing", UiFileFingerprint.Check(record, Path.Combine(folder, "gone.txt")) == FingerprintState.Missing);
        UiFileFingerprint.Check(record, doc, out var current);
        Check("fingerprint: Differs is false for the same print and true for another", !UiFileFingerprint.Differs(record, current) && UiFileFingerprint.Differs(record, ("0000", 1)) && !UiFileFingerprint.Differs(new FileRecord(), ("0000", 1)));
        var old = JsonSerializer.Deserialize<FileRecord>("{\"Id\":\"x\",\"SourcePath\":\"a.txt\",\"Display\":\"a.txt\",\"Status\":\"Added\"}")!;
        Check("fingerprint: a project file saved before the field existed loads as unknown", old.ContentSha256 == null && old.ContentLength == null);
        var projectPath = Path.Combine(folder, "fp.dsproj");
        var project = Project.New("Fingerprint project", Pass);
        project.Files.Add(record);
        ProjectStore.Save(projectPath, project);
        var reloaded = ProjectStore.Load(projectPath, Pass).Files.Single();
        Check("fingerprint: the fingerprint survives saving and loading", reloaded.ContentSha256 == record.ContentSha256 && reloaded.ContentLength == record.ContentLength);
        Rescan(Check, folder);
        Check("fingerprint: the messages contain no em or en dash", !UiFileFingerprint.MismatchMessage.Contains('—') && !UiFileFingerprint.MismatchMessage.Contains('–') && !UiFileFingerprint.UnknownNotice.Contains('—') && !UiFileFingerprint.UnknownNotice.Contains('–'));
    }
}

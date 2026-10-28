using System.Text.Json;
using Desensitizer;

static class CheckpointTests
{
    private static Candidate Found(string value) => new Candidate { Value = value, Category = "ORGANIZATION", Confidence = 0, Source = "Local AI", Explanation = "test" };

    public static void Run(CheckFn Check, string work)
    {
        const string config = "test-config";
        const string sectionOne = "The Harbor Group signed the agreement.";
        const string sectionTwo = "Nothing identifying is written in this paragraph.";

        // ---- recording a run ----------------------------------------------------------------------
        using (var first = new ExportReviewCache())
        {
            var recorder = new UiCheckpointRecorder();
            recorder.Attach(first);
            first.Review(config, sectionOne, () => new List<Candidate> { Found("Harbor Group") }, CancellationToken.None);
            first.Review(config, sectionTwo, () => new List<Candidate>(), CancellationToken.None);
            recorder.ReportProgress(2, 6);
            Check("record: every stored section is recorded as it completes", recorder.Count == 2 && recorder.HasUncommittedChanges && recorder.Total == 6);
            var failures = new[] { new SectionFailure { Reviewer = "Gemma", SectionIndex = 3, SectionCount = 3, Reason = "HttpRequestException: connection refused" } };
            var checkpoint = recorder.Commit(failures);
            Check("record: committing returns the entries, the totals and the failures", checkpoint.Entries.Count == 2 && checkpoint.SectionsTotal == 6 && checkpoint.SectionsStarted == 2 && checkpoint.Failed.Count == 1 && !recorder.HasUncommittedChanges);
            Check("record: the failure list carries no document text", !JsonSerializer.Serialize(checkpoint.Failed).Contains("Harbor"));

            // ---- it survives the project round trip, encrypted inside the project file ---------------------
            var path = Path.Combine(work, "resume.dsproj");
            var project = Project.New("Resume test", "Violet-Harbor-Lantern-42");
            project.Files.Add(new FileRecord { Id = "file1", SourcePath = "C:\\fictional\\a.docx", Display = "a.docx" });
            project.ReviewCheckpoints["file1"] = checkpoint;
            ProjectStore.Save(path, project);
            var raw = File.ReadAllBytes(path);
            Check("persist: the file on disk does not contain the reviewed text or the finding", !System.Text.Encoding.UTF8.GetString(raw).Contains("Harbor Group") && !System.Text.Encoding.UTF8.GetString(raw).Contains("agreement"));
            var loaded = ProjectStore.Load(path, "Violet-Harbor-Lantern-42");
            Check("persist: the checkpoint comes back from the encrypted project", loaded.ReviewCheckpoints.TryGetValue("file1", out var back) && back.Entries.Count == 2 && back.SectionsTotal == 6 && back.Failed.Count == 1);

            // ---- resuming -----------------------------------------------------------------------------------
            using var second = new ExportReviewCache();
            var resumed = new UiCheckpointRecorder();
            var accepted = resumed.Restore(loaded.ReviewCheckpoints["file1"], second);
            resumed.Attach(second);
            Check("resume: the cache accepts the saved entries", accepted == 2 && resumed.Count == 2 && resumed.Total == 6);
            var calls = 0;
            var hit = second.Review(config, sectionOne, () => { calls++; return new List<Candidate>(); }, CancellationToken.None);
            Check("resume: a finished section is not reviewed again and keeps its finding", calls == 0 && hit.Count == 1 && hit[0].Value == "Harbor Group" && second.Hits == 1);
            var fresh = second.Review(config, "Gemma reviewed the Orion Portal today.", () => { calls++; return new List<Candidate> { Found("Orion Portal") }; }, CancellationToken.None);
            Check("resume: a new section is reviewed and recorded too", calls == 1 && fresh.Count == 1 && resumed.Count == 3);
            Check("resume: the offer text counts finished sections", UiResume.OfferText(loaded.ReviewCheckpoints["file1"]) == "Resume previous check (2 of 6 sections done)");
            Check("resume: the offer needs saved work", UiResume.HasWork(loaded.ReviewCheckpoints["file1"]) && !UiResume.HasWork(null) && !UiResume.HasWork(new ReviewCheckpoint()));
        }

        // ---- damaged and hostile checkpoints fail closed to a fresh review -------------------------------
        using (var guarded = new ExportReviewCache())
        {
            var damaged = new ReviewCheckpoint { SectionsTotal = 2 };
            damaged.Entries["not-a-key"] = "[]";
            damaged.Entries[new string('A', 64)] = "{broken json";
            var recorder = new UiCheckpointRecorder();
            var accepted = recorder.Restore(damaged, guarded);
            Check("damaged: malformed keys and entries are ignored by the cache", accepted == 0 && guarded.Count == 0);
            var future = new ReviewCheckpoint { Version = 99 };
            future.Entries[ExportReviewCache.KeyFor(config, sectionOne)] = "[]";
            Check("damaged: an unknown snapshot version is ignored", new UiCheckpointRecorder().Restore(future, guarded) == 0 && guarded.Count == 0);
            Check("damaged: restoring nothing is harmless", new UiCheckpointRecorder().Restore(null, guarded) == 0);

            // A valid-looking entry that does not belong to the section is a miss, not an empty review.
            var key = ExportReviewCache.KeyFor(config, sectionTwo);
            var wrong = new ReviewCheckpoint();
            wrong.Entries[key] = JsonSerializer.Serialize(new List<Candidate> { Found("Not In This Section") });
            new UiCheckpointRecorder().Restore(wrong, guarded);
            var calls = 0;
            guarded.Review(config, sectionTwo, () => { calls++; return new List<Candidate>(); }, CancellationToken.None);
            Check("damaged: an ungrounded entry is reviewed again", calls == 1);
        }

        // ---- failed section text -----------------------------------------------------------------------------
        var failed = new[]
        {
            new SectionFailure { Reviewer = "Gemma", SectionIndex = 3, SectionCount = 20 },
            new SectionFailure { Reviewer = "Gemma", SectionIndex = 7, SectionCount = 20 },
            new SectionFailure { Reviewer = "NuExtract", SectionIndex = 2, SectionCount = 20 }
        };
        var lines = UiResume.FailedLines(failed);
        Check("failed: one line per reviewer", lines.Count == 2 && lines.Contains("Gemma: sections 3, 7 of 20") && lines.Contains("NuExtract: section 2 of 20"));
        Check("failed: the summary counts sections and says to retry", UiResume.FailedSummary(failed).StartsWith("3 sections were not checked (") && UiResume.FailedSummary(failed).Contains("Retry"));
        Check("failed: nothing to say when nothing failed", UiResume.FailedSummary(Array.Empty<SectionFailure>()) == "");

        // ---- hidden channel text for the scan ------------------------------------------------------------------
        Check("hidden: no lines leaves the text unchanged", UiHiddenText.Append("Body text.", Array.Empty<string>()) == "Body text." && UiHiddenText.Append("Body text.", null) == "Body text." && UiHiddenText.Append("Body text.", new[] { " ", "" }) == "Body text.");
        var combined = UiHiddenText.Append("Body text.", new[] { "sdt alias: Atlas Retail Corp", "field code: MERGEFIELD Client" });
        Check("hidden: lines are added under a labelled heading after the body", combined.StartsWith("Body text.\n\n" + UiHiddenText.Heading + "\n") && combined.Contains("sdt alias: Atlas Retail Corp") && combined.Contains("field code: MERGEFIELD Client"));
        Check("hidden: the heading cannot be mistaken for a replacement token", !System.Text.RegularExpressions.Regex.IsMatch(UiHiddenText.Heading, @"\[[A-Z_]+_\d{3,}\]"));

        // Real document: a value that exists only in a content control title is visible to the scan text.
        var docxFolder = Path.Combine(work, "hidden"); Directory.CreateDirectory(docxFolder);
        var docx = Path.Combine(docxFolder, "sdt.docx");
        using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(docx, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(
                new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Visible body text.")))));
            var body = main.Document.Body!;
            body.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.SdtBlock(
                new DocumentFormat.OpenXml.Wordprocessing.SdtProperties(new DocumentFormat.OpenXml.Wordprocessing.SdtAlias { Val = "Zephyr Holdings Client" }),
                new DocumentFormat.OpenXml.Wordprocessing.SdtContentBlock(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Control text."))))));
            main.Document.Save();
        }
        var bodyText = DocumentProcessor.ReadText(docx);
        var hiddenLines = DocumentProcessor.ReadHiddenChannels(docx);
        var scanText = UiHiddenText.Append(bodyText, hiddenLines);
        Check("hidden: the content control title is not in the body text", !bodyText.Contains("Zephyr Holdings Client"));
        Check("hidden: the real document yields the hidden line and the scan text contains it", hiddenLines.Any(l => l.Contains("Zephyr Holdings Client")) && scanText.Contains("Zephyr Holdings Client") && scanText.StartsWith(bodyText));
    }
}

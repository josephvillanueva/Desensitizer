using System.Windows.Input;
using Desensitizer;

static class ExportAndSaveTests
{
    private const string Pass = "Violet-Harbor-Lantern-42";

    public static void Run(CheckFn Check, string work)
    {
        // ---- failed sections written by the engine can be read back (retry) ---------------------------------
        var failures = new[]
        {
            new SectionFailure { Reviewer = "Gemma", SectionIndex = 3, SectionCount = 20, Reason = "x" },
            new SectionFailure { Reviewer = "Gemma", SectionIndex = 7, SectionCount = 20, Reason = "x" },
            new SectionFailure { Reviewer = "NuExtract", SectionIndex = 2, SectionCount = 20, Reason = "x" }
        };
        var sentence = LocalReviewResult.DescribeFailures(failures);
        var parsed = UiResume.ParseFailures(sentence + " Other warning text.");
        Check("retry: the engine's own failure sentence is read back", parsed.Count == 3 && parsed.Count(f => f.Reviewer == "Gemma" && f.SectionCount == 20) == 2 && parsed.Any(f => f.Reviewer == "NuExtract" && f.SectionIndex == 2), sentence);
        Check("retry: a single reviewer and section", UiResume.ParseFailures(LocalReviewResult.DescribeFailures(new[] { new SectionFailure { Reviewer = "Qwen", SectionIndex = 1, SectionCount = 4 } })).Single().Reviewer == "Qwen");
        Check("retry: text without failed sections gives none", UiResume.ParseFailures("Presidio worker did not start.").Count == 0 && UiResume.ParseFailures(null).Count == 0 && UiResume.ParseFailures("").Count == 0);
        Check("retry: the parsed list carries no text from the document", parsed.All(f => f.Reason == ""));

        // ---- shortcuts ---------------------------------------------------------------------------------------------
        Check("shortcut: Ctrl+O opens, Ctrl+N creates only on the project list", UiShortcuts.Decide(Key.O, ModifierKeys.Control, false, false, false, false) == UiShortcut.OpenProject && UiShortcuts.Decide(Key.N, ModifierKeys.Control, false, false, false, false) == UiShortcut.NewProject && UiShortcuts.Decide(Key.N, ModifierKeys.Control, false, true, false, false) == UiShortcut.None);
        Check("shortcut: F5 scans only with a project open", UiShortcuts.Decide(Key.F5, ModifierKeys.None, false, true, false, false) == UiShortcut.ScanSelected && UiShortcuts.Decide(Key.F5, ModifierKeys.None, false, false, false, false) == UiShortcut.None);
        Check("shortcut: R, K and V work on the mapping table only", UiShortcuts.Decide(Key.R, ModifierKeys.None, false, true, true, false) == UiShortcut.Replace && UiShortcuts.Decide(Key.K, ModifierKeys.None, false, true, true, false) == UiShortcut.Keep && UiShortcuts.Decide(Key.V, ModifierKeys.None, false, true, true, false) == UiShortcut.Review && UiShortcuts.Decide(Key.R, ModifierKeys.None, false, true, false, false) == UiShortcut.None);
        Check("shortcut: nothing fires while busy or typing", UiShortcuts.Decide(Key.F5, ModifierKeys.None, true, true, false, false) == UiShortcut.None && UiShortcuts.Decide(Key.R, ModifierKeys.None, false, true, true, true) == UiShortcut.None && UiShortcuts.Decide(Key.O, ModifierKeys.Control, false, false, false, true) == UiShortcut.None);
        Check("shortcut: other modifiers do not trigger letters", UiShortcuts.Decide(Key.R, ModifierKeys.Control, false, true, true, false) == UiShortcut.None && UiShortcuts.Decide(Key.K, ModifierKeys.Alt, false, true, true, false) == UiShortcut.None);

        // ---- image placeholder settings (item U5) ----------------------------------------------------------------
        var defaults = new AppSettings();
        var built = UiImageSettings.Build(defaults);
        Check("images: the defaults are the engine defaults", built.ImagePlaceholder == "[IMAGE REMOVED]" && built.IncludeImageSize && !built.IncludeImageAltText);
        Check("images: an empty placeholder removes pictures silently", !UiImageSettings.Build("", true, false).PlaceholdersEnabled && !UiImageSettings.Build("   ", true, false).PlaceholdersEnabled);
        Check("images: custom text is trimmed and kept", UiImageSettings.Build("  [PICTURE OMITTED]  ", false, true).ImagePlaceholder == "[PICTURE OMITTED]");
        Check("images: a token shaped placeholder is refused with a readable reason", (UiImageSettings.Problem("[PERSON_001]", true, false) ?? "").Contains("replacement token"));
        Check("images: an over long placeholder is refused", (UiImageSettings.Problem(new string('x', 121), true, false) ?? "").Contains("longer than"));
        Check("images: a control character is refused", UiImageSettings.Problem("bad\u0001text", true, false) != null);
        Check("images: acceptable settings have no problem", UiImageSettings.Problem("[IMAGE REMOVED]", true, false) == null);
        Check("images: the description says what happens", UiImageSettings.Describe(defaults).Contains("[IMAGE REMOVED]") && UiImageSettings.Describe(new AppSettings { ImagePlaceholder = "" }).Contains("without a placeholder"));
        Check("images: settings written before this existed load with the defaults", System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"AutomaticDeepReview\":true}")!.ImagePlaceholder == "[IMAGE REMOVED]");
        // The options reach the real export: a custom placeholder appears in the protected document.
        var folder = Path.Combine(work, "images"); Directory.CreateDirectory(folder);
        var docx = Path.Combine(folder, "pic.docx");
        using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(docx, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Plain text only.")))));
            main.Document.Save();
        }
        var exported = Path.Combine(folder, "out.docx");
        DocumentProcessor.Desensitize(docx, exported, Array.Empty<Mapping>(), UiImageSettings.Build("[PICTURE OMITTED]", true, false));
        Check("images: the options are accepted by the export", DocumentProcessor.ReadText(exported).Contains("Plain text only."));

        // ---- saving without blocking (item U4) --------------------------------------------------------------------
        var path = Path.Combine(work, "split-save.dsproj");
        var project = Project.New("Split save test", Pass);
        project.Files.Add(new FileRecord { Id = "f1", SourcePath = "C:\\fictional\\a.txt", Display = "a.txt" });
        var snapshot = ProjectStore.Serialize(project);
        project.Name = "Changed after the snapshot";
        ProjectStore.SaveSerialized(path, Pass, snapshot);
        var loaded = ProjectStore.Load(path, Pass);
        Check("split save: the file holds the snapshot, not later edits", loaded.Name == "Split save test" && loaded.Files.Count == 1);
        Check("split save: the snapshot buffer is wiped after the write", snapshot.All(b => b == 0));
        Check("split save: nothing is left beside the project", Directory.GetFiles(work, ".split-save.dsproj*").Length == 0 && !File.Exists(path + ".bak"));
        var weak = Project.New("Weak one", "short");
        var threw = false;
        try { ProjectStore.Serialize(weak); } catch (PassphrasePolicyException) { threw = true; }
        Check("split save: a weak new passphrase is still refused when the snapshot is taken", threw);
        var bad = false;
        try { ProjectStore.SaveSerialized("", Pass, new byte[] { 1, 2, 3 }); } catch (InvalidDataException) { bad = true; }
        Check("split save: an invalid path is refused", bad);
        Check("split save: Save and the two steps give a project that opens the same way", ProjectStore.Load(path, Pass).Name == "Split save test");
    }
}

using System.Reflection;
using Desensitizer;

static class MessageTests
{
    public static void Run(CheckFn Check, string work)
    {
        // ---- passphrase text ------------------------------------------------------------------
        var weak = PassphrasePolicy.Check("abc", "Atlas");
        var lines = UiMessages.PassphraseProblemLines(weak);
        Check("passphrase: every problem is listed, not only the first", lines.Count == weak.Problems.Count && lines.Count >= 2);
        Check("passphrase: the length rule says 12, never 8", lines.Any(l => l.Contains("12 characters")) && !UiMessages.PassphraseRuleSummary.Contains("8 characters") && UiMessages.PassphraseRuleSummary.Contains("12 characters"));
        Check("passphrase: an acceptable passphrase has no problem lines", UiMessages.PassphraseProblemLines(PassphrasePolicy.Check("Violet-Harbor-Lantern-42", "Atlas")).Count == 0);
        Check("passphrase: the refusal dialog lists all problems with bullets", UiMessages.PassphraseRefusal(weak).Split('\n').Count(l => l.StartsWith("• ")) == lines.Count);
        Check("passphrase: no refusal text for an acceptable passphrase", UiMessages.PassphraseRefusal(PassphrasePolicy.Check("Violet-Harbor-Lantern-42", "Atlas")) == "");

        Check("create: a missing name is refused first", UiMessages.ValidateNewProject("  ", "Violet-Harbor-Lantern-42", "Violet-Harbor-Lantern-42") == "Enter a project name.");
        Check("create: a seven character passphrase is refused with the real rule", (UiMessages.ValidateNewProject("Atlas", "Ab1!xyz", "Ab1!xyz") ?? "").Contains("12 characters"));
        Check("create: an eight character passphrase is no longer enough", UiMessages.ValidateNewProject("Atlas", "Ab1!xyzq", "Ab1!xyzq") != null);
        Check("create: a mismatch is refused", UiMessages.ValidateNewProject("Atlas", "Violet-Harbor-Lantern-42", "Violet-Harbor-Lantern-43") == "The two passphrases do not match.");
        Check("create: a good name and passphrase pass", UiMessages.ValidateNewProject("Atlas", "Violet-Harbor-Lantern-42", "Violet-Harbor-Lantern-42") == null);
        Check("create: a passphrase built from the project name is refused", UiMessages.ValidateNewProject("Harborview", "Harborview2024!!", "Harborview2024!!") != null);

        Check("change: current passphrase required", UiMessages.ValidatePassphraseChange("", "Violet-Harbor-Lantern-42", "Violet-Harbor-Lantern-42", "Atlas") == "Enter the current passphrase.");
        Check("change: weak new passphrase lists every problem", (UiMessages.ValidatePassphraseChange("old-old-old-old-1", "short", "short", "Atlas") ?? "").Contains("12 characters"));
        Check("change: mismatch refused", UiMessages.ValidatePassphraseChange("old-old-old-old-1", "Violet-Harbor-Lantern-42", "other", "Atlas") == "The new passphrases do not match.");
        Check("change: same as current refused", UiMessages.ValidatePassphraseChange("Violet-Harbor-Lantern-42", "Violet-Harbor-Lantern-42", "Violet-Harbor-Lantern-42", "Atlas") == "Choose a different new passphrase.");
        Check("change: a valid change passes", UiMessages.ValidatePassphraseChange("old-old-old-old-1", "Violet-Harbor-Lantern-42", "Violet-Harbor-Lantern-42", "Atlas") == null);

        // ---- the one-time upgrade notice ----------------------------------------------------------
        Check("upgrade notice has the required wording", UiMessages.UpgradeNotice ==
            "This project will be upgraded to a stronger format. Older app versions will not be able to open it. A backup copy (.bak) is kept until the upgrade is verified.");

        // ---- legacy import --------------------------------------------------------------------
        Check("legacy prompt: singular and plural", UiMessages.LegacyImportPrompt(1).Contains("1 label ") && UiMessages.LegacyImportPrompt(3).Contains("3 labels"));
        Check("legacy result: all imported", UiMessages.LegacyImportResult(2, 2) == "2 labels imported into this project.");
        Check("legacy result: some skipped", UiMessages.LegacyImportResult(1, 3).Contains("1 of 3"));

        // ---- tracked changes banner ---------------------------------------------------------------
        Check("banner: nothing for a clean file", UiMessages.ReviewMarkupBanner(ReviewMarkupSummary.Empty) == "" && UiMessages.ReviewMarkupBanner(null) == "");
        var summary = new ReviewMarkupSummary { Insertions = 2, Deletions = 1, CommentsRemoved = 3 };
        var banner = UiMessages.ReviewMarkupBanner(summary);
        Check("banner: counts tracked changes and comments", banner.StartsWith("This file contains 3 tracked changes and 3 comments. "), banner);
        Check("banner: includes the engine's own description", banner.Contains(summary.Describe()));
        Check("banner: singular wording", UiMessages.ReviewMarkupBanner(new ReviewMarkupSummary { Insertions = 1, CommentsRemoved = 1 }).StartsWith("This file contains 1 tracked change and 1 comment. "));

        // ---- unapplied replacements (item G3) -----------------------------------------------------
        var folder = Path.Combine(work, "g3"); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.txt");
        File.WriteAllText(source, "The organization approves the request.");
        var generic = new Mapping { OriginalValue = "organization", NormalizedValue = Project.Normalize("organization"), Category = "ORGANIZATION", Token = "[ORGANIZATION_005]", Approved = true };
        InvalidDataException? thrown = null;
        try { DocumentProcessor.Desensitize(source, Path.Combine(folder, "out.txt"), new[] { generic }); }
        catch (InvalidDataException ex) { thrown = ex; }
        Check("unapplied: the engine still stops the export", thrown != null);
        var parsed = UiMessages.TryParseUnapplied(thrown, out var found);
        Check("unapplied: the real exception is recognized with count and category", parsed && found.Count == 1 && found.Categories == "ORGANIZATION: 1", thrown?.Message);
        var explanation = UiMessages.UnappliedExplanation(found);
        Check("unapplied: the explanation names count and category but never the value", explanation.Contains("1 approved replacement was not applied (ORGANIZATION: 1)") && !explanation.Contains("organization approves"));
        Check("unapplied: the explanation offers both choices", explanation.Contains("Fix the list") && explanation.Contains("Export anyway"));
        Check("unapplied: an unrelated InvalidDataException is not recognized", !UiMessages.TryParseUnapplied(new InvalidDataException("The document could not be processed safely (X). Nothing was released."), out _));
        Check("unapplied: other exception types and null are not recognized", !UiMessages.TryParseUnapplied(new IOException("Export blocked: 1 approved replacement (X: 1) would not be applied"), out _) && !UiMessages.TryParseUnapplied(null, out _));
        // Export anyway is the engine's explicit opt-in, and it leaves the dropped value visible.
        var anywayOut = Path.Combine(folder, "anyway.txt");
        DocumentProcessor.Desensitize(source, anywayOut, new[] { generic }, null, null, true);
        Check("unapplied: Export anyway works through allowUnappliedMappings and keeps the word", File.ReadAllText(anywayOut).Contains("organization"));
        Check("unapplied: nothing is written by the blocked attempt", !File.Exists(Path.Combine(folder, "out.txt")));

        // ---- rehydrate summary (item U10) ------------------------------------------------------------
        Check("rehydrate: clean summary", UiMessages.RehydrateSummary(0, "a.docx") == "Rehydrated copy exported: a.docx" && UiMessages.RehydrateDetail(0).Contains("restored"));
        Check("rehydrate: unknown tokens are counted", UiMessages.RehydrateSummary(2, "a.docx").Contains("2 unrecognized tokens") && UiMessages.RehydrateDetail(1).Contains("1 unrecognized token was"));

        Check("suppressed: the status note appears only when something was hidden", UiMessages.SuppressedNotice(0) == "" && UiMessages.SuppressedNotice(1).Contains("1 organization or vendor name was hidden") && UiMessages.SuppressedNotice(3).Contains("3 organization or vendor names were hidden"));
        Check("status: an old status with a dash is shown with a colon", UiMessages.PlainStatus("Deep check complete — review mappings") == "Deep check complete: review mappings" && UiMessages.PlainStatus("Scan incomplete – check settings") == "Scan incomplete: check settings" && UiMessages.PlainStatus("Ready to scan") == "Ready to scan" && UiMessages.PlainStatus(null) == "");

        // ---- the writing rule ---------------------------------------------------------------------------
        var texts = new List<string>();
        foreach (var type in new[] { typeof(UiMessages) })
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(string))) texts.Add((string)field.GetValue(null)!);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string))) texts.Add((string)property.GetValue(null)!);
        }
        texts.Add(UiMessages.PassphraseRefusal(weak));
        texts.Add(UiMessages.UnappliedExplanation(found));
        texts.Add(UiMessages.LegacyImportPrompt(2));
        texts.Add(banner);
        texts.Add(UiMessages.RehydrateDetail(3));
        Check("writing rule: no em dash or en dash in any interface text", texts.All(t => !t.Contains('—') && !t.Contains('–')));

        // The source of every window and dialog the user reads. Detector patterns elsewhere may name dashes on purpose.
        var root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "Shared"))) root = Path.GetDirectoryName(root);
        if (root == null) Check("writing rule: repository root found for the source scan", false);
        else
        {
            var files = new List<string>();
            foreach (var app in new[] { "Desensitizer", "AI.Desensitizer" })
                foreach (var name in new[] { "MainWindow.xaml", "MainWindow.xaml.cs", "Dialogs.cs", "Themes/DesignSystem.xaml" })
                    files.Add(Path.Combine(root, app, name));
            files.AddRange(Directory.GetFiles(Path.Combine(root, "Shared"), "Ui*.cs"));
            files.AddRange(Directory.GetFiles(Path.Combine(root, "Shared"), "Support*.cs"));
            var offenders = files.Where(f => File.Exists(f) && File.ReadAllText(f).IndexOfAny(new[] { '—', '–' }) >= 0).Select(Path.GetFileName).ToList();
            Check("writing rule: no em dash or en dash in the windows, dialogs and interface helpers", offenders.Count == 0 && files.Count(File.Exists) >= 11, string.Join(", ", offenders));
        }
    }
}

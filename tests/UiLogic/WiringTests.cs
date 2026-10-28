using System.Text;
using System.Text.Json;
using Desensitizer;

// The order of calls the windows make around a project: unlock, bind, edit, save, close.
static class WiringTests
{
    private const string Pass = "Violet-Harbor-Lantern-42";

    public static void Run(CheckFn Check, string work)
    {
        var root = Path.Combine(work, "app-data");
        ProjectCatalog.UseRoot(root);

        // ---- create, list, unlock, close -----------------------------------------------------------------
        var path = ProjectCatalog.NewProjectPath();
        var project = Project.New("Fictional Quarterly Review", Pass);
        ProjectStore.Save(path, project);
        var entry = new ProjectEntry { FilePath = path };
        UiProjectSession.Adopt(entry, project);
        Check("create: the list shows the real name in memory and bound preferences are active", entry.Name == "Fictional Quarterly Review" && entry.NameRevealed && ProjectScope.IsBound);
        ProjectCatalog.Save(new[] { entry });
        var catalogText = File.ReadAllText(Path.Combine(root, "projects.json"));
        Check("create: the catalog on disk holds a neutral label, not the name", !catalogText.Contains("Fictional") && catalogText.Contains("Project 1"));
        UiProjectSession.Close(entry);
        Check("close: the preferences are unbound and the name is hidden again", !ProjectScope.IsBound && entry.Name == "Project 1" && !entry.NameRevealed && !ReviewAllowList.Current.IsProjectBound);

        var listed = ProjectCatalog.Load();
        Check("list: a fresh start shows the neutral label", listed.Count == 1 && listed[0].Name == "Project 1");
        var opened = UiProjectSession.Open(listed[0], listed[0].FilePath, Pass);
        Check("open: unlocking reveals the real name and binds the project", listed[0].Name == "Fictional Quarterly Review" && ProjectScope.IsBound && opened.LoadedFormat == ProjectFormat.Dsp2);

        // ---- keep as-is is per project ---------------------------------------------------------------------
        ReviewAllowList.Current.Add("Boarding Pass", new[] { "PRODUCT" }, null);
        ProjectStore.Save(listed[0].FilePath, opened);
        UiProjectSession.Close(listed[0]);
        Check("keep: after closing the label no longer applies anywhere", !ReviewAllowList.Current.Contains("Boarding Pass", "PRODUCT"));
        var again = UiProjectSession.Open(listed[0], listed[0].FilePath, Pass);
        Check("keep: it comes back, saved inside the project, when the project is opened again", ReviewAllowList.Current.Contains("Boarding Pass", "PRODUCT") && again.ReviewPreferences.KeepAsIsSnapshot().Count == 1);

        // A second project must not see it.
        var otherPath = ProjectCatalog.NewProjectPath();
        var other = Project.New("Second Fictional Project", Pass);
        ProjectStore.Save(otherPath, other);
        var otherEntry = new ProjectEntry { FilePath = otherPath };
        UiProjectSession.Close(listed[0]);
        UiProjectSession.Adopt(otherEntry, other);
        Check("keep: another project does not inherit the label", !ReviewAllowList.Current.Contains("Boarding Pass", "PRODUCT"));

        // ---- generic terms, the suppressed list ----------------------------------------------------------------
        other.ReviewPreferences.AddGenericTerm("Treasury Desk", new[] { "ORGANIZATION" });
        Check("generic: a term added in the open project is active at once", DocumentPrivacyPolicy.IsGeneric("Treasury Desk", "ORGANIZATION"));
        DocumentPrivacyPolicy.ClearSuppressed();
        Check("suppressed: the list starts empty after a clear", DocumentPrivacyPolicy.SuppressedOrganizations.Count == 0);
        DocumentPrivacyPolicy.IsGeneric("Treasury Desk", "ORGANIZATION");
        Check("suppressed: a hidden organization is listed with its category", DocumentPrivacyPolicy.SuppressedOrganizations.Any(s => s.Value == "Treasury Desk" && s.Category == "ORGANIZATION"));
        other.ReviewPreferences.RemoveGenericTerm("Treasury Desk");
        Check("generic: removing the term takes effect at once", !DocumentPrivacyPolicy.IsGeneric("Treasury Desk", "ORGANIZATION"));
        UiProjectSession.Close(otherEntry);
        Check("generic: closing the project removes its terms", !DocumentPrivacyPolicy.IsGeneric("Treasury Desk", "ORGANIZATION"));

        // ---- legacy keep-as-is import (once per project) ------------------------------------------------------------
        var legacy = Path.Combine(work, "keep-as-is-labels.json");
        File.WriteAllText(legacy, JsonSerializer.Serialize(new { version = 1, labels = new[] { "Boarding Pass", "Orion Portal", "a@b.com", "Build 12" } }));
        var fresh = Project.New("Legacy import test", Pass);
        var offered = UiProjectSession.LegacyLabelsToOffer(fresh, legacy);
        Check("legacy: only acceptable labels are offered", offered.Count == 2 && offered.Contains("Boarding Pass") && offered.Contains("Orion Portal"));
        Check("legacy: no file means no offer", UiProjectSession.LegacyLabelsToOffer(fresh, Path.Combine(work, "missing.json")).Count == 0);
        var imported = UiProjectSession.AnswerLegacyOffer(fresh, offered, true);
        Check("legacy: Yes imports into this project with the default categories", imported == 2 && fresh.ReviewPreferences.LegacyImportDecision == "Imported" && fresh.ReviewPreferences.MatchesKeepAsIs("Orion Portal", "SYSTEM"));
        Check("legacy: once answered, it is not offered again", UiProjectSession.LegacyLabelsToOffer(fresh, legacy).Count == 0);
        var declined = Project.New("Legacy decline test", Pass);
        Check("legacy: No imports nothing and is remembered", UiProjectSession.AnswerLegacyOffer(declined, UiProjectSession.LegacyLabelsToOffer(declined, legacy), false) == 0 && declined.ReviewPreferences.LegacyImportDecision == "Declined" && declined.ReviewPreferences.KeepAsIsSnapshot().Count == 0 && UiProjectSession.LegacyLabelsToOffer(declined, legacy).Count == 0);
        Check("legacy: the old file is never changed or deleted", File.Exists(legacy) && File.ReadAllText(legacy).Contains("Orion Portal"));
        var legacyPath = Path.Combine(work, "legacy-roundtrip.dsproj");
        ProjectStore.Save(legacyPath, fresh);
        Check("legacy: the answer survives a save and load", ProjectStore.Load(legacyPath, Pass).ReviewPreferences.LegacyImportDecision == "Imported");

        // ---- rename inside the encrypted project ------------------------------------------------------------------------
        var renamed = ProjectStore.Load(path, Pass);
        renamed.Name = "Renamed Fictional Review";
        ProjectStore.Save(path, renamed);
        var entryAfter = ProjectCatalog.Load().First(e => e.FilePath == path);
        ProjectCatalog.RevealName(entryAfter, ProjectStore.Load(path, Pass));
        ProjectCatalog.Save(new[] { entryAfter, otherEntry });
        Check("rename: the new name is inside the project and the catalog still holds none", entryAfter.Name == "Renamed Fictional Review" && !File.ReadAllText(Path.Combine(root, "projects.json")).Contains("Renamed"));

        // ---- the neutral label edit ----------------------------------------------------------------------------------------
        var labelEntry = ProjectCatalog.Load().First(e => e.FilePath == path);
        labelEntry.Label = "Team workspace";
        ProjectCatalog.Save(ProjectCatalog.Load().Select(e => e.FilePath == path ? labelEntry : e));
        Check("label: an edited neutral label is saved and reloaded", ProjectCatalog.Load().Any(e => e.FilePath == path && e.Label == "Team workspace" && e.Name == "Team workspace"));

        // ---- old catalog opens and is converted --------------------------------------------------------------------------------
        var oldRoot = Path.Combine(work, "old-app-data");
        ProjectCatalog.UseRoot(oldRoot);
        var oldPath = Path.Combine(oldRoot, "somewhere", "old-project.dsproj");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        ProjectStore.Save(oldPath, Project.New("Old catalog project", Pass));
        File.WriteAllText(Path.Combine(oldRoot, "projects.json"), JsonSerializer.Serialize(new[] { new { Id = "abc123", Name = "Real Client Name Review", FilePath = oldPath, LastOpened = DateTimeOffset.UtcNow } }));
        var migrated = ProjectCatalog.Load();
        Check("old catalog: the entry opens with a neutral label", migrated.Count == 1 && migrated[0].Name == "Project 1" && migrated[0].FilePath == oldPath);
        Check("old catalog: the plaintext name is gone from disk straight away", !File.ReadAllText(Path.Combine(oldRoot, "projects.json")).Contains("Real Client Name") && !File.ReadAllText(Path.Combine(oldRoot, "projects.json")).Contains("somewhere"));
        UiProjectSession.Open(migrated[0], oldPath, Pass);
        Check("old catalog: unlocking shows the real name from inside the project", migrated[0].Name == "Old catalog project");
        UiProjectSession.Close(migrated[0]);

        // ---- a catalog row lost: managed projects are recovered -----------------------------------------------------------------
        var lostRoot = Path.Combine(work, "lost-app-data");
        ProjectCatalog.UseRoot(lostRoot);
        var lostPath = ProjectCatalog.NewProjectPath();
        ProjectStore.Save(lostPath, Project.New("Lost row project", Pass));
        var recovered = UiProjectSession.RecoverUncataloged(Array.Empty<ProjectEntry>());
        Check("recover: a project file with no catalog row is found with a neutral label", recovered.Count == 1 && recovered[0].FilePath == lostPath && recovered[0].Name.StartsWith("Project "));

        // ---- deleting also removes the backup ----------------------------------------------------------------------------------
        var delPath = Path.Combine(work, "to-delete.dsproj");
        File.WriteAllText(delPath, "x"); File.WriteAllText(delPath + ".bak", "y");
        UiProjectSession.DeleteFiles(delPath);
        Check("delete: the project file and its backup are removed", !File.Exists(delPath) && !File.Exists(delPath + ".bak"));

        ProjectCatalog.UseRoot(Path.Combine(work, "app-data"));
        ProjectScope.Unbind();
    }
}

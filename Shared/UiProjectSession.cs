using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Desensitizer;

// What happens around opening and closing a project, in one place so the order is the same everywhere and
// can be tested: unlock, bind the project's review preferences to the detectors, reveal the real name in
// memory; close, unbind and hide the name again.
public static class UiProjectSession
{
    // Unlocks the file. Throws what ProjectStore.Load throws (wrong passphrase, damaged file).
    public static Project Open(ProjectEntry? entry, string filePath, string passphrase)
    {
        var project = ProjectStore.Load(filePath, passphrase);
        project.EnsureFileProvenance();
        foreach (var file in project.Files) file.Status = UiMessages.PlainStatus(file.Status);
        ProjectScope.Bind(project);
        if (entry != null)
        {
            ProjectCatalog.RevealName(entry, project);
            entry.LastOpened = DateTimeOffset.UtcNow;
        }
        return project;
    }

    // A project that was just created and saved.
    public static void Adopt(ProjectEntry entry, Project project)
    {
        ProjectScope.Bind(project);
        ProjectCatalog.RevealName(entry, project);
        entry.LastOpened = DateTimeOffset.UtcNow;
    }

    public static void Close(ProjectEntry? entry)
    {
        ProjectScope.Unbind();
        if (entry != null) ProjectCatalog.HideName(entry);
    }

    // The old global keep-as-is labels to offer for this project, or none when the question was already answered.
    public static IReadOnlyList<string> LegacyLabelsToOffer(Project project, string legacyFilePath)
    {
        if (!string.IsNullOrEmpty(project.ReviewPreferences.LegacyImportDecision)) return Array.Empty<string>();
        return LegacyKeepAsIsFile.Read(legacyFilePath);
    }

    // The user answered the prompt. Returns the number of labels imported.
    public static int AnswerLegacyOffer(Project project, IReadOnlyList<string> offered, bool import)
    {
        if (!import) { project.ReviewPreferences.DeclineLegacyImport(); return 0; }
        return project.ReviewPreferences.ImportLegacyLabels(offered);
    }

    // Deleting a project removes the file and the backup the safe save may have left beside it.
    public static void DeleteFiles(string filePath)
    {
        File.Delete(filePath);
        var backup = ProjectStore.BackupPath(filePath);
        if (File.Exists(backup)) File.Delete(backup);
    }

    // Managed-folder projects with no catalog row, as entries ready to add to the list (catalog repair).
    public static List<ProjectEntry> RecoverUncataloged(IEnumerable<ProjectEntry> known) =>
        ProjectCatalog.FindUncataloged(known);
}

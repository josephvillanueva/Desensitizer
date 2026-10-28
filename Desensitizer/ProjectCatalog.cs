using System;
using System.Collections.Generic;
using System.IO;

namespace Desensitizer
{
 // The recent-project list. The storage format, the migration from the old plaintext catalog and the atomic
 // write are in Shared/ProjectCatalogCore.cs. projects.json holds only an opaque id, a neutral label and the
 // storage location, never the real project name or source paths (those are inside the encrypted project).
 //
 // For the interface:
 //   ProjectCatalog.Load()                      entries show their neutral label ("Project 1") in Name
 //   ProjectCatalog.RevealName(entry, project)  after unlock, shows the real name in memory only
 //   ProjectCatalog.HideName(entry)             when the project is closed
 //   ProjectCatalog.Save(entries)               never writes Name; an empty Label gets the next "Project N"
 public static class ProjectCatalog
 {
  private const string AppFolder="Desensitizer";
  private static string? rootOverride;
  public static string Root => rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder);
  public static string ProjectsFolder => ProjectCatalogStorage.ProjectsFolder(Root);

  // Test seam: use another folder instead of the user's local-data folder. Pass null to restore the default.
  public static void UseRoot(string? folder) => rootOverride = folder;

  public static List<ProjectEntry> Load() => ProjectCatalogStorage.Load(Root);
  public static void Save(IEnumerable<ProjectEntry> entries) => ProjectCatalogStorage.Save(Root, entries);
  public static string NewProjectPath() => ProjectCatalogStorage.NewProjectPath(Root);
  public static string NextLabel(IEnumerable<ProjectEntry> existing) => ProjectCatalogStorage.NextLabel(existing);
  public static void RevealName(ProjectEntry entry, Project unlocked) => ProjectCatalogStorage.RevealName(entry, unlocked);
  public static void HideName(ProjectEntry entry) => ProjectCatalogStorage.HideName(entry);
  public static string AdoptIntoManagedFolder(ProjectEntry entry) => ProjectCatalogStorage.AdoptIntoManagedFolder(Root, entry);
  public static List<ProjectEntry> FindUncataloged(IEnumerable<ProjectEntry> known) => ProjectCatalogStorage.FindUncataloged(Root, known);
 }
}

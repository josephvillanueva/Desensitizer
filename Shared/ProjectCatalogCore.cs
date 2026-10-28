using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Desensitizer;

// One row of the recent-project list.
//
// What reaches projects.json: an opaque Id, a neutral Label such as "Project 1", the last-opened time and the
// storage location (a file name inside the managed Projects folder, or for a project stored elsewhere the
// path sealed with the Windows account's data protection). The real project name and the source documents'
// paths live only inside the encrypted project. Name is the display text in memory: it starts as the Label and
// becomes the real name after ProjectCatalog.RevealName(entry, unlockedProject). It is never written.
public sealed class ProjectEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    // Neutral, caller chosen. Empty means "assign the next free 'Project N' when the catalog is saved".
    public string Label { get; set; } = "";
    [JsonIgnore] public string Name { get; set; } = "Untitled project";
    // Absolute path, resolved from the stored location when the catalog is loaded. Never written as is.
    [JsonIgnore] public string FilePath { get; set; } = "";
    public DateTimeOffset LastOpened { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public bool NameRevealed { get; set; }
    [JsonIgnore] public string LastOpenedLabel => "Last opened " + LastOpened.LocalDateTime.ToString("MMM d, yyyy 'at' h:mm tt");
}

// The shared implementation behind each app's ProjectCatalog. Two on-disk formats exist:
//   version 1 (old): a JSON array of {Id, Name, FilePath, LastOpened}. Names and full paths in plaintext.
//   version 2 (now): {"version":2,"entries":[{"id","label","location"|"protectedLocation","lastOpened"}]}
// Load reads either. An old file is converted and rewritten in the new format on first load, and no copy of
// the old plaintext is kept (the .bak is removed or never created from it).
public static class ProjectCatalogStorage
{
    public const int CurrentVersion = 2;
    private const string ManagedPrefix = "Projects/";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Desensitizer project catalog location v2");

    // Test seam: called with "temp-written" before the new catalog replaces the old one.
    public static Action<string>? FaultHook;

    public static string CatalogPath(string root) => Path.Combine(root, "projects.json");
    public static string ProjectsFolder(string root) => Path.Combine(root, "Projects");

    public static List<ProjectEntry> Load(string root)
    {
        try
        {
            var path = CatalogPath(root);
            var rows = TryRead(path, root, out var wasLegacy);
            if (rows == null) rows = TryRead(path + ".bak", root, out wasLegacy);
            if (rows == null) return new List<ProjectEntry>();
            AssignLabels(rows);
            if (wasLegacy)
            {
                // Rewrite in the new format now, so the plaintext names and paths leave the disk as early as possible.
                try { Save(root, rows); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
            }
            // Missing does not mean deleted: an external drive or share can be
            // temporarily unavailable. Keep safely decoded rows until the user
            // explicitly removes them, including across a later catalog save.
            return rows.OrderByDescending(x => x.LastOpened).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return new List<ProjectEntry>(); }
    }

    public static void Save(string root, IEnumerable<ProjectEntry> entries)
    {
        var list = entries.ToList();
        AssignLabels(list);
        var rows = new JsonArray();
        foreach (var entry in list)
        {
            if (string.IsNullOrWhiteSpace(entry.FilePath)) continue;
            var row = new JsonObject { ["id"] = entry.Id, ["label"] = entry.Label, ["lastOpened"] = entry.LastOpened.ToString("O") };
            var managed = RelativeLocation(root, entry.FilePath);
            if (managed != null) row["location"] = managed;
            else
            {
                try { row["protectedLocation"] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Path.GetFullPath(entry.FilePath)), Entropy, DataProtectionScope.CurrentUser)); }
                catch (CryptographicException) { continue; } // cannot be stored safely: skip it, the user can open the file again
            }
            rows.Add(row);
        }
        var document = new JsonObject { ["version"] = CurrentVersion, ["entries"] = rows };
        Directory.CreateDirectory(root);
        var path = CatalogPath(root);
        var backup = path + ".bak";
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                fs.Write(bytes);
                fs.Flush(true);
            }
            FaultHook?.Invoke("temp-written");
            if (File.Exists(path))
            {
                // Never copy an old plaintext catalog into the backup.
                if (IsLegacyFile(path)) File.Replace(temporary, path, null, true);
                else
                {
                    try { File.Replace(temporary, path, backup, true); }
                    catch (PlatformNotSupportedException) { File.Copy(path, backup, true); File.Move(temporary, path, true); }
                }
            }
            else File.Move(temporary, path);
            if (File.Exists(backup) && IsLegacyFile(backup)) File.Delete(backup);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // The label "Project N" with the smallest free N. Callers may set their own neutral label instead.
    public static string NextLabel(IEnumerable<ProjectEntry> existing)
    {
        var used = new HashSet<string>(existing.Select(e => e.Label), StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++) if (!used.Contains("Project " + n)) return "Project " + n;
    }

    public static string NewProjectPath(string root)
    {
        Directory.CreateDirectory(ProjectsFolder(root));
        return Path.Combine(ProjectsFolder(root), Guid.NewGuid().ToString("N") + ".dsproj");
    }

    // Shows the real name once the project is unlocked. Memory only.
    public static void RevealName(ProjectEntry entry, Project unlocked)
    {
        if (!string.IsNullOrWhiteSpace(unlocked.Name)) { entry.Name = unlocked.Name; entry.NameRevealed = true; }
    }

    public static void HideName(ProjectEntry entry)
    {
        entry.Name = string.IsNullOrWhiteSpace(entry.Label) ? "Untitled project" : entry.Label;
        entry.NameRevealed = false;
    }

    // Copies an encrypted project stored outside the managed folder into it, so the catalog no longer needs an
    // outside path. The original file is left in place. Returns the new path and updates the entry.
    public static string AdoptIntoManagedFolder(string root, ProjectEntry entry)
    {
        if (RelativeLocation(root, entry.FilePath) != null) return entry.FilePath;
        var target = NewProjectPath(root);
        File.Copy(entry.FilePath, target, false);
        entry.FilePath = target;
        return target;
    }

    // Managed-folder projects with no catalog row (for example after a damaged catalog), as new entries.
    public static List<ProjectEntry> FindUncataloged(string root, IEnumerable<ProjectEntry> known)
    {
        var have = new HashSet<string>(known.Select(k => Path.GetFullPath(k.FilePath)), StringComparer.OrdinalIgnoreCase);
        var found = new List<ProjectEntry>();
        var folder = ProjectsFolder(root);
        if (!Directory.Exists(folder)) return found;
        foreach (var file in Directory.EnumerateFiles(folder, "*.dsproj").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            if (!have.Contains(Path.GetFullPath(file)))
                found.Add(new ProjectEntry { FilePath = file, LastOpened = File.GetLastWriteTimeUtc(file) });
        AssignLabels(known.Concat(found).ToList());
        return found;
    }

    // ---- internals ------------------------------------------------------------------------

    private static void AssignLabels(List<ProjectEntry> entries)
    {
        foreach (var entry in entries.Where(e => string.IsNullOrWhiteSpace(e.Label)).ToList())
        {
            entry.Label = NextLabel(entries);
            if (!entry.NameRevealed && (entry.Name == "Untitled project" || string.IsNullOrWhiteSpace(entry.Name))) entry.Name = entry.Label;
        }
    }

    private static string? RelativeLocation(string root, string filePath)
    {
        try
        {
            var folder = Path.GetFullPath(ProjectsFolder(root)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(filePath);
            if (full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && full.IndexOf(Path.DirectorySeparatorChar, folder.Length) < 0)
                return ManagedPrefix + full.Substring(folder.Length);
        }
        catch (ArgumentException) { }
        return null;
    }

    private static bool IsLegacyFile(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }

    // null: missing or unreadable. Otherwise the entries, with paths resolved and labels assigned for old rows.
    private static List<ProjectEntry>? TryRead(string path, string root, out bool wasLegacy)
    {
        wasLegacy = false;
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var rows = new List<ProjectEntry>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                wasLegacy = true;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var file = Text(item, "FilePath");
                    if (string.IsNullOrWhiteSpace(file)) continue;
                    var entry = new ProjectEntry { FilePath = file };
                    var id = Text(item, "Id"); if (!string.IsNullOrWhiteSpace(id)) entry.Id = id;
                    if (item.TryGetProperty("LastOpened", out var opened) && opened.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(opened.GetString(), out var when)) entry.LastOpened = when;
                    // The old plaintext name is deliberately dropped: Label stays empty so a neutral one is assigned.
                    entry.Name = "Untitled project";
                    rows.Add(entry);
                }
                return rows;
            }
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? resolved = null;
                var location = Text(item, "location");
                if (!string.IsNullOrEmpty(location) && location.StartsWith(ManagedPrefix, StringComparison.Ordinal))
                {
                    var name = location.Substring(ManagedPrefix.Length);
                    if (name.Length > 0 && name.IndexOfAny(new[] { '/', '\\' }) < 0 && name != ".." && name != ".") resolved = Path.Combine(ProjectsFolder(root), name);
                }
                var sealedLocation = Text(item, "protectedLocation");
                if (resolved == null && !string.IsNullOrEmpty(sealedLocation))
                {
                    try { resolved = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(sealedLocation), Entropy, DataProtectionScope.CurrentUser)); }
                    catch (CryptographicException) { }
                    catch (FormatException) { }
                }
                if (resolved == null) continue;
                var entry = new ProjectEntry { FilePath = resolved, Label = Text(item, "label") ?? "" };
                var id = Text(item, "id"); if (!string.IsNullOrWhiteSpace(id)) entry.Id = id;
                if (item.TryGetProperty("lastOpened", out var opened) && opened.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(opened.GetString(), out var when)) entry.LastOpened = when;
                entry.Name = string.IsNullOrWhiteSpace(entry.Label) ? "Untitled project" : entry.Label;
                rows.Add(entry);
            }
            return rows;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

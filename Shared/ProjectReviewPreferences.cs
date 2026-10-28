using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Review preferences that belong to ONE project and are stored inside its encrypted data.
// A project saved before this existed has no such field and loads as empty preferences.
//
//  Keep as-is labels: a business label the user decided is not identifying. An entry only
//  applies to the categories it lists (never a person, company, contact, place or identifier),
//  and, when it has a context hint, only where that hint appears in the surrounding text.
//  Generic terms: ordinary vocabulary of this project's documents (a department name, a product
//  family) that detectors should not report. The user can see and remove each one, and every
//  organization or vendor candidate such a term hides is listed by DocumentPrivacyPolicy.
public sealed class KeepAsIsEntry
{
    public string Value { get; set; } = "";
    public List<string> Categories { get; set; } = new();
    public string ContextHint { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectGenericTerm
{
    public string Value { get; set; } = "";
    // Empty means every category that is allowed for generic terms.
    public List<string> Categories { get; set; } = new();
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectReviewPreferences
{
    public const int CurrentVersion = 1;
    // Categories where a saved label may ever apply. Person, company, contact, place and identifier
    // values are never silenced by spelling alone.
    public static readonly IReadOnlyList<string> KeepAsIsCategories = new[] { "PROJECT", "SYSTEM", "PRODUCT" };
    // Categories a project generic term must never apply to.
    private static readonly HashSet<string> NeverGeneric = new(StringComparer.OrdinalIgnoreCase)
    { "PERSON", "EMAIL", "PHONE", "ADDRESS", "URL", "IDENTIFIER", "ACCOUNT", "DATE" };

    public int Version { get; set; } = CurrentVersion;
    public List<KeepAsIsEntry> KeepAsIs { get => keepAsIs; set => keepAsIs = value ?? new(); }
    public List<ProjectGenericTerm> GenericTerms { get => genericTerms; set => genericTerms = value ?? new(); }
    // "", "Imported" or "Declined": remembers the answer about the old global keep-as-is file.
    public string LegacyImportDecision { get; set; } = "";

    private List<KeepAsIsEntry> keepAsIs = new();
    private List<ProjectGenericTerm> genericTerms = new();
    private readonly object sync = new();

    // Raised after any change made through the methods below (not for direct list edits).
    public event Action? Changed;

    public static bool IsAcceptableLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.Length < 2 || trimmed.Length > 120 || trimmed.Contains('@') || trimmed.Contains("://") || trimmed.Any(char.IsDigit)) return false;
        if (Regex.IsMatch(trimmed, @"\[[A-Z_]+_\d{3,}\]")) return false;
        return trimmed.Any(char.IsLetter);
    }

    public static string KeyOf(string value) => Regex.Replace(value.Trim(), @"\s+", " ").ToLowerInvariant();
    public static string DisplayOf(string value) => Regex.Replace(value.Trim(), @"\s+", " ");

    [JsonIgnore] public bool IsSupportedVersion => Version >= 1 && Version <= CurrentVersion;

    // ---- keep as-is -------------------------------------------------------------------------
    // categories null or empty means the default set (PROJECT, SYSTEM, PRODUCT). Any category outside that
    // set refuses the entry. A context hint limits the entry to text that contains the hint.
    public bool AddKeepAsIs(string? value, IEnumerable<string>? categories = null, string? contextHint = null)
    {
        if (!IsAcceptableLabel(value)) return false;
        var wanted = (categories ?? Array.Empty<string>()).Select(c => (c ?? "").Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (wanted.Count == 0) wanted = KeepAsIsCategories.ToList();
        if (wanted.Any(c => !KeepAsIsCategories.Contains(c))) return false;
        var hint = string.IsNullOrWhiteSpace(contextHint) ? "" : KeyOf(contextHint);
        if (hint.Length > 200) return false;
        lock (sync)
        {
            var key = KeyOf(value!);
            var existing = keepAsIs.FirstOrDefault(e => KeyOf(e.Value) == key && e.ContextHint == hint);
            if (existing != null) existing.Categories = existing.Categories.Concat(wanted).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
            else keepAsIs.Add(new KeepAsIsEntry { Value = DisplayOf(value!), Categories = wanted, ContextHint = hint });
        }
        Changed?.Invoke();
        return true;
    }

    // Explicitly narrower than the existing project-wide remembered-label action.
    // This applies only to AI suggestions in one allowed category near the chosen
    // phrase. It does not turn a direct Keep decision into occurrence-level redaction.
    public bool AddContextKeepAsIs(string? value, string? category, string? contextHint)
    {
        if (!IsSupportedVersion || !ContextKeepPolicy.IsValidRule(value, category, contextHint)) return false;
        return AddKeepAsIs(value, new[] { category!.Trim().ToUpperInvariant() }, contextHint);
    }

    public bool RemoveKeepAsIs(string value, string? contextHint = null)
    {
        int removed;
        lock (sync)
        {
            var key = KeyOf(value);
            var hint = string.IsNullOrWhiteSpace(contextHint) ? "" : KeyOf(contextHint);
            removed = keepAsIs.RemoveAll(e => KeyOf(e.Value) == key && e.ContextHint == hint);
        }
        if (removed > 0) Changed?.Invoke();
        return removed > 0;
    }

    // context: text around the occurrence (a sentence, row or heading). null means unknown, and an entry
    // that needs a context hint then never matches.
    public bool MatchesKeepAsIs(string? value, string? category, string? context = null)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(category)) return false;
        var cat = category!.Trim().ToUpperInvariant();
        if (!KeepAsIsCategories.Contains(cat)) return false;
        var key = KeyOf(value!);
        var folded = context == null ? null : KeyOf(context);
        lock (sync)
        {
            foreach (var entry in keepAsIs)
            {
                if (KeyOf(entry.Value) != key || !entry.Categories.Contains(cat, StringComparer.OrdinalIgnoreCase)) continue;
                if (entry.ContextHint.Length == 0 || (folded != null && folded.Contains(entry.ContextHint))) return true;
            }
        }
        return false;
    }

    public IReadOnlyList<KeepAsIsEntry> KeepAsIsSnapshot()
    {
        lock (sync) return keepAsIs.Select(e => new KeepAsIsEntry { Value = e.Value, Categories = e.Categories.ToList(), ContextHint = e.ContextHint, AddedAt = e.AddedAt }).ToList();
    }

    // ---- generic terms ----------------------------------------------------------------------
    public bool AddGenericTerm(string? value, IEnumerable<string>? categories = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value!.Trim();
        if (trimmed.Length < 2 || trimmed.Length > 120 || trimmed.Contains('@') || trimmed.Contains("://") || trimmed.Any(char.IsDigit) || !trimmed.Any(char.IsLetter)) return false;
        if (Regex.IsMatch(trimmed, @"\[[A-Z_]+_\d{3,}\]")) return false;
        var wanted = (categories ?? Array.Empty<string>()).Select(c => (c ?? "").Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (wanted.Any(c => NeverGeneric.Contains(c))) return false;
        lock (sync)
        {
            var key = KeyOf(trimmed);
            var existing = genericTerms.FirstOrDefault(e => KeyOf(e.Value) == key);
            if (existing != null) existing.Categories = existing.Categories.Count == 0 || wanted.Count == 0 ? new List<string>() : existing.Categories.Concat(wanted).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
            else genericTerms.Add(new ProjectGenericTerm { Value = DisplayOf(trimmed), Categories = wanted });
        }
        Changed?.Invoke();
        return true;
    }

    public bool RemoveGenericTerm(string value)
    {
        int removed;
        lock (sync) { var key = KeyOf(value); removed = genericTerms.RemoveAll(e => KeyOf(e.Value) == key); }
        if (removed > 0) Changed?.Invoke();
        return removed > 0;
    }

    public static bool GenericTermAppliesTo(string category) => !NeverGeneric.Contains((category ?? "").Trim());

    public IReadOnlyList<ProjectGenericTerm> GenericTermsSnapshot()
    {
        lock (sync) return genericTerms.Select(e => new ProjectGenericTerm { Value = e.Value, Categories = e.Categories.ToList(), AddedAt = e.AddedAt }).ToList();
    }

    // ---- the old global file ----------------------------------------------------------------
    // Imports labels read from the old global keep-as-is file. Only call this after the user agreed.
    // Old entries carry no category, so they get the default set. Anything unacceptable is skipped.
    public int ImportLegacyLabels(IEnumerable<string> labels)
    {
        var imported = 0;
        foreach (var label in labels) if (AddKeepAsIs(label)) imported++;
        LegacyImportDecision = "Imported";
        return imported;
    }

    public void DeclineLegacyImport() { LegacyImportDecision = "Declined"; }
}

// Read-only access to the old global, plaintext, unauthenticated keep-as-is file
// ({"version":1,"labels":[...]} in the app's local-data folder). Nothing in the apps applies it
// any more. The interface may show its entries and, if the user agrees, import them into the open
// project with ProjectReviewPreferences.ImportLegacyLabels. This class never writes or deletes.
public static class LegacyKeepAsIsFile
{
    public static string DefaultPath(string appFolderName) => ReviewAllowList.DefaultPath(appFolderName);

    public static IReadOnlyList<string> Read(string path)
    {
        var labels = new List<string>();
        try
        {
            if (!File.Exists(path)) return labels;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("labels", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var item in array.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && ProjectReviewPreferences.IsAcceptableLabel(item.GetString())) labels.Add(ProjectReviewPreferences.DisplayOf(item.GetString()!));
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return labels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

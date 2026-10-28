using System;
using System.Collections.Generic;
using System.Linq;

namespace Desensitizer;

// A resumable record of a document check, stored INSIDE the encrypted project (Project.ReviewCheckpoints).
// Entries hold the reviewed section text, so they are confidential. They are only ever written as part of
// the encrypted project file, never to a plain file.
public sealed class ReviewCheckpoint
{
    public int Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    // Exactly what ExportReviewCache.Export() returns.
    private Dictionary<string, string> entries = new();
    public Dictionary<string, string> Entries { get => entries; set => entries = value ?? new(); }
    // Reviewer sections in the whole run (all reviewers) and how many have been reported as started.
    public int SectionsTotal { get; set; }
    public int SectionsStarted { get; set; }
    // Sections that could not be reviewed. Never contains document text.
    private List<SectionFailure> failed = new();
    public List<SectionFailure> Failed { get => failed; set => failed = UiResume.NormalizeFailures(value); }
}

// Collects the entries of one run as they are stored, from any thread, and writes them into the project's
// checkpoint. Create one per run: Restore (optional), Attach, run the review, then Commit or Discard.
public sealed class UiCheckpointRecorder
{
    private readonly object sync = new();
    private readonly Dictionary<string, string> entries = new();
    private int total;
    private int started;
    private long revision;
    private long committedRevision;

    public int Count { get { lock (sync) return entries.Count; } }
    public int Total { get { lock (sync) return total; } }
    public long Revision { get { lock (sync) return revision; } }
    public bool HasUncommittedChanges { get { lock (sync) return revision != committedRevision; } }

    // Loads a saved checkpoint into the cache. Returns how many entries the cache accepted (damaged or
    // oversized entries are ignored by the cache and simply reviewed again).
    public int Restore(ReviewCheckpoint? saved, ExportReviewCache cache)
    {
        if (saved == null || saved.Entries.Count == 0) return 0;
        var accepted = cache.Import(saved.Entries, saved.Version);
        if (accepted == 0) return 0;
        // Never retain rejected entries in a second unbounded collection. Import
        // already validates shape, version, entry sizes and the cache budget.
        var held = cache.Export();
        lock (sync)
        {
            foreach (var pair in held)
                if (saved.Entries.TryGetValue(pair.Key, out var original) && original == pair.Value)
                    entries[pair.Key] = pair.Value;
            total = Math.Max(total, Math.Max(0, saved.SectionsTotal));
            started = Math.Max(started, Math.Max(0, saved.SectionsStarted));
            if (total > 0) started = Math.Min(started, total);
            revision++;
        }
        return accepted;
    }

    // Every section the cache stores from now on is recorded here as well.
    public void Attach(ExportReviewCache cache)
    {
        attached = cache;
        cache.OnEntryStored = (key, entry) => { lock (sync) { entries[key] = entry; revision++; } };
    }

    private ExportReviewCache? attached;

    public void ReportProgress(int step, int totalSections)
    {
        lock (sync)
        {
            if (totalSections > total) total = totalSections;
            if (step > started) started = step;
        }
    }

    // Writes the recorded state into the project. failures: sections that could not be checked in this run.
    public ReviewCheckpoint Commit(IEnumerable<SectionFailure>? failures = null)
    {
        // Only entries the cache still holds are kept: an entry it refused (damaged, or over its limits) is dropped
        // here instead of being carried from run to run.
        Dictionary<string, string>? held = null;
        try { held = attached?.Export(); }
        catch (ObjectDisposedException) { }
        lock (sync)
        {
            committedRevision = revision;
            return new ReviewCheckpoint
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Entries = held != null ? new Dictionary<string, string>(held) : new Dictionary<string, string>(entries),
                SectionsTotal = total,
                SectionsStarted = started,
                Failed = UiResume.NormalizeFailures(failures)
            };
        }
    }
}

public sealed record CheckpointPruneResult(int InvalidRemoved, int Evicted, int RetainedCount,
    long RetainedCharacters, bool ActiveEvicted)
{
    public int RemovedCount => InvalidRemoved + Evicted;
}

// Checkpoints are optional acceleration, never evidence of a passed scan. Evicting
// one only means its sections must be reviewed again. No FileRecord completion
// state or approved mapping is changed here. Character counts cover the stored
// checkpoint key and each serialized entry key/value, not byte lengths on disk.
public static class UiCheckpointPruner
{
    public const int MaxCheckpoints = 32;
    public const int MaxRetainedCharacters = 16 * 1024 * 1024;

    public static CheckpointPruneResult Prune(IDictionary<string, ReviewCheckpoint> checkpoints,
        IEnumerable<string> fileIds, string? activeKey = null,
        int maxCheckpoints = MaxCheckpoints, int maxCharacters = MaxRetainedCharacters)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        if (maxCheckpoints < 1) throw new ArgumentOutOfRangeException(nameof(maxCheckpoints));
        if (maxCharacters < 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in fileIds ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            allowed.Add(id); allowed.Add(id + "#deep"); allowed.Add(id + "#export");
        }
        var invalid = 0;
        var evicted = 0;
        var activeEvicted = false;
        foreach (var pair in checkpoints.ToArray())
        {
            if (allowed.Contains(pair.Key) && pair.Value != null &&
                pair.Value.Version == ExportReviewCache.SnapshotVersion && UiResume.HasWork(pair.Value)) continue;
            checkpoints.Remove(pair.Key); invalid++;
            activeEvicted |= pair.Key == activeKey;
        }
        var sizes = checkpoints.ToDictionary(p => p.Key, p => Size(p.Key, p.Value), StringComparer.Ordinal);
        // A single over-budget snapshot cannot be retained, active or otherwise.
        // Remove it before considering smaller useful snapshots for eviction.
        foreach (var key in sizes.Where(p => p.Value > maxCharacters).Select(p => p.Key).ToArray())
        {
            checkpoints.Remove(key); sizes.Remove(key); evicted++;
            activeEvicted |= key == activeKey;
        }
        var retained = sizes.Values.Sum();
        // Active state wins over older state, but not over the hard memory budget.
        // Stable tie ordering makes repeated pruning deterministic.
        foreach (var key in checkpoints.Where(p => p.Key != activeKey)
            .OrderBy(p => p.Value.UpdatedAt).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToArray())
        {
            if (checkpoints.Count <= maxCheckpoints && retained <= maxCharacters) break;
            retained -= sizes[key]; checkpoints.Remove(key); evicted++;
        }
        if ((checkpoints.Count > maxCheckpoints || retained > maxCharacters) &&
            activeKey != null && checkpoints.Remove(activeKey))
        {
            retained -= sizes[activeKey]; evicted++; activeEvicted = true;
        }
        return new(invalid, evicted, checkpoints.Count, retained, activeEvicted);
    }

    private static long Size(string key, ReviewCheckpoint checkpoint)
    {
        long size = key.Length;
        foreach (var entry in checkpoint.Entries) size += (long)entry.Key.Length + (entry.Value?.Length ?? 0);
        return size;
    }
}

public static class UiResume
{
    // These values are deserialized from an encrypted project, not a guarantee
    // that an older or damaged project supplied usable field values.
    public static List<SectionFailure> NormalizeFailures(IEnumerable<SectionFailure>? failures) =>
        (failures ?? Array.Empty<SectionFailure>())
            .Where(f => f != null && f.SectionIndex > 0 && f.SectionCount >= 0 &&
                (f.SectionCount == 0 || f.SectionIndex <= f.SectionCount))
            .Take(4096)
            .Select(f => new SectionFailure
            {
                Reviewer = string.IsNullOrWhiteSpace(f.Reviewer) || f.Reviewer.Length > 64 ||
                    f.Reviewer.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-'))
                    ? "Local AI" : f.Reviewer.Trim(),
                SectionIndex = f.SectionIndex, SectionCount = f.SectionCount,
                // Do not re-emit arbitrary legacy dependency messages from projects.
                Reason = string.IsNullOrEmpty(f.Reason) ? "" : "The local AI review needs to be repeated."
            }).GroupBy(f => (f.Reviewer, f.SectionIndex, f.SectionCount)).Select(g => g.First()).ToList();

    public static void RemoveFileCheckpoints(IDictionary<string, ReviewCheckpoint> checkpoints, string fileId)
    {
        // Exact legacy and current keys only. File ids with a common prefix must
        // never delete each other's private review state.
        checkpoints.Remove(fileId);
        checkpoints.Remove(fileId + "#deep");
        checkpoints.Remove(fileId + "#export");
    }

    public static int Done(ReviewCheckpoint checkpoint) =>
        checkpoint.SectionsTotal > 0 ? Math.Min(checkpoint.Entries.Count, checkpoint.SectionsTotal) : checkpoint.Entries.Count;

    public static bool HasWork(ReviewCheckpoint? checkpoint) => checkpoint != null && checkpoint.Version == ExportReviewCache.SnapshotVersion &&
        (checkpoint.Entries.Count > 0 || NormalizeFailures(checkpoint.Failed).Count > 0);

    public static string OfferText(ReviewCheckpoint checkpoint) =>
        "Resume previous check (" + Done(checkpoint) + " of " + Math.Max(checkpoint.SectionsTotal, Done(checkpoint)) + " sections done)";

    public static string OfferDetail(ReviewCheckpoint checkpoint) =>
        "A previous check of this document was interrupted. Resuming skips the " + Done(checkpoint) + " section" + (Done(checkpoint) == 1 ? "" : "s") +
        " that were already reviewed. The saved progress is kept inside the encrypted project.";

    // One line per reviewer, such as "Gemma: sections 3, 7 of 20". No document text.
    public static IReadOnlyList<string> FailedLines(IEnumerable<SectionFailure> failures) =>
        NormalizeFailures(failures).GroupBy(f => f.Reviewer).Select(g =>
            g.Key + ": section" + (g.Select(f => f.SectionIndex).Distinct().Count() == 1 ? " " : "s ") +
            string.Join(", ", g.Select(f => f.SectionIndex).Distinct().OrderBy(i => i)) +
            (g.First().SectionCount > 0 ? " of " + g.First().SectionCount : "")).ToList();

    private static readonly System.Text.RegularExpressions.Regex FailedGroup = new(
        @"^(?<reviewer>[A-Za-z0-9 ._-]{1,64}?) sections? (?<list>\d+(?:, \d+)*)(?: of (?<count>\d+))?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // The deep check result carries its failed sections as the sentence LocalReviewResult.DescribeFailures wrote
    // ("... could not be checked: Gemma sections 3, 7 of 20; Qwen section 2 of 20. The safety check ..."). This reads it
    // back into a list so a later run can offer to retry exactly those sections. Anything it cannot read gives no entries.
    public static IReadOnlyList<SectionFailure> ParseFailures(string? warning)
    {
        var result = new List<SectionFailure>();
        if (warning == null || warning.Length > 65536) return result;
        const string marker = "could not be checked: ";
        var start = warning == null ? -1 : warning.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return result;
        var rest = warning!.Substring(start + marker.Length);
        var end = rest.IndexOf(". ", StringComparison.Ordinal);
        if (end >= 0) rest = rest.Substring(0, end);
        foreach (var part in rest.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            System.Text.RegularExpressions.Match match;
            try { match = FailedGroup.Match(part.Trim().TrimEnd('.')); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return Array.Empty<SectionFailure>(); }
            if (!match.Success) continue;
            var count = 0;
            if (match.Groups["count"].Success && (!int.TryParse(match.Groups["count"].Value, out count) || count < 1)) continue;
            var group = new List<SectionFailure>();
            var valid = true;
            foreach (var number in match.Groups["list"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(number, out var section) || section < 1 || (count > 0 && section > count)) { valid = false; break; }
                group.Add(new SectionFailure { Reviewer = match.Groups["reviewer"].Value.Trim(), SectionIndex = section, SectionCount = count });
            }
            if (valid) result.AddRange(group);
            if (result.Count > 4096) return Array.Empty<SectionFailure>();
        }
        return NormalizeFailures(result);
    }

    public static string FailedSummary(IReadOnlyCollection<SectionFailure> failures)
    {
        var usable = NormalizeFailures(failures);
        return usable.Count == 0 ? "" :
            usable.Count + " section" + (usable.Count == 1 ? " was" : "s were") + " not checked (" + string.Join("; ", FailedLines(usable)) + "). Retry them before relying on this check.";
    }
}

// Text added to the end of the scan text so values that exist only in hidden Word channels
// (content control titles, document variables, field codes and so on) also get a mapping (item G1 wiring).
public static class UiHiddenText
{
    public const string Heading = "[Hidden document text, removed from the exported copy]";

    // Returns the text unchanged when there are no hidden lines.
    public static string Append(string text, IReadOnlyCollection<string>? hiddenLines)
    {
        if (hiddenLines == null || hiddenLines.Count == 0) return text;
        var lines = hiddenLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0) return text;
        return text + "\n\n" + Heading + "\n" + string.Join("\n", lines);
    }
}

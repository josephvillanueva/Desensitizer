using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Desensitizer;

// A count-only preflight. It never holds a filename, original document text,
// replacement values or excerpts for a dialog or diagnostic to accidentally show.
public sealed record RehydratePreview(
    int KnownOccurrences, int KnownTokenCount, int UnknownOccurrences,
    int UnknownTokenCount, int AliasTokenCount, int ConflictingTokenCount,
    int InvalidMappingCount)
{
    public bool CanRestore => KnownOccurrences > 0 && ConflictingTokenCount == 0 && InvalidMappingCount == 0;

    public string Summary => KnownOccurrences + " recognized project token occurrence" + (KnownOccurrences == 1 ? "" : "s") +
        " (" + KnownTokenCount + " distinct token" + (KnownTokenCount == 1 ? "" : "s") + ") can be restored.\n\n" +
        UnknownOccurrences + " unrecognized token occurrence" + (UnknownOccurrences == 1 ? "" : "s") +
        " (" + UnknownTokenCount + " distinct token" + (UnknownTokenCount == 1 ? "" : "s") + ") will stay unchanged.";

    public string Warning
    {
        get
        {
            var lines = new List<string>();
            if (KnownOccurrences == 0) lines.Add("No recognized project tokens were found. Open the project that created this returned file before restoring it.");
            if (UnknownOccurrences > 0) lines.Add("Unknown tokens may belong to another project or may have been added by the AI. Inspect the restored copy before using it.");
            if (AliasTokenCount > 0) lines.Add("Some tokens represent more than one spelling of the same identity. Restoration uses the project's saved canonical spelling, not necessarily the exact spelling of every original occurrence.");
            if (ConflictingTokenCount > 0) lines.Add("Some tokens refer to incompatible saved mappings. Resolve those mapping conflicts before restoring a copy.");
            if (InvalidMappingCount > 0) lines.Add("Some saved mappings have no usable token or restoration value. Correct those mappings before restoring a copy.");
            return string.Join("\n\n", lines);
        }
    }
}

public static class UiRehydratePreview
{
    private static readonly Regex CanonicalToken = new(@"\[[A-Z_]+_\d{3,}\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // Pass the same mapping list, in the same order, that restoration will use.
    // Known custom analyst tokens are counted too. Longest matches take precedence,
    // just as the restoration engine shields longer replacements first.
    public static RehydratePreview Analyze(string text, IEnumerable<Mapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(mappings);
        var supplied = mappings.ToList();
        var invalid = supplied.Count(m => m == null || string.IsNullOrWhiteSpace(m.Token) || string.IsNullOrWhiteSpace(m.OriginalValue));
        var usable = supplied.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Token) && !string.IsNullOrWhiteSpace(m.OriginalValue)).ToList();
        var covered = new bool[text.Length];
        var knownOccurrences = 0;
        var knownTokens = 0;
        var aliases = 0;
        var conflicts = 0;
        foreach (var group in usable.GroupBy(m => m.Token, StringComparer.Ordinal).OrderByDescending(g => g.Key.Length))
        {
            var count = 0;
            for (var start = 0; start <= text.Length - group.Key.Length;)
            {
                var index = text.IndexOf(group.Key, start, StringComparison.Ordinal);
                if (index < 0) break;
                var end = index + group.Key.Length;
                if (!Enumerable.Range(index, group.Key.Length).Any(i => covered[i]))
                {
                    for (var i = index; i < end; i++) covered[i] = true;
                    count++;
                }
                start = end;
            }
            if (count == 0) continue;
            knownOccurrences += count;
            knownTokens++;
            var rows = group.ToList();
            var spellings = rows.Select(m => Project.Normalize(m.OriginalValue)).Distinct(StringComparer.Ordinal).Count();
            if (!Consistent(rows)) conflicts++;
            else if (spellings > 1) aliases++;
        }

        List<Match> unknown;
        try
        {
            unknown = CanonicalToken.Matches(text).Cast<Match>()
                .Where(m => !Enumerable.Range(m.Index, m.Length).Any(i => covered[i])).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            // Regex timeout objects retain their input. Do not attach document
            // text to an exception that may be captured by diagnostics.
            throw new InvalidDataException("Token restoration preview could not finish safely. Nothing was restored.");
        }
        return new(knownOccurrences, knownTokens, unknown.Count,
            unknown.Select(m => m.Value).Distinct(StringComparer.Ordinal).Count(), aliases, conflicts, invalid);
    }

    private static bool Consistent(List<Mapping> group)
    {
        var categories = group.Select(m => (m.Category ?? "").Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (categories.Count != 1 || categories[0].Length == 0) return false;
        if (group.Select(m => Project.Normalize(m.OriginalValue)).Distinct(StringComparer.Ordinal).Count() == 1) return true;
        if (categories[0] == "PERSON")
            return group.All(a => group.All(b => PersonIdentity.AreAliases(a.OriginalValue, b.OriginalValue)));
        if (categories[0] is not ("ORGANIZATION" or "VENDOR")) return false;

        // Re-run only the existing conservative company rules on deep copies,
        // preserving provenance/protection flags but assigning initially unique
        // tokens. Starting with the colliding token would hide protected conflicts.
        var copies = JsonSerializer.Deserialize<List<Mapping>>(JsonSerializer.Serialize(group))!;
        for (var i = 0; i < copies.Count; i++) copies[i].Token = "[" + categories[0] + "_" + (i + 1).ToString("000") + "]";
        CompanyIdentity.Consolidate(copies);
        return copies.Select(m => m.Token).Distinct(StringComparer.Ordinal).Count() == 1;
    }
}

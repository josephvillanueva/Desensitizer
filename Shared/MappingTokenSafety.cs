using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Tokens belong to mappings, not only to a numeric counter for one category.
// Preserve custom/protected choices and never allocate an existing token, even
// when a legacy project contains an oversized number or a cross-category token.
public static class MappingTokenSafety
{
    public static bool IsAutomaticAlias(Mapping mapping)
    {
        if (mapping.IsManual || mapping.Ignored || mapping.RequiresContextReview || FragmentGuard.IsFragment(mapping)) return false;
        var match = Regex.Match(mapping.Token ?? "", "^\\[" + Regex.Escape(mapping.Category) + "_([0-9]+)\\]$");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
    }

    public static int NextNumber(IEnumerable<Mapping> mappings, string prefix)
    {
        var all = mappings.ToList();
        var occupied = new HashSet<string>(all.Select(m => m.Token ?? ""), StringComparer.Ordinal);
        var pattern = new Regex("^\\[" + Regex.Escape(prefix) + "_([0-9]+)\\]$");
        var maximum = 0;
        foreach (var token in occupied)
        {
            var match = pattern.Match(token);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > maximum)
                maximum = number;
        }
        long next = (long)maximum + 1;
        // At most occupied.Count candidates can be unavailable. This is bounded
        // independently of hostile numeric suffixes in a damaged old project.
        for (var attempt = 0; attempt <= occupied.Count; attempt++)
        {
            if (next > int.MaxValue) next = 1;
            var token = "[" + prefix + "_" + next.ToString("000", CultureInfo.InvariantCulture) + "]";
            if (!occupied.Contains(token)) return (int)next;
            next++;
        }
        throw new InvalidOperationException("No replacement token is available.");
    }
}

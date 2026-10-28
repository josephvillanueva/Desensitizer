using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Conservative document-wide matching. Missing name components are ambiguous,
// not evidence that two people are the same person.
public static class PersonIdentity
{
    public static bool AreAliases(string left, string right)
    {
        var leftForms = Forms(left);
        var rightForms = Forms(right);
        foreach (var a in leftForms)
            foreach (var b in rightForms)
                if (SameWords(a, b)) return true;
        return false;
    }

    private static bool SameWords(List<string> a, List<string> b)
    {
        if (a.Count == 0 || a.Count != b.Count) return false;
        // Initial expansion needs explicit context. Do not guess it here.
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static readonly string[] Honorifics = { "MR", "MS", "MRS", "DR", "ENGR", "ATTY", "HON", "FR" };

    // The written order, plus the given-names-then-surname order when the value is
    // written "Surname, Given M." with exactly one comma. A suffix such as Jr or III
    // stays a word, so it must match too. Two or more commas are not reordered.
    private static List<List<string>> Forms(string value)
    {
        var forms = new List<List<string>> { Normalize(RawWords(value)) };
        if (value.Count(c => c == ',') == 1)
        {
            var comma = value.IndexOf(',');
            var before = StripHonorifics(RawWords(value[..comma]));
            var after = StripHonorifics(RawWords(value[(comma + 1)..]));
            if (before.Count > 0 && after.Count > 0)
                forms.Add(Normalize(after.Concat(before).ToList()));
        }
        return forms;
    }

    private static List<string> RawWords(string value)
    {
        // Fold diacritics first so "Peña" and "Pena" are the same word.
        var folded = new string(value.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(System.Text.NormalizationForm.FormC);
        return Regex.Matches(folded.ToUpperInvariant(), @"[\p{L}]+(?:['-][\p{L}]+)?")
            .Cast<Match>().Select(m => m.Value).ToList();
    }

    private static List<string> StripHonorifics(List<string> words)
    {
        while (words.Count > 1 && Honorifics.Contains(words[0])) words.RemoveAt(0);
        return words;
    }

    private static List<string> Normalize(List<string> words)
    {
        StripHonorifics(words);
        // A detector sometimes includes the adjacent username in a table cell.
        if (words.Count >= 3)
        {
            var surname = words[^2];
            var initials = string.Concat(words.Take(words.Count - 2).Select(w => w[0])) + surname;
            if (words[^1] == words[0] + surname || words[^1] == words[0][..1] + surname || words[^1] == initials)
                words.RemoveAt(words.Count - 1);
        }
        return words;
    }

    public static void Consolidate(List<Mapping> mappings)
    {
        // Analyst choices and unresolved findings are not automatic identity evidence.
        // Reserve their tokens before processing any automatic group, even when an
        // older project accidentally gave the same token to another person.
        var protectedPeople = mappings.Where(m => m.Category == "PERSON" &&
            (m.IsManual || m.Ignored || m.RequiresContextReview || FragmentGuard.IsFragment(m) || !IsAutomaticToken(m.Token))).ToHashSet();
        var groups = new List<List<Mapping>>();
        foreach (var person in mappings.Where(m => m.Category == "PERSON" && !protectedPeople.Contains(m)).OrderBy(m => m.CreatedAt))
        {
            // Complete-link matching prevents A. bridging Alice and Andrew.
            var eligible = groups.Where(g => g.All(m => AreAliases(m.OriginalValue, person.OriginalValue) &&
                SameDocument(m, person))).ToList();
            if (eligible.Count == 1) eligible[0].Add(person);
            else groups.Add(new List<Mapping> { person });
        }
        var used = new HashSet<string>(mappings.Where(m => m.Category != "PERSON" || protectedPeople.Contains(m))
            .Select(m => m.Token), StringComparer.Ordinal);
        var existingTokens = new HashSet<string>(mappings.Select(m => m.Token), StringComparer.Ordinal);
        long next = mappings.Select(m => TokenNumber(m.Token)).DefaultIfEmpty(0).Max() + 1L;
        foreach (var group in groups)
        {
            var token = group.Select(m => m.Token).FirstOrDefault(t => !used.Contains(t));
            if (string.IsNullOrEmpty(token))
            {
                do
                {
                    // Mapping allocation elsewhere uses Int32 token numbers. A
                    // historical maximum must not make us emit an invalid number.
                    if (next > int.MaxValue) next = 1;
                    token = "[PERSON_" + (next++).ToString("000") + "]";
                } while (used.Contains(token) || existingTokens.Contains(token));
            }
            used.Add(token);
            foreach (var mapping in group) mapping.Token = token;
        }
        // Companies are consolidated with the same call, so every caller (UI, export, benchmark) gets both.
        CompanyIdentity.Consolidate(mappings);
    }

    private static bool IsAutomaticToken(string? token)
    {
        var match = Regex.Match(token ?? "", @"^\[PERSON_(\d+)\]$");
        return match.Success && int.TryParse(match.Groups[1].Value, out _);
    }

    private static int TokenNumber(string? token)
    {
        var match = Regex.Match(token ?? "", @"^\[PERSON_(\d+)\]$");
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : 0;
    }

    private static bool SameDocument(Mapping left, Mapping right)
    {
        var a = left.SourceFileIds.Concat(left.IdentityFileIds ?? new List<string>()).Distinct().ToArray();
        var b = right.SourceFileIds.Concat(right.IdentityFileIds ?? new List<string>()).Distinct().ToArray();
        return (a.Length == 0 && b.Length == 0) || a.Intersect(b).Any();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Optional AI-suggestion feedback, never a detector exemption or a replacement
// mapping. No fuzzy match, guessed identity or project-wide context search is used.
public static class ContextKeepPolicy
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    public static bool IsValidRule(string? value, string? category, string? hint)
    {
        if (!ProjectReviewPreferences.IsAcceptableLabel(value) || string.IsNullOrWhiteSpace(category)
            || !ProjectReviewPreferences.KeepAsIsCategories.Contains(category.Trim().ToUpperInvariant())
            || string.IsNullOrWhiteSpace(hint)) return false;
        var phrase = hint.Trim();
        if (phrase.Length is < 4 or > 200 || phrase.Any(char.IsControl)) return false;
        // Adding punctuation to the label must not manufacture a contextual rule.
        var outsideLabel = Regex.Replace(phrase, DocumentPrivacyPolicy.Pattern(value!, true), " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        return outsideLabel.Count(char.IsLetter) >= 4;
    }

    public static bool CanKeep(string value, string category, string section, ProjectReviewPreferences? preferences = null)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(section)
            || !ProjectReviewPreferences.KeepAsIsCategories.Contains(category.Trim().ToUpperInvariant())
            || preferences is { IsSupportedVersion: false }
            || preferences == null && !ReviewAllowList.Current.IsSupportedVersion) return false;
        var entries = preferences?.KeepAsIsSnapshot() ?? ReviewAllowList.Current.Entries;
        var key = ProjectReviewPreferences.KeyOf(value);
        var cat = category.Trim().ToUpperInvariant();
        var rules = entries.Where(e => e != null && IsValidRule(e.Value, cat, e.ContextHint)
            && ProjectReviewPreferences.KeyOf(e.Value) == key && e.Categories != null
            && e.Categories.Contains(cat, StringComparer.OrdinalIgnoreCase)).ToList();
        if (rules.Count == 0) return false;

        try
        {
            var occurrencePattern = new Regex(DocumentPrivacyPolicy.Pattern(value, true), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            var occurrences = occurrencePattern.Matches(section).Cast<Match>().ToList();
            if (occurrences.Count == 0) return false;
            foreach (var occurrence in occurrences)
            {
                var left = occurrence.Index;
                while (left > 0 && !Boundary(section[left - 1])) left--;
                var right = occurrence.Index + occurrence.Length;
                while (right < section.Length && !Boundary(section[right])) right++;
                var local = section.Substring(left, right - left);
                // A phrase beside one mention is not authority over another
                // mention in the same sentence. Ambiguous repetition stays visible.
                if (occurrencePattern.Matches(local).Count != 1) return false;
                if (!rules.Any(rule => Regex.IsMatch(local, DocumentPrivacyPolicy.Pattern(rule.ContextHint, true),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout))) return false;
            }
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            // Returning false keeps the suggestion visible. Do not retain the
            // timeout exception, which contains original text in its Input field.
            return false;
        }
    }

    private static bool Boundary(char character) => character is '\r' or '\n' or '\t' or '.' or '!' or '?' or ';' or ',';
}

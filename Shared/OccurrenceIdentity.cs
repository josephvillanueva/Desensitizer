using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Evidence is document-local. A matching spelling is not proof of identity.
public sealed class EntityOccurrence
{
    public string FileId { get; set; } = "";
    public int Start { get; set; }
    public int Length { get; set; }
    public string Token { get; set; } = "";
    public string Evidence { get; set; } = "";
}

public static class OccurrenceIdentity
{
    public static List<Mapping> ForParagraph(string paragraph, IEnumerable<Mapping> mappings)
    {
        var all = mappings.ToList();
        return all.Select(mapping =>
        {
            if (mapping.Category != "PERSON" || mapping.IsManual) return mapping;
            var tokens = LocalAliasTokens(paragraph, mapping, all);
            if (tokens.Count != 1) return mapping;
            var copy = System.Text.Json.JsonSerializer.Deserialize<Mapping>(System.Text.Json.JsonSerializer.Serialize(mapping))!;
            copy.Token = tokens[0];
            return copy;
        }).ToList();
    }

    public static void Refresh(string fileId, string text, List<Mapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            mapping.Occurrences ??= new List<EntityOccurrence>();
            mapping.Occurrences.RemoveAll(o => o.FileId == fileId);
            // Explicitly clear source-scoped introduction evidence even when the
            // mapping is no longer present in this file. Legacy notes must not revive.
            if ((mapping.Category is "ORGANIZATION" or "VENDOR") &&
                (mapping.SourceFileIds.Contains(fileId) || mapping.CompanyIntroductions?.ContainsKey(fileId) == true))
            {
                mapping.CompanyIntroductions ??= new Dictionary<string, List<CompanyIntroduction>>();
                mapping.CompanyIntroductions[fileId] = new List<CompanyIntroduction>();
            }
            if (!mapping.SourceFileIds.Contains(fileId)) continue;
            foreach (Match match in Regex.Matches(text, DocumentPrivacyPolicy.Pattern(mapping.OriginalValue, true), RegexOptions.IgnoreCase))
            {
                var start = text.LastIndexOf('\n', Math.Max(0, match.Index - 1));
                start = start < 0 ? 0 : start + 1;
                var end = text.IndexOf('\n', match.Index + match.Length);
                if (end < 0) end = text.Length;
                var tokens = LocalAliasTokens(text.Substring(start, end - start), mapping, mappings);
                var resolved = tokens.Count == 1;
                mapping.Occurrences.Add(new EntityOccurrence
                {
                    FileId = fileId, Start = match.Index, Length = match.Length,
                    Token = resolved ? tokens[0] : mapping.Token,
                    Evidence = resolved ? "Explicit alias declaration in the same paragraph" : "Exact source occurrence; identity not inferred"
                });
            }
            // An acronym printed as "Full Name (ABC)" in this document is recorded for the company consolidation.
            if (mapping.Category is "ORGANIZATION" or "VENDOR" && !mapping.IsManual) CompanyIdentity.RecordIntroductions(fileId, text, mapping, mappings);
            if (mapping.Category == "PERSON")
                mapping.IdentityStatus = IsShortName(mapping.OriginalValue)
                    ? "Short reference: token equality does not establish identity"
                    : "Exact name; contextual aliases only";
        }
    }

    private static List<string> LocalAliasTokens(string paragraph, Mapping reference, List<Mapping> mappings)
    {
        if (reference.Category != "PERSON" || !IsShortName(reference.OriginalValue)) return new List<string>();
        return mappings.Where(m => m.Category == "PERSON" && !IsShortName(m.OriginalValue)
                && Compatible(reference.OriginalValue, m.OriginalValue)
                && HasExplicitAlias(paragraph, reference.OriginalValue, m.OriginalValue))
            .Select(m => m.Token).Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool IsShortName(string value) => Regex.Matches(value, @"[\p{L}]+(?:['-][\p{L}]+)?").Count < 2;
    private static bool HasExplicitAlias(string paragraph, string reference, string fullName)
    {
        var name = DocumentPrivacyPolicy.Pattern(fullName, true);
        var alias = DocumentPrivacyPolicy.Pattern(reference, true);
 // A shared paragraph does not establish that a shortened name is an alias.
        return Regex.IsMatch(paragraph, name + @"\s*(?:\(\s*" + alias + @"\s*\)|,\s*(?:also\s+)?known\s+as\s+" + alias + @"(?:\s*,|\s*[.;]|\s*$))", RegexOptions.IgnoreCase);
    }
    private static bool Compatible(string reference, string fullName)
    {
        var unprefixed = Regex.Replace(fullName, @"^(?:Mr|Mrs|Ms|Dr)\.?\s+", "", RegexOptions.IgnoreCase);
        var first = Regex.Match(unprefixed, @"[\p{L}]+(?:['-][\p{L}]+)?").Value;
        return first.Length > 1 && first.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

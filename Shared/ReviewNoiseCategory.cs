using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Category evidence for one value that several detectors or reviewers labelled differently.
//
// Before this class the highest-confidence label won silently. Privacy Filter reports 1.0 for everything name-like, so a
// company, a codename or a username that it called PERSON beat the reviewers that had the right label (their confidence is
// 0 by design: agreement is not a probability). The synthetic live benchmark lost about 5 points of recall that way.
//
// The resolver weighs evidence per category class instead: which engine is authoritative for which class, how many
// reviewers agreed, suffix and system words, a longer company or project name that begins with the value, and label
// context. When the evidence is close the winner is still chosen but is marked RequiresContextReview so a person decides.
// Nothing here removes a value that has no competing label; it only chooses between labels.
public static class ReviewNoiseCategory
{
    // Gap in score (about one reviewer vote) that lets the winner stand without a review flag.
    private const double QuietMargin = 1.0;

    public static string ClassOf(string category)
    {
        var c = (category ?? "").Trim().ToUpperInvariant();
        return c switch
        {
            "PROJECT" or "SYSTEM" or "PRODUCT" => "PROJECT",
            "ORGANIZATION" or "VENDOR" or "DEPARTMENT" => "ORGANIZATION",
            _ => c,
        };
    }

    private static readonly HashSet<string> DeterministicSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "Explicit username", "Explicit birth date", "Explicit network address", "Date range", "Complete URL",
        "Explicit legal company name", "Related acronym", "Contract party", "Contextual role",
    };

    private static readonly HashSet<string> AiModels = new(StringComparer.OrdinalIgnoreCase) { "AI Qwen", "AI NuExtract", "AI Gemma" };

    // How much one candidate says for its own category. Reviewers (LLMs) read context, GLiNER knows companies and
    // products, Privacy Filter and Presidio know person-like text but label any capitalised name PERSON or ADDRESS.
    internal static double Weight(Candidate c)
    {
        var source = c.Source ?? "";
        var cls = ClassOf(c.Category);
        if (DeterministicSources.Contains(source)) return 3.0;
        // The deterministic rules label shapes (email, phone, labelled ids, street addresses) by their own pattern. A candidate
        // that the rules co-detected under the same category carries that evidence even when another engine reported it.
        var ruleClass = cls is "IDENTIFIER" or "EMAIL" or "PHONE" or "ADDRESS" or "URL";
        if (ruleClass && (source.Equals("Rules", StringComparison.OrdinalIgnoreCase) || (c.DetectionSources?.Contains("Rules", StringComparer.OrdinalIgnoreCase) ?? false))) return 2.5;
        if (source.Equals("Rules", StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (source.StartsWith("AI ", StringComparison.OrdinalIgnoreCase))
        {
            var votes = (c.DetectionSources ?? new List<string>()).Count(AiModels.Contains);
            return Math.Max(votes, 1) * 1.0;
        }
        if (source.Equals("Local AI", StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (source.Equals("GLiNER", StringComparison.OrdinalIgnoreCase)) return cls == "PERSON" ? 0.5 : 0.8;
        if (source.Equals("Privacy Filter", StringComparison.OrdinalIgnoreCase)) return 0.6;
        if (source.Equals("Presidio", StringComparison.OrdinalIgnoreCase)) return cls == "PERSON" ? 0.4 : 0.3;
        return 0.7;
    }

    private static readonly Regex LegalSuffix = new(@"(?:^|\s)(?:Inc|Corp|Corporation|Ltd|Limited|LLC|LLP|PLC|GmbH|AG|S\.?A\.?|B\.?V\.?|N\.?V\.?|Pte|Pty|Co)\.?(?:,?\s*(?:Ltd|Inc|Limited)\.?)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> OrganizationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Telecom", "Telecommunications", "Foods", "Logistics", "Manufacturing", "Systems", "Holdings", "Capital", "Insurance",
        "Industries", "Energy", "Bank", "Group", "Partners", "Associates", "Enterprises", "Trading", "Pharma", "Pharmaceuticals",
        "Labs", "Laboratories", "Technologies", "Solutions", "Services", "Consulting", "Airlines", "Motors", "Media", "Networks",
        "Ventures", "Finance", "Properties", "Realty", "Textiles", "Shipping", "Utilities", "Hospital", "University", "Foundation",
        "Institute", "Cooperative", "Corporation", "Company", "Brewery", "Mining", "Steel", "Electric", "Power", "Water", "Retail",
    };
    internal static readonly HashSet<string> SystemWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Portal", "Platform", "Ledger", "Gateway", "Hub", "Engine", "Tracker", "Dashboard", "Suite", "Console", "Bridge", "Vault",
        "Studio", "Workbench", "Pipeline", "Registry", "Scheduler", "Orchestrator", "Connector", "Exchange", "Cockpit",
    };
    private static readonly HashSet<string> StreetWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Street", "St", "Road", "Rd", "Avenue", "Ave", "Drive", "Dr", "Boulevard", "Blvd", "Lane", "Ln", "Highway", "Hwy", "Way",
        "Court", "Ct", "Place", "Plaza", "Circle", "Square", "Terrace", "Parkway", "Alley",
    };
    private static readonly Regex PersonShapeStrong = new(
        @"^(?:\p{Lu}\p{L}+|\p{Lu}\.)(?:\s+(?:\p{Lu}\.|de|del|dela|delos|della|di|da|van|von|der|la|le))+(?:\s+\p{Lu}[\p{L}'-]+)+$|^\p{Lu}[\p{L}'-]+(?:\s+(?:de|del|dela|delos|della|van|von|der|la))*,\s+\p{Lu}[\p{L}'-]+(?:\s+\p{Lu}\.)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PersonShapeWeak = new(@"^\p{Lu}\p{Ll}+(?:\s+\p{Lu}\p{Ll}+){1,2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProjectPrefix = new(@"^Project\s+\p{Lu}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string[] Words(string value) => Regex.Split(value.Trim(), @"\s+").Where(w => w.Length > 0).ToArray();

    // Suffix and keyword evidence that a name is a company or a system rather than a person.
    private static double ShapeEvidence(string cls, string value)
    {
        var words = Words(value);
        var last = words.Length == 0 ? "" : words[^1].TrimEnd('.', ',');
        if (cls == "ORGANIZATION") return (LegalSuffix.IsMatch(value.Trim()) ? 2.5 : 0) + (words.Length >= 2 && OrganizationWords.Contains(last) ? 2.0 : 0);
        if (cls == "PROJECT") return (ProjectPrefix.IsMatch(value.Trim()) ? 3.0 : 0) + (words.Length >= 2 && SystemWords.Contains(last) ? 2.0 : 0);
        return 0;
    }

    // Shape and name-structure evidence for a class. Only classes that already have a candidate get a prior.
    private static double Prior(string cls, string value, IReadOnlyList<Candidate> all, string text)
    {
        var words = Words(value);
        var last = words.Length == 0 ? "" : words[^1].TrimEnd('.', ',');
        var norm = Project.Normalize(value);
        double stem = 0;
        // A longer candidate that starts with this value says the value is a short form of that entity.
        foreach (var other in all)
        {
            if (!ClassOf(other.Category).Equals(cls, StringComparison.Ordinal)) continue;
            var o = Project.Normalize(other.Value);
            if (o.Length > norm.Length && (o.StartsWith(norm + " ", StringComparison.Ordinal) || (cls == "PROJECT" && o == "PROJECT " + norm))) { stem = ShapeEvidence(cls, other.Value) > 0 ? 3.0 : 1.5; break; }
        }
        switch (cls)
        {
            case "PERSON":
                return stem + (PersonShapeStrong.IsMatch(value.Trim()) ? 1.0 : PersonShapeWeak.IsMatch(value.Trim()) ? 0.5 : 0);
            case "ORGANIZATION":
            case "PROJECT":
                return stem + ShapeEvidence(cls, value);
            case "ADDRESS":
                return (words.Length >= 2 && StreetWords.Contains(last) ? 2.0 : 0) + (Regex.IsMatch(value, @"^\d+[A-Za-z]?\s") ? 1.0 : 0);
            case "DATE":
                return ReviewNoise.IsDateShape(value) ? 4.0 : 0;
            case "EMAIL":
                return Regex.IsMatch(value.Trim(), @"^[^\s@]+@[^\s@]+\.[A-Za-z]{2,}$") ? 4.0 : 0;
            case "PHONE":
                return Regex.IsMatch(value.Trim(), @"^\+?[\d\s().-]{7,}$") && value.Count(char.IsDigit) >= 7 ? 2.0 : 0;
            case "IDENTIFIER":
                return ReviewNoise.IsUsernameCandidate(text, value, all) ? 3.0 : 0;
            default:
                return 0;
        }
    }

    // Returns one winning category per value that was labelled in more than one category class. Values with a single class,
    // and labels inside one class (SYSTEM versus PRODUCT), are returned unchanged. When the evidence is close the winners are
    // flagged RequiresContextReview with a note naming the other labels; the flag is only added, never cleared.
    //
    // A value that only a name detector labelled PERSON or ADDRESS may also compete with the class its own shape or a longer
    // name implies ("Velmora" is the start of "Velmora Ltd.", "Project Basilisk" has the project prefix). That derived class
    // wins only on evidence, and a close call is flagged, never silent.
    public static List<Candidate> Resolve(string text, IEnumerable<Candidate> input, bool keepAllWhenAmbiguous = false)
    {
        var all = input.ToList();
        var result = new List<Candidate>(all.Count);
        foreach (var group in all.GroupBy(c => Project.Normalize(c.Value)))
        {
            var members = group.ToList();
            var classes = members.Select(c => ClassOf(c.Category)).Distinct().ToList();
            var weak = classes.All(cls => cls is "PERSON" or "ADDRESS" or "PROJECT") && members.All(c => !DeterministicSources.Contains(c.Source ?? "") && !(c.DetectionSources?.Contains("Rules", StringComparer.OrdinalIgnoreCase) ?? false));
            var derived = new Dictionary<string, (double Score, string Label)>();
            if (weak)
            {
                var sample = members[0].Value;
                foreach (var cls in new[] { "ORGANIZATION", "PROJECT" })
                {
                    var prior = Prior(cls, sample, all, text);
                    if (prior >= 1.5) derived[cls] = (prior, DerivedLabel(cls, sample, all));
                }
            }
            if (classes.Count == 1 && derived.Count == 0)
            {
                result.AddRange(members);
                continue;
            }
            var scores = classes.ToDictionary(cls => cls, cls => members.Where(c => ClassOf(c.Category) == cls).Sum(Weight) + Prior(cls, members.First(c => ClassOf(c.Category) == cls).Value, all, text));
            foreach (var kv in derived) scores[kv.Key] = kv.Value.Score - 0.01;
            var ranked = scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            var winner = ranked[0].Key;
            if (ranked.Count == 1) { result.AddRange(members); continue; }
            var quiet = ranked[0].Value - ranked[1].Value >= QuietMargin;
            if (!quiet && keepAllWhenAmbiguous && classes.Count > 1 && classes.Contains(winner))
            {
                // A genuine conflict stays visible: every label is kept so ReviewDisagreement flags them all.
                result.AddRange(members);
                continue;
            }
            List<Candidate> keep;
            string[] otherLabels;
            if (!classes.Contains(winner))
            {
                // The winning class was derived: relabel the strongest member and drop the rest of the group.
                var first = members.OrderByDescending(Weight).First();
                otherLabels = members.Select(c => (c.Category ?? "").ToUpperInvariant()).Distinct().ToArray();
                first.Category = derived[winner].Label;
                keep = new List<Candidate> { first };
            }
            else
            {
                keep = members.Where(c => ClassOf(c.Category) == winner).ToList();
                otherLabels = members.Where(c => ClassOf(c.Category) != winner).Select(c => (c.Category ?? "").ToUpperInvariant()).Distinct().ToArray();
            }
            if (!quiet)
            {
                var others = string.Join(", ", otherLabels);
                foreach (var c in keep)
                {
                    if (c.RequiresContextReview) continue;
                    c.RequiresContextReview = true;
                    c.Explanation = "Review competing categories in source context (also proposed as " + others + "). " + c.Explanation;
                }
            }
            result.AddRange(keep);
        }
        return result;
    }

    // The category label to give a value relabelled into a derived class: the label of the longer name it starts, if any.
    private static string DerivedLabel(string cls, string value, IReadOnlyList<Candidate> all)
    {
        var norm = Project.Normalize(value);
        var longer = all.FirstOrDefault(o => ClassOf(o.Category) == cls && Project.Normalize(o.Value) is var n && n.Length > norm.Length && (n.StartsWith(norm + " ", StringComparison.Ordinal) || n == "PROJECT " + norm));
        return longer != null ? (longer.Category ?? cls).ToUpperInvariant() : cls == "PROJECT" ? "PROJECT" : "ORGANIZATION";
    }
}

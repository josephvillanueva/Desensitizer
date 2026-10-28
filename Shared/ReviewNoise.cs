using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Shape and context rules that separate identifying values from review noise. Used by DocumentPrivacyPolicy.Apply, so every
// engine and reviewer candidate passes through them. Every rule here has a positive and a negative case in
// tests/Detection2/ReviewNoiseTests.cs: a rule that drops or relabels a value must not also catch a real identifier.
public static partial class ReviewNoise
{
    private const string Month = @"(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)";
    private static readonly Regex DateShape = new(
        @"^(?:\d{4}-\d{1,2}-\d{1,2}|\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4}|\d{1,2}(?:st|nd|rd|th)?\s+" + Month + @"\.?,?\s+\d{4}|" + Month + @"\.?\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4}|" + Month + @"\.?\s+\d{4})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsDateShape(string value) => DateShape.IsMatch(value.Trim());

    private static readonly Regex UsernameLabel = new(
        @"(?:user\s*name|user\s*id|userid|login(?:\s*(?:id|name))?|logon|account\s*name|handle)\s*[:=]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LowerToken = new(@"^[a-z]{6,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> NameParticles = new(StringComparer.OrdinalIgnoreCase) { "de", "del", "dela", "delos", "della", "di", "da", "van", "von", "der", "la", "le" };

    // True when the value is printed after a Username, Login or User ID label in the text.
    public static bool HasUsernameLabel(string text, string value)
    {
        foreach (Match m in Regex.Matches(text, DocumentPrivacyPolicy.Pattern(value, true), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            var start = Math.Max(0, m.Index - 30);
            var before = text.Substring(start, m.Index - start);
            var lineBreak = before.LastIndexOfAny(new[] { '\n', '\r' });
            if (lineBreak >= 0) before = before.Substring(lineBreak + 1);
            if (UsernameLabel.IsMatch(before)) return true;
        }
        return false;
    }

    // A username is first initial(s) plus a surname: ldmercado for Liza D. Mercado, lqpascual for Liza Q. Pascual. The shape
    // alone (six or more lower-case letters, no space) would also match ordinary words, so the surname must belong to a person
    // candidate whose given-name initials start the token. A Username or Login label is accepted without the person evidence.
    public static bool IsUsernameCandidate(string text, string value, IEnumerable<Candidate> all)
    {
        var v = value.Trim();
        if (v.Length < 3 || v.Contains(' ')) return false;
        if (HasUsernameLabel(text, v)) return true;
        if (!LowerToken.IsMatch(v)) return false;
        foreach (var person in all.Where(c => ReviewNoiseCategory.ClassOf(c.Category) == "PERSON"))
        {
            if (!TrySplitPerson(person.Value, out var initials, out var surnames)) continue;
            foreach (var surname in surnames)
                if (surname.Length >= 4 && v.EndsWith(surname, StringComparison.Ordinal) && v.Length - surname.Length is >= 1 and <= 3)
                {
                    var prefix = v.Substring(0, v.Length - surname.Length);
                    if (initials.StartsWith(prefix, StringComparison.Ordinal) || prefix[0] == initials[0]) return true;
                }
        }
        return false;
    }

    // "Liza D. Mercado" gives initials "ld" and surname forms {"mercado"}; "Rosario O. dela Cruz" gives "ro" and
    // {"cruz", "delacruz"}; "Mercado, Liza D." (reversed) gives the same as the natural order.
    private static bool TrySplitPerson(string value, out string initials, out string[] surnames)
    {
        initials = ""; surnames = Array.Empty<string>();
        var v = value.Trim();
        var comma = v.IndexOf(',');
        string given, family;
        if (comma > 0)
        {
            family = v.Substring(0, comma); given = v.Substring(comma + 1);
        }
        else
        {
            var words = Regex.Split(v, @"\s+").Where(w => w.Length > 0).ToList();
            if (words.Count < 2) return false;
            var familyStart = words.Count - 1;
            while (familyStart > 1 && NameParticles.Contains(words[familyStart - 1])) familyStart--;
            family = string.Join(" ", words.Skip(familyStart)); given = string.Join(" ", words.Take(familyStart));
        }
        var letters = Regex.Matches(given, @"\p{L}+").Select(m => m.Value).Where(w => !NameParticles.Contains(w)).ToList();
        if (letters.Count == 0) return false;
        initials = string.Concat(letters.Select(w => char.ToLowerInvariant(w[0])));
        var fam = Regex.Matches(family, @"\p{L}+").Select(m => m.Value.ToLowerInvariant()).ToList();
        if (fam.Count == 0) return false;
        surnames = new[] { fam[^1], string.Concat(fam) }.Distinct().ToArray();
        return true;
    }
}

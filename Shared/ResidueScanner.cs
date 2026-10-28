using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Desensitizer;

public sealed record ResidueHit(Mapping Mapping, bool InText, XAttribute? Attribute);

// Finds mapped original values that remain in an exported part. Replacement
// tokens are masked first so a token that happens to contain a mapped value is
// not reported. One combined alternation answers "does anything remain" for a
// string in a single pass. Only when something remains does it fall back to the
// per-mapping patterns, in mapping order, to name the first offending mapping.
// The result is the same as testing each mapping against each string separately.
public sealed class ResidueScanner
{
    private readonly Mapping[] mappings;
    private readonly string[] tokens;
    private readonly Func<string, string>? extraMask;
    private readonly Regex? combined;
    private readonly Regex?[] individual;
    private readonly int minimumLength;

    public ResidueScanner(IEnumerable<Mapping> mappings, IEnumerable<string> replacementTokens, Func<string, string>? extraMask = null)
    {
        this.mappings = mappings.Where(m => !string.IsNullOrWhiteSpace(m.OriginalValue)).ToArray();
        // Longest first, as the audit has always masked them.
        tokens = replacementTokens.Where(t => !string.IsNullOrEmpty(t)).ToArray();
        this.extraMask = extraMask;
        individual = new Regex?[this.mappings.Length];
        if (this.mappings.Length == 0) { minimumLength = int.MaxValue; return; }
        var ordered = this.mappings.Select(m => m.OriginalValue).OrderByDescending(v => v.Length).ToArray();
        combined = new Regex(string.Join("|", ordered.Select(v => "(?:" + DocumentPrivacyPolicy.Pattern(v, true) + ")")), RegexOptions.IgnoreCase);
        // A match consumes at least the visible characters of its value.
        minimumLength = this.mappings.Min(m => m.OriginalValue.Count(c => !char.IsWhiteSpace(c)));
    }

    // Text with the fixed placeholder wording and every replacement token blanked.
    public string Mask(string value)
    {
        if (extraMask != null) value = extraMask(value);
        foreach (var token in tokens) value = value.Replace(token, " ", StringComparison.OrdinalIgnoreCase);
        return value;
    }

    public bool ContainsAny(string value)
    {
        if (combined == null || value.Length < minimumLength) return false;
        return combined.IsMatch(Mask(value));
    }

    // Conservative release check for separator/Unicode and encoded forms of an
    // already approved value. This never discovers or automatically merges people.
    // Short generic strings remain on the exact-match path to avoid blocking rules.
    public Mapping? FindHiddenVariant(string value)
    {
        var masked=Mask(value);
        foreach(var variant in Variants(masked))
        {
            var folded=Fold(variant);
            foreach(var mapping in mappings)
            {
                var needle=Fold(mapping.OriginalValue);
                if(needle.Length>=8&&(folded.Contains(needle,StringComparison.Ordinal)||folded.Contains(new string(needle.Reverse().ToArray()),StringComparison.Ordinal)))return mapping;
                if(mapping.Category=="PERSON")
                {
                    var words=mapping.OriginalValue.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                    if(words.Length>1&&needle.Length>=8&&folded.Contains(Fold(string.Join(" ",words.Skip(1))+" "+words[0]),StringComparison.Ordinal))return mapping;
                }
            }
        }
        return null;
    }

    private static string Fold(string value)
    {
        var result=new StringBuilder();
        foreach(var c in value.Normalize(NormalizationForm.FormKD))
            if(char.IsLetterOrDigit(c)&&CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark))
                result.Append(char.ToUpperInvariant(c switch
                {
                    '\u0430'=>'a','\u0435'=>'e','\u043E'=>'o','\u0440'=>'p','\u0441'=>'c','\u0445'=>'x','\u0456'=>'i',
                    '\u0410'=>'A','\u0415'=>'E','\u041E'=>'O','\u0420'=>'P','\u0421'=>'C','\u0425'=>'X',
                    '\u0396'=>'Z','\u03B6'=>'z','\u039F'=>'O','\u03BF'=>'o',_=>c
                }));
        return result.ToString();
    }

    private static IEnumerable<string> Variants(string value)
    {
        yield return value;
        var decoded=WebUtility.HtmlDecode(value);
        yield return decoded;
        string? percent=null;
        try{percent=Uri.UnescapeDataString(decoded);}catch(UriFormatException){}
        if(percent!=null)yield return percent;
        foreach(Match match in Regex.Matches(decoded,@"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{16,}={0,2}(?![A-Za-z0-9+/])",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))
        {
            // A bounded encoded word can conceal an identifying value. Do not
            // decode whole documents or retain decoded diagnostics.
            if(match.Length>4096)continue;
            byte[]? bytes=null;
            try{bytes=Convert.FromBase64String(match.Value.PadRight((match.Length+3)/4*4,'='));}catch(FormatException){}
            if(bytes!=null){yield return Encoding.UTF8.GetString(bytes);yield return Encoding.Unicode.GetString(bytes);}
        }
        foreach(Match match in Regex.Matches(decoded,@"\b(?:[0-9A-Fa-f]{2}){8,}\b",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))
            if(match.Length<=4096){var bytes=Convert.FromHexString(match.Value);yield return Encoding.UTF8.GetString(bytes);yield return Encoding.Unicode.GetString(bytes);}
    }

    // The first mapping, in the order given, whose value remains in the part.
    public ResidueHit? Find(XDocument document, Func<XAttribute, bool> isAuditedAttribute)
    {
        if (combined == null) return null;
        var texts = new List<string>();
        foreach (var node in document.DescendantNodes().OfType<XText>())
            if (node.Value.Length >= minimumLength) texts.Add(Mask(node.Value));
        var attributes = new List<(XAttribute Attribute, string Masked)>();
        foreach (var attribute in document.Descendants().Attributes())
            if (!attribute.IsNamespaceDeclaration && attribute.Value.Length >= minimumLength && isAuditedAttribute(attribute))
                attributes.Add((attribute, Mask(attribute.Value)));
        if (!texts.Any(combined.IsMatch) && !attributes.Any(item => combined.IsMatch(item.Masked))) return null;
        for (var i = 0; i < mappings.Length; i++)
        {
            var regex = individual[i] ??= new Regex(DocumentPrivacyPolicy.Pattern(mappings[i].OriginalValue, true), RegexOptions.IgnoreCase);
            var inText = texts.Any(regex.IsMatch);
            var attribute = attributes.FirstOrDefault(item => regex.IsMatch(item.Masked)).Attribute;
            if (inText || attribute != null) return new ResidueHit(mappings[i], inText, attribute);
        }
        return null;
    }

    public Mapping? FindPackageVariant(XDocument document)
    {
        // Joining XML text covers split CDATA and adjacent nodes. Names and
        // namespace declarations are hidden channels, outside the exact scanner.
        var hidden=string.Join(" ",document.Descendants().Select(e=>e.Name.ToString()))+" "+
            string.Join(" ",document.Descendants().Attributes().Select(a=>a.Name+" "+a.Value))+" "+
            string.Concat(document.DescendantNodes().OfType<XText>().Select(n=>n.Value));
        return FindHiddenVariant(hidden);
    }
}

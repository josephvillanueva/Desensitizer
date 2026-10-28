using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Desensitizer;

// Text replacement helpers for Open XML parts.
internal static class OoxmlText
{
    // Replaces inside text nodes that together form one string (the runs of a
    // paragraph, a shared string or a comment). A value split across runs is
    // found and replaced; the first run keeps the replacement and later runs lose
    // the covered characters. Same algorithm as the Word pipeline.
    public static void ReplaceInRuns(List<XElement> nodes, List<Mapping> map, bool forward)
    {
        if (nodes.Count == 0 || map.Count == 0) return;
        var markerPrefix = Guid.NewGuid().ToString("N") + "_";
        var paragraphMap = forward ? OccurrenceIdentity.ForParagraph(string.Concat(nodes.Select(n => n.Value)), map) : map;
        var replacements = paragraphMap.Select((mapping, index) => new
        {
            From = forward ? mapping.OriginalValue : mapping.Token,
            To = forward ? mapping.Token : mapping.OriginalValue,
            Marker = markerPrefix + index,
        }).ToList();
        foreach (var r in replacements) DocumentProcessor.ReplaceAcrossRuns(nodes, r.From, r.Marker, forward);
        foreach (var r in replacements) DocumentProcessor.ReplaceAcrossRuns(nodes, r.Marker, r.To, false);
    }

    private static readonly Regex HeaderCode = new(@"(&""[^""]*""|&\d+|&[LCRBIUESXYPNDTZFAG&])", RegexOptions.CultureInvariant);

    // Header and footer text carries codes glued to the words (&LName, &CPage &P). The words are
    // handled piece by piece so a code letter never hides a value from a word boundary.
    public static string MapHeader(string text, Func<string, string> change)
    {
        var pieces = HeaderCode.Split(text);
        for (var i = 0; i < pieces.Length; i += 2) pieces[i] = change(pieces[i]);
        return string.Concat(pieces);
    }

    public static IEnumerable<string> HeaderPieces(string text) => HeaderCode.Split(text).Where((_, i) => i % 2 == 0);

    // The words of a header with every code replaced by a space.
    public static string PlainHeader(string text) => string.Join(" ", HeaderPieces(text).Where(p => p.Length > 0)).Trim();

    public static string Joined(IEnumerable<XElement> nodes) => string.Concat(nodes.Select(n => n.Value));

    // A line-safe form of text for the review text: line breaks and tabs become one space.
    public static string Flat(string value) =>
        value.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0 ? value : Regex.Replace(value, @"[\r\n\t]+", " ").Trim();

    // Applies a change to the contents of every string literal in a formula and
    // leaves everything else, including quoted sheet names, exactly as written.
    public static string RewriteFormula(string formula, Func<string, string> change)
    {
        if (formula.IndexOf('"') < 0) return formula;
        var result = new StringBuilder();
        var i = 0;
        while (i < formula.Length)
        {
            var c = formula[i];
            if (c == '\'')
            {
                var start = i++;
                while (i < formula.Length)
                {
                    if (formula[i] == '\'') { if (i + 1 < formula.Length && formula[i + 1] == '\'') { i += 2; continue; } i++; break; }
                    i++;
                }
                result.Append(formula, start, i - start);
            }
            else if (c == '"')
            {
                i++;
                var content = new StringBuilder();
                var closed = false;
                while (i < formula.Length)
                {
                    if (formula[i] == '"') { if (i + 1 < formula.Length && formula[i + 1] == '"') { content.Append('"'); i += 2; continue; } i++; closed = true; break; }
                    content.Append(formula[i++]);
                }
                if (!closed) throw new System.IO.InvalidDataException("A formula has a text value without a closing quote. The file is damaged.");
                result.Append('"').Append(change(content.ToString()).Replace("\"", "\"\"")).Append('"');
            }
            else { result.Append(c); i++; }
        }
        return result.ToString();
    }

    // The string literals of a formula, decoded.
    public static List<string> Literals(string formula)
    {
        var found = new List<string>();
        RewriteFormula(formula, s => { found.Add(s); return s; });
        return found;
    }

    private static readonly Regex ExternalReference = new(@"'?\[\d+\][^!]*!", RegexOptions.CultureInvariant);

    // True when a formula points into another workbook ([1]Sheet1!A1).
    public static bool HasExternalReference(string formula)
    {
        // Blank the literals first so quoted text such as "[1]x!" is not mistaken for a link.
        var stripped = RewriteFormula(formula, _ => "");
        return ExternalReference.IsMatch(stripped);
    }
}

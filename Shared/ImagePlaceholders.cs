using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Desensitizer;

// Pictures, charts, text boxes and embedded objects are removed from the
// protected copy. Each one leaves a visible text placeholder in its run so the
// reader of the protected copy can see where it was and how large it was.
// The placeholder never contains the picture name or file name. Alt text is
// added only after it has been passed through the document mappings.
public static class ImagePlaceholders
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const double EmuPerInch = 914400.0;
    private const string AltLabel = "alt text: ";

    public static bool IsObject(XElement element) => element.Name.LocalName is "drawing" or "pict" or "object";

    // The elements that each become one placeholder, in document order. A
    // mc:AlternateContent wrapper holding a drawing and its VML fallback is one image.
    public static List<XElement> FindUnits(XContainer root)
    {
        var units = new List<XElement>();
        var chosen = new HashSet<XElement>();
        foreach (var candidate in root.Descendants().Where(IsObject).ToList())
        {
            if (candidate.Ancestors().Any(chosen.Contains)) continue;
            var unit = candidate;
            var wrapper = candidate.Ancestors().Where(a => a.Name == MC + "AlternateContent" && HoldsOnlyObjects(a)).LastOrDefault();
            if (wrapper != null) unit = wrapper;
            chosen.Add(unit);
            units.Add(unit);
        }
        return units;
    }

    // Only objects inside a paragraph leave text. A picture bullet in numbering
    // or a picture in a style has no place for text and is just removed.
    private static bool WantsPlaceholder(XElement unit, DocumentProcessingOptions options) =>
        options.PlaceholdersEnabled && unit.Ancestors(W + "p").Any();

    private static bool HoldsOnlyObjects(XElement wrapper) =>
        !wrapper.Descendants(W + "t").Any(text => !text.Ancestors().TakeWhile(a => a != wrapper).Any(IsObject));

    // Replaces every unit with placeholder text and returns the texts in
    // document order. Without a placeholder label the units are only removed.
    public static List<string> ReplaceAll(XContainer root, DocumentProcessingOptions options, Func<string, string>? mapAltText)
    {
        var placeholders = new List<string>();
        foreach (var unit in FindUnits(root))
        {
            if (!WantsPlaceholder(unit, options)) { unit.Remove(); continue; }
            var text = Build(unit, options, mapAltText);
            placeholders.Add(text);
            var node = new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text);
            if (unit.Parent?.Name == W + "r") unit.ReplaceWith(node);
            else unit.ReplaceWith(new XElement(W + "r", node));
        }
        return placeholders;
    }

    // Removes the units without leaving text and returns the placeholders that
    // ReplaceAll would have produced, so an auditor can check the output for them.
    public static List<string> ExpectedPlaceholders(XContainer root, DocumentProcessingOptions options, Func<string, string>? mapAltText)
    {
        var expected = new List<string>();
        foreach (var unit in FindUnits(root))
        {
            if (WantsPlaceholder(unit, options)) expected.Add(Build(unit, options, mapAltText));
            unit.Remove();
        }
        return expected;
    }

    // Removes the placeholder text nodes, in order, from an exported part.
    // Returns false when the expected placeholders were not all present in order.
    public static bool ConsumePlaceholders(XContainer root, IReadOnlyList<string> expected)
    {
        var next = 0;
        if (expected.Count > 0)
            foreach (var text in root.Descendants(W + "t").ToList())
                if (next < expected.Count && text.Value == expected[next]) { text.Remove(); next++; }
        return next == expected.Count;
    }

    // The unmapped alt texts of every picture, used only to decide which saved
    // mappings apply to this document. Never written anywhere.
    public static IEnumerable<string> AltTexts(XContainer root) =>
        FindUnits(root).Select(AltText).Where(alt => !string.IsNullOrWhiteSpace(alt)).Select(alt => Clean(alt!));

    public static string Build(XElement unit, DocumentProcessingOptions options, Func<string, string>? mapAltText)
    {
        var details = new List<string>();
        if (options.IncludeImageSize && Size(unit) is { } size)
            details.Add(size.Width.ToString("F2", CultureInfo.InvariantCulture) + " x " + size.Height.ToString("F2", CultureInfo.InvariantCulture) + " in");
        if (options.IncludeImageAltText && mapAltText != null && AltText(unit) is { } alt)
        {
            var mapped = Truncate(Clean(mapAltText(Clean(alt))), options.MaxAltTextLength);
            if (mapped.Length > 0) details.Add(AltLabel + mapped);
        }
        var label = options.ImagePlaceholder!;
        if (details.Count == 0) return label;
        var joined = string.Join("; ", details);
        return label.EndsWith("]", StringComparison.Ordinal) ? label.Substring(0, label.Length - 1) + ": " + joined + "]" : label + " (" + joined + ")";
    }

    // Blanks only the fixed wording of a placeholder (label, size and the alt
    // text label) so an export audit does not flag generated words. Alt text
    // itself stays visible to the audit.
    public static Func<string, string> FixedTextMask(DocumentProcessingOptions options)
    {
        if (!options.PlaceholdersEnabled) return value => value;
        var label = options.ImagePlaceholder!;
        var head = label.EndsWith("]", StringComparison.Ordinal) ? label.Substring(0, label.Length - 1) : label;
        var opening = label.EndsWith("]", StringComparison.Ordinal) ? ": " : " \\(";
        var prefix = new Regex("^" + Regex.Escape(head) + "(?:" + opening + @"(?:\d+\.\d\d x \d+\.\d\d in(?:; )?)?(?:" + Regex.Escape(AltLabel) + ")?)?");
        return value => prefix.IsMatch(value) ? prefix.Replace(value, " ", 1) : value;
    }

    private static (double Width, double Height)? Size(XElement unit)
    {
        var extent = unit.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "extent" && e.Attribute("cx") != null && e.Attribute("cy") != null);
        if (extent != null
            && double.TryParse(extent.Attribute("cx")!.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cx)
            && double.TryParse(extent.Attribute("cy")!.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cy)
            && cx > 0 && cy > 0)
            return (cx / EmuPerInch, cy / EmuPerInch);
        foreach (var element in unit.DescendantsAndSelf())
        {
            var style = element.Attribute("style")?.Value;
            if (style == null) continue;
            var values = style.Split(';').Select(part => part.Split(new[] { ':' }, 2)).Where(pair => pair.Length == 2)
                .GroupBy(pair => pair[0].Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First()[1].Trim());
            if (values.TryGetValue("width", out var w) && values.TryGetValue("height", out var h)
                && Inches(w) is { } width && Inches(h) is { } height && width > 0 && height > 0)
                return (width, height);
        }
        return null;
    }

    private static double? Inches(string css)
    {
        var match = Regex.Match(css, @"^(?<n>\d+(?:\.\d+)?)(?<u>pt|in|cm|mm|px|pc)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var number = double.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        return match.Groups["u"].Value.ToLowerInvariant() switch
        {
            "pt" => number / 72.0,
            "in" => number,
            "cm" => number / 2.54,
            "mm" => number / 25.4,
            "px" => number / 96.0,
            "pc" => number / 6.0,
            _ => null
        };
    }

    // The author-written description. The picture name is not used: it is
    // often a file name or an automatic label that can carry identifying text.
    private static string? AltText(XElement unit)
    {
        var properties = unit.DescendantsAndSelf().Where(e => e.Name.LocalName == "docPr").ToList();
        var description = properties.Select(e => e.Attribute("descr")?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (description != null) return description;
        var vml = unit.DescendantsAndSelf().Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "alt")?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (vml != null) return vml;
        return properties.Select(e => e.Attribute("title")?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static string Clean(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value) builder.Append(char.IsControl(c) || (c >= '' && c <= '') ? ' ' : c);
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    // Shortens at a word boundary and never inside a replacement token.
    private static string Truncate(string value, int limit)
    {
        if (limit <= 0) return "";
        if (value.Length <= limit) return value;
        var cut = value.Substring(0, limit);
        var open = cut.LastIndexOf('[');
        if (open >= 0 && cut.IndexOf(']', open) < 0) cut = cut.Substring(0, open);
        var space = cut.LastIndexOf(' ');
        if (space > limit / 2) cut = cut.Substring(0, space);
        cut = cut.TrimEnd();
        return cut.Length == 0 ? "" : cut + "...";
    }
}

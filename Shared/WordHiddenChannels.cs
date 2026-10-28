using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Desensitizer;

// Text that Word stores outside the visible paragraphs: content control
// alias and tag, document variables, field instructions, glossary entries,
// list number text, smart tag attributes, table captions and shape alt text.
// Detectors never see it, so a value that appears only there would be neither
// found nor replaced.
//
//   Strip(doc)    removes or neutralises these channels in a part. The export
//                 applies it to every Word part, so such a value cannot ship.
//   Describe(doc) returns the same channels as labelled text lines
//                 ("sdt alias: ..."), so a caller can feed them to detectors.
//                 DocumentProcessor.ReadHiddenChannels reads a whole file.
//                 The interface does not call it yet (a later UI stage).
public static class WordHiddenChannels
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // Words that may stay in list number text such as "Article %1" or "FR-%1".
    private static readonly HashSet<string> StructuralWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Article", "Section", "Chapter", "Part", "Appendix", "Annex", "Step", "Item", "Figure", "Table", "Phase", "Rule", "Level",
        "Requirement", "Clause", "Schedule", "Exhibit", "Page", "No", "Stage", "Task", "Note", "Req", "FR", "BR", "NFR", "UC", "TC", "Q", "A"
    };
    private static readonly Regex NumberTextToken = new(@"%\d|[\p{L}\p{N}]+", RegexOptions.CultureInvariant);

    // ------------------------------------------------------------------ strip

    public static void Strip(XDocument doc)
    {
        // Content controls: alias, tag, data binding, placeholder pointer and list entries.
        foreach (var properties in doc.Descendants(W + "sdtPr").ToList())
        {
            foreach (var name in new[] { "alias", "tag", "dataBinding", "placeholder" })
                properties.Elements().Where(e => e.Name.Namespace == W && e.Name.LocalName == name).ToList().ForEach(e => e.Remove());
            foreach (var item in properties.Descendants(W + "listItem").ToList()) item.Remove();
        }
        // Document variables, mail merge sources, attached templates and schemas (settings.xml).
        foreach (var name in new[] { "docVars", "mailMerge", "smartTagType", "attachedSchema", "saveThroughXslt", "attachedTemplate" })
            doc.Descendants(W + name).ToList().ForEach(e => e.Remove());
        // Imported alternate content has no scanned text and a relationship that is removed.
        doc.Descendants(W + "altChunk").ToList().ForEach(e => e.Remove());
        // Embedded fonts are binary parts that are not exported.
        foreach (var font in doc.Descendants(W + "font").ToList())
            font.Elements().Where(e => e.Name.Namespace == W && e.Name.LocalName.StartsWith("embed", StringComparison.Ordinal)).ToList().ForEach(e => e.Remove());
        // Smart tags and inline custom XML wrappers keep their content and lose their attributes.
        foreach (var wrapper in doc.Descendants().Where(e => e.Name.Namespace == W && e.Name.LocalName is "smartTag" or "customXml").ToList())
            wrapper.ReplaceWith(wrapper.Nodes().Where(n => !(n is XElement e && e.Name.Namespace == W && e.Name.LocalName is "smartTagPr" or "customXmlPr")).ToArray());
        // Table captions and descriptions.
        doc.Descendants().Where(e => e.Name.Namespace == W && e.Name.LocalName is "tblCaption" or "tblDescription").ToList().ForEach(e => e.Remove());
        // Names of list definitions.
        foreach (var abstractNum in doc.Descendants(W + "abstractNum").ToList())
            abstractNum.Elements(W + "name").ToList().ForEach(e => e.Remove());
        // List number text keeps its placeholders and structural words only.
        foreach (var level in doc.Descendants(W + "lvlText").ToList())
        {
            var value = level.Attribute(W + "val");
            if (value != null) value.Value = NeutraliseNumberText(value.Value);
        }
        // The user names behind editing permissions.
        doc.Descendants(W + "permStart").Attributes(W + "ed").ToList().ForEach(a => a.Remove());
        // Shape alt text and titles that survive outside a picture placeholder (a diagram fallback, a stray VML shape).
        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName is "shape" or "shapetype" or "group" or "rect" or "roundrect" or "oval" or "line" or "image").ToList())
            element.Attributes().Where(a => a.Name.Namespace == XNamespace.None && a.Name.LocalName is "alt" or "title" or "href").ToList().ForEach(a => a.Remove());
        doc.Descendants().Where(e => e.Name.LocalName == "docPr" && e.Name.Namespace != W).ToList().ForEach(e =>
            e.Attributes().Where(a => a.Name.LocalName is "descr" or "title").ToList().ForEach(a => a.Remove()));
    }

    public static string NeutraliseNumberText(string value) =>
        NumberTextToken.Replace(value, match => match.Value.StartsWith("%", StringComparison.Ordinal) || StructuralWords.Contains(match.Value) ? match.Value : "");

    // ------------------------------------------------------------------ describe

    // Labelled text lines for each channel in one part, in document order.
    // The label names the channel; the value is text exactly as stored.
    public static IEnumerable<string> Describe(XDocument doc)
    {
        foreach (var element in doc.Descendants())
        {
            if (element.Name.Namespace == W)
            {
                switch (element.Name.LocalName)
                {
                    case "alias" when element.Parent?.Name == W + "sdtPr": foreach (var l in Line("sdt alias", element.Attribute(W + "val")?.Value)) yield return l; break;
                    case "tag" when element.Parent?.Name == W + "sdtPr": foreach (var l in Line("sdt tag", element.Attribute(W + "val")?.Value)) yield return l; break;
                    case "listItem":
                        foreach (var l in Line("sdt list item", element.Attribute(W + "displayText")?.Value)) yield return l;
                        foreach (var l in Line("sdt list value", element.Attribute(W + "value")?.Value)) yield return l;
                        break;
                    case "docVar":
                        foreach (var l in Line("document variable name", element.Attribute(W + "name")?.Value)) yield return l;
                        foreach (var l in Line("document variable value", element.Attribute(W + "val")?.Value)) yield return l;
                        break;
                    case "fldSimple":
                        var instruction = element.Attribute(W + "instr")?.Value;
                        if (instruction != null && !FieldInstructions.IsKept(instruction)) foreach (var l in Line("field code", instruction)) yield return l;
                        break;
                    case "p":
                        foreach (var l in FieldCodes(element)) yield return l;
                        break;
                    case "docPart":
                        foreach (var l in Line("glossary entry name", element.Element(W + "docPartPr")?.Element(W + "name")?.Attribute(W + "val")?.Value)) yield return l;
                        foreach (var l in Line("glossary entry description", element.Element(W + "docPartPr")?.Element(W + "description")?.Attribute(W + "val")?.Value)) yield return l;
                        break;
                    case "lvlText":
                        var number = element.Attribute(W + "val")?.Value;
                        if (number != null && NeutraliseNumberText(number) != number) foreach (var l in Line("list number text", number)) yield return l;
                        break;
                    case "smartTag":
                        foreach (var a in element.Attributes().Where(a => !a.IsNamespaceDeclaration)) foreach (var l in Line("smart tag " + a.Name.LocalName, a.Value)) yield return l;
                        break;
                    case "smartTagPr":
                    case "customXmlPr":
                        foreach (var attribute in element.Descendants().Attributes(W + "val").Concat(element.Descendants().Attributes(W + "name"))) foreach (var l in Line("tag property", attribute.Value)) yield return l;
                        break;
                    case "tblCaption": foreach (var l in Line("table caption", element.Attribute(W + "val")?.Value)) yield return l; break;
                    case "tblDescription": foreach (var l in Line("table description", element.Attribute(W + "val")?.Value)) yield return l; break;
                    case "name" when element.Parent?.Name == W + "abstractNum": foreach (var l in Line("list name", element.Attribute(W + "val")?.Value)) yield return l; break;
                    case "permStart": foreach (var l in Line("editing permission user", element.Attribute(W + "ed")?.Value)) yield return l; break;
                    case "hyperlink": foreach (var l in Line("hyperlink tooltip", element.Attribute(W + "tooltip")?.Value)) yield return l; break;
                }
            }
            else if (element.Name.LocalName == "docPr")
            {
                foreach (var l in Line("shape alt text", element.Attribute("descr")?.Value)) yield return l;
                foreach (var l in Line("shape title", element.Attribute("title")?.Value)) yield return l;
            }
            else if (element.Name.LocalName is "shape" or "rect" or "roundrect" or "oval" or "image" or "group")
            {
                foreach (var l in Line("shape alt text", element.Attribute("alt")?.Value)) yield return l;
                foreach (var l in Line("shape title", element.Attribute("title")?.Value)) yield return l;
            }
        }
    }

    // Complex field instructions can be split over several runs: join them per field in one paragraph.
    private static IEnumerable<string> FieldCodes(XElement paragraph)
    {
        var stack = new Stack<(StringBuilder Code, bool InCode)>();
        var codes = new List<string>();
        foreach (var element in paragraph.Descendants().Where(x => x.Name == W + "fldChar" || x.Name == W + "instrText"))
        {
            if (element.Ancestors(W + "p").First() != paragraph) continue;
            if (element.Name == W + "fldChar")
            {
                var type = element.Attribute(W + "fldCharType")?.Value;
                if (type == "begin") stack.Push((new StringBuilder(), true));
                else if (type == "separate" && stack.Count > 0) { var top = stack.Pop(); codes.Add(top.Code.ToString()); stack.Push((new StringBuilder(), false)); }
                else if (type == "end" && stack.Count > 0) { var top = stack.Pop(); if (top.InCode) codes.Add(top.Code.ToString()); }
            }
            else if (stack.Count > 0 && stack.Peek().InCode) stack.Peek().Code.Append(element.Value);
        }
        while (stack.Count > 0) { var top = stack.Pop(); if (top.InCode) codes.Add(top.Code.ToString()); }
        foreach (var code in codes)
            if (code.Trim().Length > 0 && !FieldInstructions.IsKept(code))
                foreach (var l in Line("field code", code)) yield return l;
    }

    private static IEnumerable<string> Line(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        yield return label + ": " + Regex.Replace(value.Trim(), @"\s+", " ");
    }
}

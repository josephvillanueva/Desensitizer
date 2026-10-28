using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace Desensitizer;

// Excel workbooks. The protected copy is a brand new package built part by part
// from an allow-list (never the source file with edits). Text is replaced in
// shared strings (plain and rich), inline strings, cached formula text, string
// literals inside formulas, comments, headers and footers, table column names,
// validation messages and cell style names. Everything that can carry unreviewed
// content is removed: drawings and charts, pictures, embedded objects, pivot
// caches, external links, printer settings, custom data, threaded comments,
// document properties. Sheet, table and named range names are NOT renamed: if
// one contains a selected value the export stops, because a rename would have to
// be applied to every formula and reference and cannot be proven complete.
public sealed class XlsxFormat : IDocumentFormat
{
    public string Extension => ".xlsx";
    public string DisplayName => "Excel workbook";
    public bool IsBuiltIn => false;

    public void Validate(string path) { using var model = XlsxModel.Open(path); }
    public string ReadText(string path, DocumentPackageContext? context = null)
    {
        using var model = XlsxModel.Open(path);
        return string.Join("\n", model.TextLines());
    }
    public TableReviewContext? ReadTableContext(string path, string text, DocumentPackageContext? context = null) => null;
    public ReviewMarkupSummary InspectReviewMarkup(string path, DocumentPackageContext? context = null) => ReviewMarkupSummary.Empty;
    public IReadOnlyList<string> ReadHiddenChannels(string path, DocumentPackageContext? context = null) => Array.Empty<string>();
    public IReadOnlyList<string> DescribeRemovals(string path, DocumentPackageContext? context = null)
    {
        using var model = XlsxModel.Open(path);
        return model.Removals();
    }

    public int Transform(string source, string output, IEnumerable<Mapping> mappings, bool forward, DocumentProcessingOptions? options = null, DocumentPackageContext? context = null, bool allowUnappliedMappings = false) =>
        FormatSupport.Run(source, output, () =>
        {
            using var model = XlsxModel.Open(source);
            var policyText = forward ? string.Join("\n", model.TextLines()) : "";
            var requested = mappings.ToList();
            var list = FormatSupport.Prepare(policyText, requested, forward, allowUnappliedMappings);
            var built = new XlsxBuilder(model, list, forward).Build();
            var unknown = 0;
            if (forward) XlsxAudit.Run(model, built, list, FormatSupport.AuditMappings(policyText, list, requested));
            else
            {
                XlsxAudit.Package(built);
                using var restored = XlsxModel.FromPackage(OoxmlPackage.OpenBytes(built));
                unknown = DocumentProcessor.UnknownTokens(string.Join("\n", restored.TextLines()), list);
                XlsxAudit.Validate(model, built);
            }
            FormatSupport.Release(output, built);
            return unknown;
        });

    public void Audit(string source, string output, IEnumerable<Mapping> mappings)
    {
        var list = mappings.OrderByDescending(m => m.OriginalValue.Length).ToList();
        using var model = XlsxModel.Open(source);
        XlsxAudit.Run(model, File.ReadAllBytes(output), list);
    }
}

internal sealed class XlsxBuilder
{
    private static readonly XNamespace S = XlsxModel.S;
    private static readonly XNamespace R = OoxmlPackage.R;
    private const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    private static readonly HashSet<string> SheetKeep = new()
    {
        "sheetPr", "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "sheetProtection", "autoFilter", "sortState", "mergeCells",
        "phoneticPr", "conditionalFormatting", "dataValidations", "hyperlinks", "printOptions", "pageMargins", "pageSetup", "headerFooter",
        "rowBreaks", "colBreaks", "ignoredErrors", "tableParts", "legacyDrawing",
    };
    private static readonly HashSet<string> WorkbookKeep = new() { "fileVersion", "workbookPr", "workbookProtection", "bookViews", "sheets", "definedNames", "calcPr" };

    private readonly XlsxModel model;
    private readonly List<Mapping> list;
    private readonly bool forward;
    private readonly OoxmlPackage pkg;
    private readonly List<(string Name, byte[] Data, string? ContentType)> parts = new();
    private HashSet<int> referenced = new();
    private bool keepMetadata;

    public XlsxBuilder(XlsxModel model, List<Mapping> list, bool forward)
    {
        this.model = model; this.list = list; this.forward = forward; pkg = model.Pkg;
    }

    private string P(string text) => FormatSupport.Apply(text, list, forward);
    private string Formula(string formula)
    {
        if (forward && OoxmlText.HasExternalReference(formula))
            throw new InvalidDataException("This workbook has formulas that read from another workbook. Break those links in Excel (Data, Edit Links, Break Link), save, then add the file again. Nothing was released.");
        return OoxmlText.RewriteFormula(formula, P);
    }
    private void Add(string name, XDocument doc, string type) => parts.Add((name, OoxmlPackage.Serialize(doc), type));
    private void AddRaw(string name, byte[] data, string? type) => parts.Add((name, data, type));

    public byte[] Build()
    {
        // Which shared strings some cell uses. The others are stale text and are blanked.
        foreach (var sheet in model.Sheets.Where(s => !s.IsChartSheet))
            foreach (var c in model.Sheet(sheet).Descendants(S + "c").Where(c => (string?)c.Attribute("t") == "s"))
                if (int.TryParse(c.Element(S + "v")?.Value, out var i)) referenced.Add(i);

        keepMetadata = !pkg.Names.Any(n => n.StartsWith("xl/richData/", StringComparison.OrdinalIgnoreCase));
        var wbRels = pkg.Rels(model.WorkbookPart);
        var stylesRel = wbRels.FirstOrDefault(r => r.IsType("styles") && !r.External && pkg.Has(r.Resolved));
        var themeRel = wbRels.FirstOrDefault(r => r.IsType("theme") && !r.External && pkg.Has(r.Resolved));
        var metaRel = keepMetadata ? wbRels.FirstOrDefault(r => r.IsType("sheetMetadata") && !r.External && pkg.Has(r.Resolved)) : null;
        var sstRel = wbRels.FirstOrDefault(r => r.IsType("sharedStrings") && !r.External && pkg.Has(r.Resolved));

        // Package properties are cleared, not copied.
        var core = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(XName.Get("coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties"),
                new XAttribute(XNamespace.Xmlns + "cp", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties"),
                new XAttribute(XNamespace.Xmlns + "dc", "http://purl.org/dc/elements/1.1/"),
                new XAttribute(XNamespace.Xmlns + "dcterms", "http://purl.org/dc/terms/")));
        Add("docProps/core.xml", core, "application/vnd.openxmlformats-package.core-properties+xml");
        Add("_rels/.rels", OoxmlPackage.Relationships(new[]
        {
            ("rId1", RelBase + "officeDocument", model.WorkbookPart),
            ("rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml"),
        }), "application/vnd.openxmlformats-package.relationships+xml");

        // Sheets first, so the workbook relationships know their final names.
        var workbookRels = new List<(string Id, string Type, string Target)>();
        var sheetOut = new Dictionary<string, string>();
        var folder = OoxmlPackage.Folder(model.WorkbookPart);
        string Rel(string part) => folder.Length > 0 && part.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) ? part.Substring(folder.Length + 1) : "/" + part;
        var commentNumber = 0;
        foreach (var sheet in model.Sheets)
        {
            var outName = sheet.IsChartSheet ? "xl/worksheets/sheetEmpty" + (sheet.Index + 1) + ".xml" : sheet.Part;
            sheetOut[sheet.RelId] = outName;
            if (sheet.IsChartSheet)
            {
                Add(outName, new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(S + "worksheet", new XElement(S + "sheetData"))), XlsxModel.WorksheetContentType);
                continue;
            }
            Sheet(sheet, outName, ref commentNumber);
        }

        foreach (var rel in wbRels.Where(r => !r.External))
        {
            if (rel.IsType("worksheet") || rel.IsType("chartsheet"))
            {
                var sheet = model.Sheets.First(s => s.RelId == rel.Id);
                workbookRels.Add((rel.Id, RelBase + "worksheet", Rel(sheetOut[rel.Id])));
            }
            else if (rel == sstRel || rel == stylesRel || rel == themeRel || rel == metaRel)
                workbookRels.Add((rel.Id, rel.Type, Rel(rel.Resolved)));
        }
        Add(OoxmlPackage.RelsNameFor(model.WorkbookPart), OoxmlPackage.Relationships(workbookRels), "application/vnd.openxmlformats-package.relationships+xml");
        Add(model.WorkbookPart, Workbook(), XlsxModel.WorkbookContentType);
        if (sstRel != null) Add(sstRel.Resolved, SharedStrings(), pkg.ContentType(sstRel.Resolved));
        if (stylesRel != null) Add(stylesRel.Resolved, Styles(stylesRel.Resolved), pkg.ContentType(stylesRel.Resolved));
        if (themeRel != null) AddRaw(themeRel.Resolved, pkg.Bytes(themeRel.Resolved), pkg.ContentType(themeRel.Resolved));
        if (metaRel != null) AddRaw(metaRel.Resolved, pkg.Bytes(metaRel.Resolved), pkg.ContentType(metaRel.Resolved));

        var defaults = new Dictionary<string, string>(OoxmlPackage.StandardDefaults);
        if (parts.Any(p => p.Name.EndsWith(".vml"))) defaults["vml"] = "application/vnd.openxmlformats-officedocument.vmlDrawing";
        return OoxmlPackage.Build(parts, defaults);
    }

    private XDocument Workbook()
    {
        var doc = new XDocument(pkg.Xml(model.WorkbookPart));
        var root = doc.Root!;
        foreach (var child in root.Elements().Where(e => !WorkbookKeep.Contains(e.Name.LocalName)).ToList()) child.Remove();
        foreach (var name in root.Descendants(S + "definedName").ToList())
        {
            name.Value = Formula(name.Value);
            foreach (var a in new[] { "customMenu", "help", "statusBar", "vbProcedure", "function", "functionGroupId", "shortcutKey", "publishToServer", "workbookParameter", "xlm" }) name.Attribute(a)?.Remove();
            foreach (var a in new[] { "comment", "description" }) if (name.Attribute(a) is { } attribute) attribute.Value = P(attribute.Value);
        }
        // Names of sheets that were chart sheets stay; the relationship ids are unchanged.
        return doc;
    }

    private XDocument SharedStrings()
    {
        var doc = new XDocument(pkg.Xml(model.SharedStringsPart!));
        var index = 0;
        foreach (var si in doc.Root!.Elements(S + "si").ToList())
        {
            foreach (var guide in si.Elements().Where(e => e.Name.LocalName is "rPh" or "phoneticPr").ToList()) guide.Remove();
            if (!referenced.Contains(index))
            {
                si.RemoveNodes();
                si.Add(new XElement(S + "t"));
            }
            else OoxmlText.ReplaceInRuns(XlsxModel.TextNodes(si), list, forward);
            index++;
        }
        foreach (var ext in doc.Root.Elements(S + "extLst").ToList()) ext.Remove();
        return doc;
    }

    private XDocument Styles(string part)
    {
        var doc = new XDocument(pkg.Xml(part));
        foreach (var style in doc.Descendants(S + "cellStyle"))
            if (style.Attribute("builtinId") == null && style.Attribute("name") is { } name) name.Value = P(name.Value);
        foreach (var ext in doc.Root!.Elements(S + "extLst").ToList()) ext.Remove();
        return doc;
    }

    private void Sheet(XlsxModel.SheetRef sheet, string outName, ref int commentNumber)
    {
        var doc = new XDocument(pkg.Xml(sheet.Part));
        var root = doc.Root!;
        foreach (var child in root.Elements().Where(e => !SheetKeep.Contains(e.Name.LocalName)).ToList()) child.Remove();
        foreach (var attribute in root.Attributes().Where(a => a.Name.Namespace == R).ToList()) attribute.Remove();

        foreach (var c in root.Descendants(S + "c").ToList())
        {
            c.Attribute("vm")?.Remove();
            if (!keepMetadata) c.Attribute("cm")?.Remove();
            foreach (var ext in c.Elements(S + "extLst").ToList()) ext.Remove();
            var f = c.Element(S + "f");
            if (f != null && f.Value.Length > 0) f.Value = Formula(f.Value);
            var type = (string?)c.Attribute("t") ?? "n";
            switch (type)
            {
                case "inlineStr":
                    if (c.Element(S + "is") is { } inline)
                    {
                        foreach (var guide in inline.Elements().Where(e => e.Name.LocalName is "rPh" or "phoneticPr").ToList()) guide.Remove();
                        OoxmlText.ReplaceInRuns(XlsxModel.TextNodes(inline), list, forward);
                    }
                    break;
                case "str":
                    if (c.Element(S + "v") is { } cached) cached.Value = P(cached.Value);
                    break;
                case "n":
                case "d":
                    if (c.Element(S + "v") is { } number)
                    {
                        var changed = P(number.Value);
                        if (changed == number.Value) break;
                        // A number or date that is itself a selected value becomes text.
                        number.Value = changed;
                        if (f != null) c.SetAttributeValue("t", "str");
                        else
                        {
                            c.SetAttributeValue("t", "inlineStr");
                            number.Remove();
                            c.Add(new XElement(S + "is", new XElement(S + "t", changed)));
                        }
                    }
                    break;
            }
        }

        foreach (var link in root.Descendants(S + "hyperlink").ToList())
        {
            if (link.Attribute(R + "id") != null) { link.Remove(); continue; }
            foreach (var a in new[] { "display", "tooltip" }) if (link.Attribute(a) is { } attribute) attribute.Value = P(attribute.Value);
        }
        if (root.Element(S + "hyperlinks") is { } hyperlinks && !hyperlinks.HasElements) hyperlinks.Remove();
        foreach (var v in root.Descendants(S + "dataValidation"))
        {
            foreach (var a in new[] { "prompt", "promptTitle", "error", "errorTitle" }) if (v.Attribute(a) is { } attribute) attribute.Value = P(attribute.Value);
            foreach (var f in v.Elements().Where(e => e.Name.LocalName is "formula1" or "formula2")) f.Value = Formula(f.Value);
        }
        foreach (var rule in root.Descendants(S + "cfRule"))
        {
            if (rule.Attribute("text") is { } text) text.Value = P(text.Value);
            foreach (var f in rule.Elements(S + "formula")) f.Value = Formula(f.Value);
            foreach (var ext in rule.Elements(S + "extLst").ToList()) ext.Remove();
        }
        foreach (var filter in root.Descendants().Where(e => e.Name.LocalName is "filter" or "customFilter").ToList())
            if (filter.Attribute("val") is { } val) val.Value = P(val.Value);
        foreach (var hf in root.Elements(S + "headerFooter").Elements().Where(e => e.Name.LocalName.EndsWith("Header") || e.Name.LocalName.EndsWith("Footer"))) hf.Value = OoxmlText.MapHeader(hf.Value, P);
        foreach (var page in root.Elements(S + "pageSetup")) { page.Attribute(R + "id")?.Remove(); }

        var rels = new List<(string Id, string Type, string Target)>();
        // Tables keep their relationship ids; each table part is rewritten.
        foreach (var rel in pkg.Rels(sheet.Part).Where(r => r.IsType("table") && !r.External && pkg.Has(r.Resolved)))
        {
            Add(rel.Resolved, Table(rel.Resolved), pkg.ContentType(rel.Resolved));
            rels.Add((rel.Id, rel.Type, "/" + rel.Resolved));
        }
        foreach (var part in root.Elements(S + "tableParts").Elements()) { }
        // Comments are rebuilt: neutral authors, replaced text, a fresh drawing for the note boxes.
        var commentsRel = pkg.Rels(sheet.Part).FirstOrDefault(r => r.IsType("comments") && !r.External && pkg.Has(r.Resolved));
        var legacy = root.Element(S + "legacyDrawing");
        if (commentsRel != null)
        {
            commentNumber++;
            var commentsName = "xl/comments" + commentNumber + ".xml";
            var vmlName = "xl/drawings/vmlDrawing" + commentNumber + ".vml";
            var refs = Comments(commentsRel.Resolved, commentsName, vmlName, commentNumber);
            if (refs > 0)
            {
                rels.Add(("rIdComments", RelBase + "comments", "/" + commentsName));
                rels.Add(("rIdVml", RelBase + "vmlDrawing", "/" + vmlName));
                if (legacy == null)
                {
                    legacy = new XElement(S + "legacyDrawing");
                    if (root.Element(S + "tableParts") is { } tp) tp.AddBeforeSelf(legacy); else root.Add(legacy);
                }
                legacy.SetAttributeValue(R + "id", "rIdVml");
            }
            else { legacy?.Remove(); parts.RemoveAll(p => p.Name == commentsName || p.Name == vmlName); commentNumber--; }
        }
        else legacy?.Remove();
        if (rels.Count > 0) Add(OoxmlPackage.RelsNameFor(outName), OoxmlPackage.Relationships(rels), "application/vnd.openxmlformats-package.relationships+xml");
        if (!root.Attributes().Any(a => a.Name.Namespace == XNamespace.Xmlns && a.Name.LocalName == "r") && root.Descendants().Attributes().Any(a => a.Name.Namespace == R))
            root.Add(new XAttribute(XNamespace.Xmlns + "r", OoxmlPackage.RelNs));
        Add(outName, doc, XlsxModel.WorksheetContentType);
    }

    private XDocument Table(string part)
    {
        var doc = new XDocument(pkg.Xml(part));
        var root = doc.Root!;
        root.Attribute("tableType")?.Remove();
        root.Attribute("connectionId")?.Remove();
        foreach (var ext in root.Elements(S + "extLst").ToList()) ext.Remove();
        foreach (var col in root.Descendants(S + "tableColumn"))
        {
            foreach (var a in new[] { "name", "totalsRowLabel" }) if (col.Attribute(a) is { } attribute) attribute.Value = P(attribute.Value);
            col.Attribute("uniqueName")?.Remove();
            col.Attribute("queryTableFieldId")?.Remove();
            foreach (var x in col.Elements(S + "xmlColumnPr").ToList()) x.Remove();
            foreach (var f in col.Elements().Where(e => e.Name.LocalName.EndsWith("Formula"))) f.Value = Formula(f.Value);
            foreach (var ext in col.Elements(S + "extLst").ToList()) ext.Remove();
        }
        foreach (var filter in root.Descendants().Where(e => e.Name.LocalName is "filter" or "customFilter").ToList())
            if (filter.Attribute("val") is { } val) val.Value = P(val.Value);
        return doc;
    }

    // Returns the number of comments written.
    private int Comments(string source, string commentsName, string vmlName, int sheetNumber)
    {
        var src = pkg.Xml(source).Root!;
        var authors = src.Element(S + "authors")?.Elements(S + "author").Count() ?? 0;
        var outRoot = new XElement(S + "comments");
        var authorList = new XElement(S + "authors");
        for (var i = 0; i < Math.Max(authors, 1); i++) authorList.Add(new XElement(S + "author", "Author " + (i + 1)));
        outRoot.Add(authorList);
        var commentList = new XElement(S + "commentList");
        var shapes = new List<(int Row, int Col)>();
        foreach (var comment in src.Descendants(S + "comment"))
        {
            var reference = (string?)comment.Attribute("ref") ?? "";
            var copy = new XElement(S + "comment", new XAttribute("ref", reference), new XAttribute("authorId", (string?)comment.Attribute("authorId") ?? "0"), new XAttribute("shapeId", "0"));
            var text = new XElement(comment.Element(S + "text") ?? new XElement(S + "text"));
            foreach (var guide in text.Elements().Where(e => e.Name.LocalName is "rPh" or "phoneticPr").ToList()) guide.Remove();
            OoxmlText.ReplaceInRuns(XlsxModel.TextNodes(text), list, forward);
            copy.Add(text);
            commentList.Add(copy);
            shapes.Add(CellPosition(reference));
        }
        outRoot.Add(commentList);
        if (shapes.Count == 0) return 0;
        Add(commentsName, new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), outRoot), "application/vnd.openxmlformats-officedocument.spreadsheetml.comments+xml");
        AddRaw(vmlName, Encoding.UTF8.GetBytes(Vml(shapes, sheetNumber)), null);
        return shapes.Count;
    }

    private static (int Row, int Col) CellPosition(string reference)
    {
        var m = Regex.Match(reference, @"^([A-Za-z]+)(\d+)$");
        if (!m.Success) throw new InvalidDataException("A comment points at a cell address that cannot be read. The workbook is damaged.");
        var col = 0;
        foreach (var ch in m.Groups[1].Value.ToUpperInvariant()) col = col * 26 + (ch - 'A' + 1);
        return (int.Parse(m.Groups[2].Value) - 1, col - 1);
    }

    private static string Vml(List<(int Row, int Col)> shapes, int sheetNumber)
    {
        var b = new StringBuilder();
        b.Append("<xml xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:x=\"urn:schemas-microsoft-com:office:excel\">");
        b.Append("<o:shapelayout v:ext=\"edit\"><o:idmap v:ext=\"edit\" data=\"" + sheetNumber + "\"/></o:shapelayout>");
        b.Append("<v:shapetype id=\"_x0000_t202\" coordsize=\"21600,21600\" o:spt=\"202\" path=\"m,l,21600r21600,l21600,xe\"><v:stroke joinstyle=\"miter\"/><v:path gradientshapeok=\"t\" o:connecttype=\"rect\"/></v:shapetype>");
        var id = sheetNumber * 1024;
        foreach (var (row, col) in shapes)
        {
            id++;
            b.Append("<v:shape id=\"_x0000_s" + id + "\" type=\"#_x0000_t202\" style=\"position:absolute;margin-left:80pt;margin-top:5pt;width:108pt;height:59pt;z-index:" + (id - sheetNumber * 1024) + ";visibility:hidden\" fillcolor=\"#ffffe1\" o:insetmode=\"auto\">");
            b.Append("<v:fill color2=\"#ffffe1\"/><v:shadow on=\"t\" color=\"black\" obscured=\"t\"/><v:path o:connecttype=\"none\"/><v:textbox style=\"mso-direction-alt:auto\"><div style=\"text-align:left\"></div></v:textbox>");
            b.Append("<x:ClientData ObjectType=\"Note\"><x:MoveWithCells/><x:SizeWithCells/><x:Anchor>" + (col + 1) + ", 15, " + Math.Max(row - 1, 0) + ", 10, " + (col + 3) + ", 15, " + (row + 3) + ", 4</x:Anchor><x:AutoFill>False</x:AutoFill><x:Row>" + row + "</x:Row><x:Column>" + col + "</x:Column></x:ClientData></v:shape>");
        }
        b.Append("</xml>");
        return b.ToString();
    }
}

internal static class XlsxAudit
{
    private static readonly XNamespace S = XlsxModel.S;

    private static readonly Regex AllowedParts = new(
        @"^(\[Content_Types\]\.xml|_rels/\.rels|docProps/core\.xml|xl/workbook\.xml|xl/_rels/workbook\.xml\.rels|xl/sharedStrings\.xml|xl/styles\.xml|xl/metadata\.xml|" +
        @"xl/theme/theme\d+\.xml|xl/worksheets/[A-Za-z0-9]+\.xml|xl/worksheets/_rels/[A-Za-z0-9]+\.xml\.rels|xl/tables/table\d+\.xml|xl/comments\d+\.xml|xl/drawings/vmlDrawing\d+\.vml)$", RegexOptions.IgnoreCase);

    // Attributes that hold cell addresses, indexes and numbers, never document text.
    private static readonly HashSet<string> Structural = new(StringComparer.Ordinal)
    {
        "r", "ref", "sqref", "s", "t", "si", "count", "uniqueCount", "spans", "min", "max", "width", "ht", "xfId", "numFmtId", "fontId", "fillId", "borderId",
        "id", "uid", "guid", "authorId", "shapeId", "sheetId", "activeTab", "firstSheet", "topLeftCell", "activeCell", "style", "dxfId", "tableType",
        "rgb", "theme", "tint", "indexed", "space", "Ignorable", "xSplit", "ySplit", "zoomScale", "zoomScaleNormal", "workbookViewId", "tabSelected",
    };

    public static bool IsAudited(XAttribute attribute) => !Structural.Contains(attribute.Name.LocalName);

    // Package-level rules that hold for any export: only allowed parts, nothing external, every reference resolves.
    public static void Package(byte[] built)
    {
        using var package = OoxmlPackage.OpenBytes(built);
        foreach (var name in package.Names)
        {
            if (!AllowedParts.IsMatch(name)) throw new InvalidDataException("The output contains a part that is not on the allowed list (" + name + "). Export was blocked because its content cannot be checked.");
        }
        foreach (var name in package.Names.Where(n => n.EndsWith(".xml") || n.EndsWith(".rels") || n.EndsWith(".vml")))
            package.Xml(name);
        OoxmlPackage.AssertSelfContained(package);
    }

    public static void Validate(XlsxModel source, byte[] built)
    {
        if (string.IsNullOrEmpty(source.SourcePath)) return;
        var validator = new OpenXmlValidator(FileFormatVersions.Office2021);
        var originalErrors = new Dictionary<string, int>();
        // A source the SDK cannot open has no baseline; the protected copy must still open and validate.
        try
        {
            using var original = SpreadsheetDocument.Open(source.SourcePath, false);
            originalErrors = validator.Validate(original).GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        }
        catch (OpenXmlPackageException) { }
        using var protectedBook = SpreadsheetDocument.Open(new MemoryStream(built, false), false);
        foreach (var error in validator.Validate(protectedBook))
        {
            var key = Key(error);
            if (originalErrors.TryGetValue(key, out var remaining) && remaining > 0) { originalErrors[key] = remaining - 1; continue; }
            throw new InvalidDataException("The protected copy has a new Excel structure error (code " + error.Id + " in " + error.Part?.Uri + " at " + error.Path?.XPath + "). Export was blocked. No file was released.");
        }
    }
    private static string Key(ValidationErrorInfo e) => string.Join("|", e.Id, e.Description, e.Part?.Uri, e.Path?.XPath);

    public static void Run(XlsxModel source, byte[] built, List<Mapping> list, List<Mapping>? auditMappings = null)
    {
        Package(built);
        using var package = OoxmlPackage.OpenBytes(built);
        var scanner = FormatSupport.Scanner(list);
        foreach (var name in package.Names.Where(n => n.EndsWith(".xml") || n.EndsWith(".rels") || n.EndsWith(".vml")))
        {
            var hit = scanner.Find(package.Xml(name), IsAudited);
            if (hit == null) continue;
            var hint = name == "xl/workbook.xml" || name.StartsWith("xl/tables/") || name.StartsWith("xl/worksheets/")
                ? " Sheet, table and named range names cannot be renamed safely, so rename it in Excel first, then add the file again."
                : "";
            var error = FormatSupport.Residue(hit, name, "");
            throw new InvalidDataException(error.Message + hint);
        }
        foreach (var name in package.Names.Where(n => n.StartsWith("xl/worksheets/") && n.EndsWith(".xml")))
            foreach (var hf in package.Xml(name).Descendants(S + "headerFooter").Elements())
                foreach (var piece in OoxmlText.HeaderPieces(hf.Value))
                {
                    var hit = FormatSupport.FindResidue(scanner, list, piece);
                    if (hit != null) throw FormatSupport.ResidueInText(hit, name, "a header or footer");
                }
        // Formatting codes can interrupt a full identifying value. Join only
        // literal header/footer pieces for an audit, never change their formatting.
        var auditScanner = FormatSupport.Scanner(auditMappings ?? list);
        foreach (var name in package.Names.Where(n => n.StartsWith("xl/worksheets/") && n.EndsWith(".xml")))
            foreach (var hf in package.Xml(name).Descendants(S + "headerFooter").Elements())
                if (auditScanner.FindHiddenVariant(string.Concat(OoxmlText.HeaderPieces(hf.Value))) != null)
                    throw new InvalidDataException("Export blocked: an identifying value remains across header or footer formatting. No file was released.");
        // Decode only literal CHAR/UNICHAR calls for the audit. Do not evaluate
        // formulas, follow references, or rewrite business calculations.
        foreach (var name in package.Names.Where(n => n.StartsWith("xl/worksheets/") && n.EndsWith(".xml")))
            foreach (var formula in package.Xml(name).Descendants(S + "f"))
            {
                var decoded = Regex.Replace(formula.Value, @"\b(?:CHAR|UNICHAR)\s*\(\s*([0-9]{1,7})\s*\)", match =>
                {
                    var scalar = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    return scalar is > 0 and <= 0x10FFFF && scalar is not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(scalar) : match.Value;
                }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (auditScanner.FindHiddenVariant(decoded) != null)
                    throw new InvalidDataException("Export blocked: a formula can reconstruct an identifying value. No file was released. Remove that value from the formula in Excel, then add the file again.");
            }
        FormatSupport.AuditHiddenPackage(package, auditMappings ?? list);
        Structure(source, package, list);
        Validate(source, built);
    }

    private static void Structure(XlsxModel source, OoxmlPackage outPackage, List<Mapping> list)
    {
        using var output = XlsxModel.FromPackage(outPackage);
        if (output.Sheets.Count != source.Sheets.Count) throw new InvalidDataException("Export blocked: the number of sheets changed.");
        if (output.SharedText.Count != source.SharedText.Count) throw new InvalidDataException("Export blocked: the shared text table changed size.");
        for (var i = 0; i < source.Sheets.Count; i++)
        {
            var a = source.Sheets[i];
            var b = output.Sheets[i];
            if (a.Name != b.Name || a.State != b.State) throw new InvalidDataException("Export blocked: a sheet name or its hidden state changed.");
            if (a.IsChartSheet) continue;
            var before = source.Cells(a).ToList();
            var after = output.Cells(b).ToList();
            if (before.Count != after.Count) throw new InvalidDataException("Export blocked: the number of cells changed in sheet " + (i + 1) + ".");
            var sourceDoc = source.Sheet(a);
            var outDoc = output.Sheet(b);
            if (sourceDoc.Descendants(S + "row").Count() != outDoc.Descendants(S + "row").Count()) throw new InvalidDataException("Export blocked: the number of rows changed in sheet " + (i + 1) + ".");
            if (sourceDoc.Descendants(S + "mergeCell").Count() != outDoc.Descendants(S + "mergeCell").Count()) throw new InvalidDataException("Export blocked: merged cells changed in sheet " + (i + 1) + ".");
            for (var k = 0; k < before.Count; k++)
            {
                var x = before[k];
                var y = after[k];
                var where = "sheet " + (i + 1) + ", cell " + x.Ref;
                if (x.Ref != y.Ref) throw new InvalidDataException("Export blocked: cell positions changed in sheet " + (i + 1) + ".");
                if ((string?)x.Element.Attribute("s") != (string?)y.Element.Attribute("s")) throw new InvalidDataException("Export blocked: a cell style changed (" + where + ").");
                if ((x.Formula == null) != (y.Formula == null)) throw new InvalidDataException("Export blocked: a formula was added or removed (" + where + ").");
                if (x.Formula != null && OoxmlText.RewriteFormula(x.Formula, t => FormatSupport.Protect(t, list)) != y.Formula)
                    throw new InvalidDataException("Export blocked: a formula changed outside text values (" + where + ").");
                var convertible = x.Type is "n" or "d";
                if (x.Type != y.Type && !(convertible && y.Type is "inlineStr" or "str"))
                    throw new InvalidDataException("Export blocked: a cell type changed (" + where + ").");
                if (x.Type is "e" or "b") { if (x.Text != y.Text) throw new InvalidDataException("Export blocked: a cell value changed (" + where + ")."); continue; }
                if (FormatSupport.CanonicalSpace(FormatSupport.Protect(x.Text, list)) != FormatSupport.CanonicalSpace(y.Text))
                    throw new InvalidDataException("Export blocked: text outside the selected replacements changed (" + where + ").");
            }
            if (sourceDoc.Descendants(S + "dataValidation").Count() != outDoc.Descendants(S + "dataValidation").Count()) throw new InvalidDataException("Export blocked: data validation changed in sheet " + (i + 1) + ".");
            if (source.SheetRels(a, "table").Count() != output.SheetRels(b, "table").Count()) throw new InvalidDataException("Export blocked: tables changed in sheet " + (i + 1) + ".");
            var commentsBefore = source.SheetRels(a, "comments").Sum(r => source.Pkg.Xml(r.Resolved).Descendants(S + "comment").Count());
            var commentsAfter = output.SheetRels(b, "comments").Sum(r => output.Pkg.Xml(r.Resolved).Descendants(S + "comment").Count());
            if (commentsBefore != commentsAfter) throw new InvalidDataException("Export blocked: comments changed in sheet " + (i + 1) + ".");
        }
    }
}

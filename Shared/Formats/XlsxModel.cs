using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Desensitizer;

// A read view of an .xlsx workbook: sheets, shared strings and the text each
// part holds. Used for the review text, the removal report and the audit.
internal sealed class XlsxModel : IDisposable
{
    public static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    public const string WorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    public const string WorksheetContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

    public sealed record SheetRef(int Index, string Name, string State, string RelId, string Part, bool IsChartSheet)
    {
        public bool Hidden => State is "hidden" or "veryHidden";
    }

    public sealed record Cell(XElement Element, string Ref, string Type, string Text, string? Formula);

    public OoxmlPackage Pkg { get; }
    public string SourcePath { get; }
    public string WorkbookPart { get; private set; } = "";
    public XDocument Workbook { get; private set; } = new();
    public List<SheetRef> Sheets { get; } = new();
    public string? SharedStringsPart { get; private set; }
    public List<XElement> SharedItems { get; } = new();
    public List<string> SharedText { get; } = new();

    private XlsxModel(OoxmlPackage pkg, string path) { Pkg = pkg; SourcePath = path; }

    public static XlsxModel Open(string path)
    {
        var pkg = OoxmlPackage.Open(path, "Excel workbook (.xlsx)");
        try
        {
            var model = new XlsxModel(pkg, path);
            model.Load(true);
            return model;
        }
        catch { pkg.Dispose(); throw; }
    }

    // A workbook built by this program (the protected copy), read for the audit.
    public static XlsxModel FromPackage(OoxmlPackage pkg)
    {
        var model = new XlsxModel(pkg, "");
        model.Load(false);
        return model;
    }

    private void Load(bool strict)
    {
        if (strict && Pkg.HasMacroContent())
            throw new InvalidDataException("This workbook contains macros or active controls. Save a copy as .xlsx (Excel Workbook), which removes macros, then add that copy.");
        var root = Pkg.Rels("").FirstOrDefault(r => r.IsType("officeDocument"));
        if (root == null || root.External || !Pkg.Has(root.Resolved)) throw OoxmlPackage.NotValid("Excel workbook (.xlsx)");
        WorkbookPart = root.Resolved;
        if (strict && !string.Equals(Pkg.ContentType(WorkbookPart), WorkbookContentType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This file is not a plain Excel workbook (.xlsx). Templates and macro workbooks are not accepted. Save a copy as .xlsx and add that copy.");
        Workbook = Pkg.Xml(WorkbookPart);
        var rels = Pkg.Rels(WorkbookPart).ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
        var index = 0;
        foreach (var sheet in Workbook.Descendants(S + "sheet"))
        {
            var id = (string?)sheet.Attribute(OoxmlPackage.R + "id") ?? "";
            if (!rels.TryGetValue(id, out var rel) || rel.External || !Pkg.Has(rel.Resolved)) throw OoxmlPackage.NotValid("Excel workbook (.xlsx)");
            if (!rel.IsType("worksheet") && !rel.IsType("chartsheet")) throw new InvalidDataException("This workbook has a sheet of a kind that is not supported (for example a macro or dialog sheet). Save a copy as a normal .xlsx and add that copy.");
            Sheets.Add(new SheetRef(index++, (string?)sheet.Attribute("name") ?? "", (string?)sheet.Attribute("state") ?? "visible", id, rel.Resolved, rel.IsType("chartsheet")));
        }
        var sst = Pkg.Rels(WorkbookPart).FirstOrDefault(r => r.IsType("sharedStrings") && !r.External && Pkg.Has(r.Resolved));
        if (sst != null)
        {
            SharedStringsPart = sst.Resolved;
            foreach (var si in Pkg.Xml(sst.Resolved).Root!.Elements(S + "si"))
            {
                SharedItems.Add(si);
                SharedText.Add(PlainText(si));
            }
        }
    }

    // Text of a string item or comment, without phonetic guides.
    public static string PlainText(XElement item) => string.Concat(TextNodes(item).Select(t => t.Value));
    public static List<XElement> TextNodes(XElement item) =>
        item.Descendants(S + "t").Where(t => !t.Ancestors().TakeWhile(a => a != item).Any(a => a.Name == S + "rPh")).ToList();

    public XDocument Sheet(SheetRef sheet) => Pkg.Xml(sheet.Part);

    public static string Label(string name) => Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.]*$") ? name : "'" + name.Replace("'", "''") + "'";
    public string Label(SheetRef sheet) => (sheet.Hidden ? "[hidden] " : "") + Label(sheet.Name);

    public IEnumerable<Cell> Cells(SheetRef sheet)
    {
        if (sheet.IsChartSheet) yield break;
        foreach (var c in Sheet(sheet).Descendants(S + "c"))
        {
            var type = (string?)c.Attribute("t") ?? "n";
            var formula = c.Element(S + "f")?.Value;
            string text;
            switch (type)
            {
                case "s":
                    if (!int.TryParse(c.Element(S + "v")?.Value, out var i) || i < 0 || i >= SharedText.Count) throw new InvalidDataException("A cell points at a shared string that does not exist. The workbook is damaged.");
                    text = SharedText[i];
                    break;
                case "inlineStr": text = c.Element(S + "is") is { } inline ? PlainText(inline) : ""; break;
                default: text = c.Element(S + "v")?.Value ?? ""; break;
            }
            yield return new Cell(c, (string?)c.Attribute("r") ?? "", type, text, formula);
        }
    }

    public IEnumerable<OoxmlPackage.Rel> SheetRels(SheetRef sheet, string type) => Pkg.Rels(sheet.Part).Where(r => r.IsType(type) && !r.External && Pkg.Has(r.Resolved));

    // ------------------------------------------------------------------ review text

    private static readonly string[] CoreProperties = { "title", "subject", "creator", "keywords", "description", "lastModifiedBy", "category", "contentStatus", "identifier", "language", "version" };

    public List<string> TextLines()
    {
        var lines = new List<string>();
        void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lines.Add(label + ": " + OoxmlText.Flat(value));
        }

        // Document properties.
        var core = Pkg.Rels("").FirstOrDefault(r => r.IsType("core-properties") && !r.External && Pkg.Has(r.Resolved));
        if (core != null)
            foreach (var e in Pkg.Xml(core.Resolved).Root!.Elements().Where(e => CoreProperties.Contains(e.Name.LocalName))) Add("Document property " + e.Name.LocalName, e.Value);
        var app = Pkg.Rels("").FirstOrDefault(r => r.IsType("extended-properties") && !r.External && Pkg.Has(r.Resolved));
        if (app != null)
            foreach (var e in Pkg.Xml(app.Resolved).Root!.Elements().Where(e => e.Name.LocalName is "Company" or "Manager" or "HyperlinkBase")) Add("Document property " + e.Name.LocalName, e.Value);
        var custom = Pkg.Rels("").FirstOrDefault(r => r.IsType("custom-properties") && !r.External && Pkg.Has(r.Resolved));
        if (custom != null)
            foreach (var p in Pkg.Xml(custom.Resolved).Root!.Elements().Where(e => e.Name.LocalName == "property"))
                Add("Custom document property " + (string?)p.Attribute("name"), p.Elements().FirstOrDefault()?.Value);

        // Sheet and defined names.
        foreach (var sheet in Sheets) Add(sheet.Hidden ? "Hidden sheet name" : "Sheet name", sheet.Name);
        foreach (var name in Workbook.Descendants(S + "definedName"))
        {
            Add("Defined name", (string?)name.Attribute("name") + " = " + name.Value);
            Add("Defined name comment", (string?)name.Attribute("comment"));
            Add("Defined name description", (string?)name.Attribute("description"));
        }

        // Cell style names.
        var styles = Pkg.Rels(WorkbookPart).FirstOrDefault(r => r.IsType("styles") && !r.External && Pkg.Has(r.Resolved));
        if (styles != null)
            foreach (var style in Pkg.Xml(styles.Resolved).Descendants(S + "cellStyle").Where(e => e.Attribute("builtinId") == null)) Add("Cell style name", (string?)style.Attribute("name"));

        foreach (var sheet in Sheets) SheetLines(sheet, lines);
        return lines;
    }

    private void SheetLines(SheetRef sheet, List<string> lines)
    {
        var label = Label(sheet);
        void Add(string text, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lines.Add(label + text + ": " + OoxmlText.Flat(value));
        }
        if (!sheet.IsChartSheet)
        {
            var doc = Sheet(sheet);
            foreach (var cell in Cells(sheet))
            {
                if (cell.Type is "e" or "b") { if (cell.Formula != null && cell.Formula.Contains('"')) Add("!" + cell.Ref + " formula", cell.Formula); continue; }
                Add("!" + cell.Ref, cell.Text);
                if (cell.Formula != null && cell.Formula.Contains('"')) Add("!" + cell.Ref + " formula", cell.Formula);
            }
            var root = doc.Root!;
            foreach (var hf in root.Elements(S + "headerFooter").Elements()) Add(" " + hf.Name.LocalName, OoxmlText.PlainHeader(hf.Value));
            foreach (var link in root.Descendants(S + "hyperlink"))
            {
                Add("!" + (string?)link.Attribute("ref") + " hyperlink text", (string?)link.Attribute("display"));
                Add("!" + (string?)link.Attribute("ref") + " hyperlink tooltip", (string?)link.Attribute("tooltip"));
            }
            foreach (var v in root.Descendants(S + "dataValidation"))
            {
                var at = "!" + (string?)v.Attribute("sqref");
                foreach (var a in new[] { "promptTitle", "prompt", "errorTitle", "error" }) Add(at + " validation message", (string?)v.Attribute(a));
                Add(at + " validation list", v.Element(S + "formula1")?.Value);
                Add(at + " validation list", v.Element(S + "formula2")?.Value);
            }
            foreach (var rule in root.Descendants(S + "cfRule"))
            {
                Add(" conditional formatting text", (string?)rule.Attribute("text"));
                foreach (var f in rule.Elements(S + "formula")) Add(" conditional formatting formula", f.Value);
            }
            foreach (var f in root.Elements(S + "autoFilter").Descendants().Where(e => e.Name.LocalName is "filter" or "customFilter")) Add(" filter value", (string?)f.Attribute("val"));
            foreach (var table in SheetRels(sheet, "table"))
            {
                var t = Pkg.Xml(table.Resolved).Root!;
                Add(" table name", (string?)t.Attribute("name"));
                Add(" table display name", (string?)t.Attribute("displayName"));
                foreach (var col in t.Descendants(S + "tableColumn"))
                {
                    Add(" table column", (string?)col.Attribute("name"));
                    Add(" table total label", (string?)col.Attribute("totalsRowLabel"));
                    foreach (var f in col.Elements().Where(e => e.Name.LocalName.EndsWith("Formula"))) Add(" table column formula", f.Value);
                }
            }
            foreach (var comments in SheetRels(sheet, "comments"))
                foreach (var comment in Pkg.Xml(comments.Resolved).Descendants(S + "comment"))
                    Add("!" + (string?)comment.Attribute("ref") + " comment", PlainText(comment));
        }
        foreach (var drawing in SheetRels(sheet, "drawing"))
            foreach (var chart in Pkg.Rels(drawing.Resolved).Where(r => r.IsType("chart") && !r.External && Pkg.Has(r.Resolved)))
            {
                var chartDoc = Pkg.Xml(chart.Resolved);
                foreach (var t in chartDoc.Descendants(A + "t")) Add(" chart text", t.Value);
                foreach (var tx in chartDoc.Descendants(C + "tx").SelectMany(x => x.Descendants(C + "v"))) Add(" chart series name", tx.Value);
            }
    }

    // ------------------------------------------------------------------ removals

    public List<string> Removals()
    {
        var found = new List<string>();
        void Count(int n, string one, string many) { if (n > 0) found.Add(n == 1 ? one : n + " " + many); }
        var names = Pkg.Names.ToList();
        Count(names.Count(n => Regex.IsMatch(n, @"^xl/media/", RegexOptions.IgnoreCase)), "1 picture is removed", "pictures are removed");
        Count(names.Count(n => Regex.IsMatch(n, @"^xl/charts/chart\d+\.xml$", RegexOptions.IgnoreCase)), "1 chart is removed", "charts are removed");
        Count(Sheets.Count(s => s.IsChartSheet), "1 chart sheet is replaced by an empty sheet", "chart sheets are replaced by empty sheets");
        var drawingShapes = names.Where(n => Regex.IsMatch(n, @"^xl/drawings/drawing\d+\.xml$", RegexOptions.IgnoreCase)).ToList();
        Count(drawingShapes.Count, "1 drawing layer (shapes, pictures, charts) is removed", "drawing layers (shapes, pictures, charts) are removed");
        Count(names.Count(n => n.StartsWith("xl/embeddings/", StringComparison.OrdinalIgnoreCase)), "1 embedded object is removed", "embedded objects are removed");
        Count(names.Count(n => Regex.IsMatch(n, @"^xl/pivotTables/pivotTable\d+\.xml$", RegexOptions.IgnoreCase)), "1 pivot table definition is removed (its cell values stay as plain values)", "pivot table definitions are removed (their cell values stay as plain values)");
        Count(names.Count(n => Regex.IsMatch(n, @"^xl/pivotCache/pivotCacheDefinition\d+\.xml$", RegexOptions.IgnoreCase)), "1 pivot cache with source data is removed", "pivot caches with source data are removed");
        Count(names.Count(n => Regex.IsMatch(n, @"^xl/externalLinks/externalLink\d+\.xml$", RegexOptions.IgnoreCase)), "1 link to another workbook is removed", "links to other workbooks are removed");
        Count(names.Count(n => Regex.IsMatch(n, @"^xl/threadedComments/", RegexOptions.IgnoreCase) && n.EndsWith(".xml")), "1 threaded comment thread is reduced to its plain note", "threaded comment threads are reduced to their plain notes");
        Count(names.Count(n => n.StartsWith("xl/printerSettings/", StringComparison.OrdinalIgnoreCase)), "1 printer setting is removed", "printer settings are removed");
        Count(names.Count(n => n.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".xml") && !n.Contains("/_rels/")), "1 custom data part is removed", "custom data parts are removed");
        Count(names.Count(n => Regex.IsMatch(n, @"^xl/(connections\.xml|queryTables/)", RegexOptions.IgnoreCase)), "1 data connection part is removed", "data connection parts are removed");
        var external = Sheets.SelectMany(s => Pkg.Rels(s.Part)).Count(r => r.External && r.IsType("hyperlink"));
        Count(external, "1 web or file link is removed (the visible text stays)", "web or file links are removed (the visible text stays)");
        Count(Sheets.Count(s => s.Hidden), "1 hidden sheet is included in the review and stays hidden", "hidden sheets are included in the review and stay hidden");
        Count(Pkg.Names.Count(n => Regex.IsMatch(n, @"^xl/comments\d*\.xml$", RegexOptions.IgnoreCase)), "comment authors in 1 sheet are replaced by a neutral name", "comment author lists are replaced by a neutral name");
        found.Add("Document properties (author, company, titles) are cleared");
        return found;
    }

    public void Dispose() => Pkg.Dispose();
}

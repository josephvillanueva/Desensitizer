using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Desensitizer;

// A read view of a .pptx presentation: slides in order, with their layouts and
// masters, notes, comments and chart text. Used for the review text, the
// removal report and the audit.
internal sealed class PptxModel : IDisposable
{
    public static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace R = OoxmlPackage.R;
    public const string PresentationContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";

    public sealed record SlideRef(int Number, string Part, string RelId, bool Hidden);

    public OoxmlPackage Pkg { get; }
    public string SourcePath { get; }
    public string PresentationPart { get; private set; } = "";
    public XDocument Presentation { get; private set; } = new();
    public List<SlideRef> Slides { get; } = new();

    private PptxModel(OoxmlPackage pkg, string path) { Pkg = pkg; SourcePath = path; }

    public static PptxModel Open(string path)
    {
        var pkg = OoxmlPackage.Open(path, "PowerPoint presentation (.pptx)");
        try
        {
            var model = new PptxModel(pkg, path);
            model.Load(true);
            return model;
        }
        catch { pkg.Dispose(); throw; }
    }

    public static PptxModel FromPackage(OoxmlPackage pkg)
    {
        var model = new PptxModel(pkg, "");
        model.Load(false);
        return model;
    }

    private void Load(bool strict)
    {
        if (strict && Pkg.HasMacroContent())
            throw new InvalidDataException("This presentation contains macros or active controls. Save a copy as .pptx (PowerPoint Presentation), which removes macros, then add that copy.");
        var root = Pkg.Rels("").FirstOrDefault(r => r.IsType("officeDocument"));
        if (root == null || root.External || !Pkg.Has(root.Resolved)) throw OoxmlPackage.NotValid("PowerPoint presentation (.pptx)");
        PresentationPart = root.Resolved;
        if (strict && !string.Equals(Pkg.ContentType(PresentationPart), PresentationContentType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This file is not a plain PowerPoint presentation (.pptx). Slide shows, templates and macro files are not accepted. Save a copy as .pptx and add that copy.");
        Presentation = Pkg.Xml(PresentationPart);
        var rels = Pkg.Rels(PresentationPart).ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
        var number = 0;
        foreach (var id in Presentation.Descendants(P + "sldId"))
        {
            var relId = (string?)id.Attribute(R + "id") ?? "";
            if (!rels.TryGetValue(relId, out var rel) || rel.External || !Pkg.Has(rel.Resolved) || !rel.IsType("slide")) throw OoxmlPackage.NotValid("PowerPoint presentation (.pptx)");
            number++;
            var hidden = (string?)Pkg.Xml(rel.Resolved).Root?.Attribute("show") == "0";
            Slides.Add(new SlideRef(number, rel.Resolved, relId, hidden));
        }
    }

    public IEnumerable<OoxmlPackage.Rel> RelsOf(string part, string type) => Pkg.Rels(part).Where(r => r.IsType(type) && !r.External && Pkg.Has(r.Resolved));

    // The paragraphs of a drawing part, one string each, with line breaks as spaces. Same notion of text as the transform.
    public static IEnumerable<(XElement Paragraph, string Text)> Paragraphs(XContainer root) =>
        root.Descendants(A + "p").Select(p => (p, ParagraphText(p)));

    public static string ParagraphText(XElement paragraph) =>
        string.Concat(paragraph.Descendants().Where(x => x.Name == A + "t" || x.Name == A + "br").Select(x => x.Name == A + "t" ? x.Value : " "));

    private static readonly Regex DefaultPrompt = new(@"^(Click to edit|Second level|Third level|Fourth level|Fifth level|‹#›|Edit Master)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GenericName = new(@"^(Title|Subtitle|Content Placeholder|Text Placeholder|Picture Placeholder|Footer Placeholder|Slide Number Placeholder|Date Placeholder|Notes Placeholder|Slide Image Placeholder|Header Placeholder|Picture|Rectangle|Rounded Rectangle|Oval|TextBox|Text Box|Table|Group|Connector|Straight Connector|Straight Arrow Connector|Elbow Connector|Graphic|Chart|Object|Placeholder|Freeform|Arrow|Isosceles Triangle|Line|Content|Subtitle|Diagram|Ink|Media|Video|Audio)( ?\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public List<string> TextLines()
    {
        var lines = new List<string>();
        void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lines.Add(label + ": " + OoxmlText.Flat(value));
        }
        var core = Pkg.Rels("").FirstOrDefault(r => r.IsType("core-properties") && !r.External && Pkg.Has(r.Resolved));
        if (core != null)
            foreach (var e in Pkg.Xml(core.Resolved).Root!.Elements().Where(e => e.Name.LocalName is "title" or "subject" or "creator" or "keywords" or "description" or "lastModifiedBy" or "category" or "contentStatus" or "identifier" or "language" or "version")) Add("Document property " + e.Name.LocalName, e.Value);
        var app = Pkg.Rels("").FirstOrDefault(r => r.IsType("extended-properties") && !r.External && Pkg.Has(r.Resolved));
        if (app != null)
            foreach (var e in Pkg.Xml(app.Resolved).Root!.Elements().Where(e => e.Name.LocalName is "Company" or "Manager" or "HyperlinkBase")) Add("Document property " + e.Name.LocalName, e.Value);
        var custom = Pkg.Rels("").FirstOrDefault(r => r.IsType("custom-properties") && !r.External && Pkg.Has(r.Resolved));
        if (custom != null)
            foreach (var p in Pkg.Xml(custom.Resolved).Root!.Elements().Where(e => e.Name.LocalName == "property"))
                Add("Custom document property " + (string?)p.Attribute("name"), p.Elements().FirstOrDefault()?.Value);

        foreach (var slide in Slides)
        {
            var label = (slide.Hidden ? "[hidden] " : "") + "Slide " + slide.Number;
            var doc = Pkg.Xml(slide.Part);
            DrawingLines(doc, label, lines, false);
            foreach (var notes in RelsOf(slide.Part, "notesSlide"))
                foreach (var (_, text) in Paragraphs(Pkg.Xml(notes.Resolved))) Add(label + " notes", text);
            foreach (var comments in RelsOf(slide.Part, "comments"))
                foreach (var text in Pkg.Xml(comments.Resolved).Descendants(P + "text")) Add(label + " comment", text.Value);
            foreach (var graphic in doc.Descendants().Where(e => e.Name.LocalName == "chart" && e.Attribute(R + "id") != null))
            {
                var rel = Pkg.Rels(slide.Part).FirstOrDefault(r => r.Id == (string?)graphic.Attribute(R + "id") && !r.External && Pkg.Has(r.Resolved));
                if (rel == null) continue;
                var chart = Pkg.Xml(rel.Resolved);
                foreach (var t in chart.Descendants(A + "t")) Add(label + " chart text", t.Value);
                foreach (var v in chart.Descendants().Where(e => e.Name.LocalName == "tx").SelectMany(x => x.Descendants().Where(d => d.Name.LocalName == "v"))) Add(label + " chart series name", v.Value);
            }
        }
        var masterNumber = 0;
        foreach (var master in Pkg.Rels(PresentationPart).Where(r => r.IsType("slideMaster") && !r.External && Pkg.Has(r.Resolved)))
        {
            masterNumber++;
            DrawingLines(Pkg.Xml(master.Resolved), "Slide master " + masterNumber, lines, true);
            var layoutNumber = 0;
            foreach (var layout in RelsOf(master.Resolved, "slideLayout"))
            {
                layoutNumber++;
                DrawingLines(Pkg.Xml(layout.Resolved), "Slide master " + masterNumber + " layout " + layoutNumber, lines, true);
            }
        }
        foreach (var notesMaster in Pkg.Rels(PresentationPart).Where(r => r.IsType("notesMaster") && !r.External && Pkg.Has(r.Resolved)))
            DrawingLines(Pkg.Xml(notesMaster.Resolved), "Notes master", lines, true);
        foreach (var handout in Pkg.Rels(PresentationPart).Where(r => r.IsType("handoutMaster") && !r.External && Pkg.Has(r.Resolved)))
            DrawingLines(Pkg.Xml(handout.Resolved), "Handout master", lines, true);
        return lines;
    }

    // Text of shapes and tables, then names and alt text of shapes.
    private void DrawingLines(XDocument doc, string label, List<string> lines, bool skipPrompts)
    {
        void Add(string suffix, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lines.Add(label + suffix + ": " + OoxmlText.Flat(value));
        }
        var tableParagraphs = new HashSet<XElement>();
        foreach (var table in doc.Descendants(A + "tbl"))
        {
            var r = 0;
            foreach (var row in table.Elements(A + "tr"))
            {
                r++;
                var c = 0;
                foreach (var cell in row.Elements(A + "tc"))
                {
                    c++;
                    foreach (var (paragraph, text) in Paragraphs(cell))
                    {
                        tableParagraphs.Add(paragraph);
                        Add(" table row " + r + " column " + c, text);
                    }
                }
            }
        }
        foreach (var (paragraph, text) in Paragraphs(doc))
        {
            if (tableParagraphs.Contains(paragraph)) continue;
            if (skipPrompts && (DefaultPrompt.IsMatch(text.Trim()) || paragraph.Descendants(A + "fld").Any())) continue;
            Add("", text);
        }
        foreach (var c in doc.Descendants(P + "cNvPr"))
        {
            var name = (string?)c.Attribute("name");
            if (name != null && !GenericName.IsMatch(name.Trim())) Add(" shape name", name);
            Add(" shape description", (string?)c.Attribute("descr"));
            Add(" shape title", (string?)c.Attribute("title"));
        }
        foreach (var link in doc.Descendants().Where(e => e.Name.LocalName is "hlinkClick" or "hlinkHover" or "hlinkMouseOver")) Add(" link tooltip", (string?)link.Attribute("tooltip"));
    }

    public List<string> Removals()
    {
        var found = new List<string>();
        void Count(int n, string one, string many) { if (n > 0) found.Add(n == 1 ? one : n + " " + many); }
        var names = Pkg.Names.ToList();
        var pictures = 0; var charts = 0; var diagrams = 0; var objects = 0; var media = 0;
        foreach (var part in names.Where(n => Regex.IsMatch(n, @"^ppt/(slides|slideLayouts|slideMasters|notesSlides|notesMasters|handoutMasters)/[^/]+\.xml$", RegexOptions.IgnoreCase)))
        {
            var doc = Pkg.Xml(part);
            pictures += doc.Descendants(P + "pic").Count();
            media += doc.Descendants().Count(e => e.Name.LocalName is "videoFile" or "audioFile" or "wavAudioFile" or "quickTimeFile");
            foreach (var frame in doc.Descendants(P + "graphicFrame"))
            {
                var uri = (string?)frame.Descendants(A + "graphicData").FirstOrDefault()?.Attribute("uri") ?? "";
                if (uri.EndsWith("/table")) continue;
                if (uri.EndsWith("/chart")) charts++; else if (uri.EndsWith("/diagram")) diagrams++; else objects++;
            }
        }
        Count(pictures - media, "1 picture is replaced by a text placeholder", "pictures are replaced by text placeholders");
        Count(media, "1 audio or video clip is replaced by a text placeholder", "audio or video clips are replaced by text placeholders");
        Count(charts, "1 chart is replaced by a text placeholder", "charts are replaced by text placeholders");
        Count(diagrams, "1 SmartArt diagram is replaced by a text placeholder", "SmartArt diagrams are replaced by text placeholders");
        Count(objects, "1 embedded object is replaced by a text placeholder", "embedded objects are replaced by text placeholders");
        Count(names.Count(n => n.StartsWith("ppt/embeddings/", StringComparison.OrdinalIgnoreCase)), "1 embedded file is removed", "embedded files are removed");
        Count(names.Count(n => Regex.IsMatch(n, @"^ppt/(media)/", RegexOptions.IgnoreCase)), "1 media file is removed", "media files are removed");
        Count(names.Count(n => Regex.IsMatch(n, @"^ppt/fonts/", RegexOptions.IgnoreCase)), "1 embedded font is removed", "embedded fonts are removed");
        var external = Pkg.Names.Where(n => n.EndsWith(".rels")).SelectMany(n => Pkg.Xml(n).Descendants().Where(e => e.Name.LocalName == "Relationship" && string.Equals((string?)e.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))).Count();
        Count(external, "1 link to a web page or file is removed (the visible text stays)", "links to web pages or files are removed (the visible text stays)");
        Count(names.Count(n => Regex.IsMatch(n, @"^ppt/comments/modernComment", RegexOptions.IgnoreCase)), "1 modern comment thread is removed", "modern comment threads are removed");
        Count(names.Count(n => Regex.IsMatch(n, @"^ppt/tags/", RegexOptions.IgnoreCase)), "1 custom tag is removed", "custom tags are removed");
        Count(names.Count(n => n.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".xml") && !n.Contains("/_rels/")), "1 custom data part is removed", "custom data parts are removed");
        Count(Slides.Count(s => s.Hidden), "1 hidden slide is included in the review and stays hidden", "hidden slides are included in the review and stay hidden");
        found.Add("Alt text and titles of shapes are cleared");
        found.Add("Document properties (author, company, titles) and the preview image are cleared");
        found.Add("Section names and custom shows are removed");
        return found;
    }

    public void Dispose() => Pkg.Dispose();
}

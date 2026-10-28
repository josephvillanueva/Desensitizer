using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace Desensitizer;

// PowerPoint presentations. Like the Excel format, the protected copy is a new
// package built part by part from an allow-list. Text is replaced inside
// paragraphs (so a value split across runs is found), in tables, notes, masters,
// layouts, comments, shape names and link tooltips. Pictures, charts, SmartArt,
// embedded objects, audio and video are replaced by a visible text placeholder
// of the same size and position. External links, alt text, tags, custom data,
// embedded fonts, section names and document properties are removed.
public sealed class PptxFormat : IDocumentFormat
{
    public string Extension => ".pptx";
    public string DisplayName => "PowerPoint presentation";
    public bool IsBuiltIn => false;

    public void Validate(string path) { using var model = PptxModel.Open(path); }
    public string ReadText(string path, DocumentPackageContext? context = null)
    {
        using var model = PptxModel.Open(path);
        return string.Join("\n", model.TextLines());
    }
    public TableReviewContext? ReadTableContext(string path, string text, DocumentPackageContext? context = null) => null;
    public ReviewMarkupSummary InspectReviewMarkup(string path, DocumentPackageContext? context = null) => ReviewMarkupSummary.Empty;
    public IReadOnlyList<string> ReadHiddenChannels(string path, DocumentPackageContext? context = null) => Array.Empty<string>();
    public IReadOnlyList<string> DescribeRemovals(string path, DocumentPackageContext? context = null)
    {
        using var model = PptxModel.Open(path);
        return model.Removals();
    }

    public int Transform(string source, string output, IEnumerable<Mapping> mappings, bool forward, DocumentProcessingOptions? options = null, DocumentPackageContext? context = null, bool allowUnappliedMappings = false) =>
        FormatSupport.Run(source, output, () =>
        {
            options ??= new DocumentProcessingOptions();
            options.Validate();
            using var model = PptxModel.Open(source);
            var policyText = forward ? string.Join("\n", model.TextLines()) : "";
            var requested = mappings.ToList();
            var list = FormatSupport.Prepare(policyText, requested, forward, allowUnappliedMappings);
            var built = new PptxBuilder(model, list, forward, options).Build();
            var unknown = 0;
            if (forward) PptxAudit.Run(model, built, list, options, FormatSupport.AuditMappings(policyText, list, requested));
            else
            {
                PptxAudit.Package(built);
                using var restored = PptxModel.FromPackage(OoxmlPackage.OpenBytes(built));
                unknown = DocumentProcessor.UnknownTokens(string.Join("\n", restored.TextLines()), list);
                PptxAudit.Validate(model, built);
            }
            FormatSupport.Release(output, built);
            return unknown;
        });

    public void Audit(string source, string output, IEnumerable<Mapping> mappings)
    {
        var list = mappings.OrderByDescending(m => m.OriginalValue.Length).ToList();
        using var model = PptxModel.Open(source);
        PptxAudit.Run(model, File.ReadAllBytes(output), list, new DocumentProcessingOptions());
    }
}

internal sealed class PptxBuilder
{
    private static readonly XNamespace P = PptxModel.P;
    private static readonly XNamespace A = PptxModel.A;
    private static readonly XNamespace R = OoxmlPackage.R;
    public const string RemovedName = "Removed object";
    private const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    private static readonly HashSet<string> PresentationKeep = new() { "sldMasterIdLst", "notesMasterIdLst", "handoutMasterIdLst", "sldIdLst", "sldSz", "notesSz", "defaultTextStyle" };

    private readonly PptxModel model;
    private readonly List<Mapping> list;
    private readonly bool forward;
    private readonly DocumentProcessingOptions options;
    private readonly OoxmlPackage pkg;
    private readonly List<(string Name, byte[] Data, string? ContentType)> parts = new();
    private readonly HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);

    public PptxBuilder(PptxModel model, List<Mapping> list, bool forward, DocumentProcessingOptions options)
    {
        this.model = model; this.list = list; this.forward = forward; this.options = options; pkg = model.Pkg;
    }

    private string Text(string text) => FormatSupport.Apply(text, list, forward);
    private void Add(string name, XDocument doc, string? type)
    {
        if (!written.Add(name)) return;
        parts.Add((name, OoxmlPackage.Serialize(doc), type));
    }
    private void AddRaw(string name, byte[] data, string? type)
    {
        if (!written.Add(name)) return;
        parts.Add((name, data, type));
    }

    private void AddRels(string part, IEnumerable<OoxmlPackage.Rel> rels)
    {
        var kept = rels.Select(r => (r.Id, r.Type, "/" + r.Resolved)).ToList();
        if (kept.Count == 0) return;
        Add(OoxmlPackage.RelsNameFor(part), OoxmlPackage.Relationships(kept), "application/vnd.openxmlformats-package.relationships+xml");
    }

    private IEnumerable<OoxmlPackage.Rel> Keep(string part, params string[] types) =>
        pkg.Rels(part).Where(r => !r.External && pkg.Has(r.Resolved) && types.Any(t => r.IsType(t)));

    public byte[] Build()
    {
        var core = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(XName.Get("coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties"),
                new XAttribute(XNamespace.Xmlns + "cp", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties"),
                new XAttribute(XNamespace.Xmlns + "dc", "http://purl.org/dc/elements/1.1/"),
                new XAttribute(XNamespace.Xmlns + "dcterms", "http://purl.org/dc/terms/")));
        Add("docProps/core.xml", core, "application/vnd.openxmlformats-package.core-properties+xml");
        Add("_rels/.rels", OoxmlPackage.Relationships(new[]
        {
            ("rId1", RelBase + "officeDocument", model.PresentationPart),
            ("rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml"),
        }), "application/vnd.openxmlformats-package.relationships+xml");

        // Presentation relationships that stay.
        var presRels = Keep(model.PresentationPart, "slideMaster", "slide", "notesMaster", "handoutMaster", "theme", "presProps", "viewProps", "tableStyles", "commentAuthors").ToList();
        AddRels(model.PresentationPart, presRels);
        Add(model.PresentationPart, Presentation(), PptxModel.PresentationContentType);
        foreach (var rel in presRels)
        {
            if (rel.IsType("slide")) Slide(rel.Resolved);
            else if (rel.IsType("slideMaster")) Master(rel.Resolved);
            else if (rel.IsType("notesMaster") || rel.IsType("handoutMaster")) Drawing(rel.Resolved, true, "theme");
            else if (rel.IsType("theme")) Theme(rel.Resolved);
            else if (rel.IsType("commentAuthors")) CommentAuthors(rel.Resolved);
            else AddRaw(rel.Resolved, pkg.Bytes(rel.Resolved), pkg.ContentType(rel.Resolved));
        }
        return OoxmlPackage.Build(parts, OoxmlPackage.StandardDefaults);
    }

    private XDocument Presentation()
    {
        var doc = new XDocument(pkg.Xml(model.PresentationPart));
        foreach (var child in doc.Root!.Elements().Where(e => !PresentationKeep.Contains(e.Name.LocalName)).ToList()) child.Remove();
        return doc;
    }

    private void Master(string part)
    {
        Drawing(part, true, "theme", "slideLayout");
        foreach (var layout in Keep(part, "slideLayout")) Drawing(layout.Resolved, true, "slideMaster", "theme");
    }

    private void Slide(string part)
    {
        var keep = Keep(part, "slideLayout", "notesSlide", "comments", "slide").ToList();
        Drawing(part, false, "slideLayout", "notesSlide", "comments", "slide");
        foreach (var rel in keep)
        {
            if (rel.IsType("notesSlide")) Drawing(rel.Resolved, false, "notesMaster", "slide");
            else if (rel.IsType("comments")) Comments(rel.Resolved);
        }
    }

    // Slides, layouts, masters and notes share one cleaning step.
    private void Drawing(string part, bool master, params string[] keepTypes)
    {
        if (written.Contains(part)) return;
        var doc = new XDocument(pkg.Xml(part));
        Clean(doc, part);
        Add(part, doc, pkg.ContentType(part));
        AddRels(part, Keep(part, keepTypes));
        foreach (var rel in Keep(part, keepTypes))
            if (rel.IsType("theme")) Theme(rel.Resolved);
    }

    private void Theme(string part)
    {
        if (written.Contains(part)) return;
        var doc = new XDocument(pkg.Xml(part));
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "extLst").ToList()) if (e.Parent != null) e.Remove();
        foreach (var blip in doc.Descendants(A + "blipFill").ToList())
            blip.ReplaceWith(new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr"))));
        foreach (var a in doc.Descendants().Attributes("name").ToList()) a.Value = Text(a.Value);
        Add(part, doc, pkg.ContentType(part));
    }

    private void CommentAuthors(string part)
    {
        var doc = new XDocument(pkg.Xml(part));
        var n = 0;
        foreach (var author in doc.Descendants(P + "cmAuthor"))
        {
            n++;
            author.SetAttributeValue("name", "Author " + n);
            author.SetAttributeValue("initials", "A");
        }
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "extLst").ToList()) if (e.Parent != null) e.Remove();
        Add(part, doc, pkg.ContentType(part));
    }

    private void Comments(string part)
    {
        var doc = new XDocument(pkg.Xml(part));
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "extLst").ToList()) if (e.Parent != null) e.Remove();
        foreach (var text in doc.Descendants(P + "text")) text.Value = Text(text.Value);
        Add(part, doc, pkg.ContentType(part));
    }

    private void Clean(XDocument doc, string part)
    {
        var root = doc.Root!;
        foreach (var e in root.Descendants().Where(e => e.Name.LocalName is "extLst" or "custDataLst" or "custData").ToList()) if (e.Parent != null) e.Remove();

        // Pictures, charts, diagrams, objects and media become one text shape each.
        foreach (var pic in root.Descendants(P + "pic").ToList()) if (pic.Parent != null) ReplaceWithPlaceholder(pic, pic.Element(P + "spPr")?.Element(A + "xfrm"));
        foreach (var frame in root.Descendants(P + "graphicFrame").ToList())
        {
            if (frame.Parent == null) continue;
            var uri = (string?)frame.Descendants(A + "graphicData").FirstOrDefault()?.Attribute("uri") ?? "";
            if (uri.EndsWith("/table")) continue;
            ReplaceWithPlaceholder(frame, frame.Element(P + "xfrm"));
        }
        foreach (var content in root.Descendants(P + "contentPart").ToList()) if (content.Parent != null) content.Remove();

        // Picture fills, picture bullets and picture backgrounds.
        foreach (var blip in root.Descendants().Where(e => e.Name.LocalName == "blipFill").ToList())
        {
            if (blip.Parent == null) continue;
            var parent = blip.Parent.Name.LocalName;
            if (parent == "bgPr") blip.ReplaceWith(new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", "FFFFFF"))));
            else blip.ReplaceWith(new XElement(A + "noFill"));
        }
        foreach (var bullet in root.Descendants(A + "buBlip").ToList()) if (bullet.Parent != null) bullet.ReplaceWith(new XElement(A + "buNone"));

        // Links: web, file and program links go; links to other slides and next/previous jumps stay.
        var relMap = pkg.Rels(part).ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
        foreach (var link in root.Descendants().Where(e => e.Name.LocalName is "hlinkClick" or "hlinkHover" or "hlinkMouseOver").ToList())
        {
            if (link.Parent == null) continue;
            var id = (string?)link.Attribute(R + "id");
            var action = (string?)link.Attribute("action") ?? "";
            var keepLink = id != null
                ? relMap.TryGetValue(id, out var rel) && !rel.External && rel.IsType("slide")
                : action.StartsWith("ppaction://hlinkshowjump", StringComparison.OrdinalIgnoreCase);
            if (!keepLink) { link.Remove(); continue; }
            if (link.Attribute("tooltip") is { } tip) tip.Value = Text(tip.Value);
        }

        // Names and alt text of shapes.
        foreach (var c in root.Descendants(P + "cNvPr"))
        {
            c.Attribute("descr")?.Remove();
            c.Attribute("title")?.Remove();
            if (c.Attribute("name") is { } name && name.Value != RemovedName) name.Value = Text(name.Value);
        }
        foreach (var cs in root.Descendants(P + "cSld"))
            if (cs.Attribute("name") is { } name) name.Value = Text(name.Value);

        // Text.
        foreach (var paragraph in root.Descendants(A + "p").ToList())
        {
            var nodes = paragraph.Descendants().Where(x => x.Name == A + "t" || x.Name == A + "br").Select(x => x.Name == A + "t" ? x : new XElement(A + "t", " ")).ToList();
            OoxmlText.ReplaceInRuns(nodes, list, forward);
        }
    }

    private void ReplaceWithPlaceholder(XElement original, XElement? transform)
    {
        if (!options.PlaceholdersEnabled) { original.Remove(); return; }
        var id = original.Descendants(P + "cNvPr").FirstOrDefault()?.Attribute("id")?.Value ?? "0";
        var label = options.ImagePlaceholder!;
        var ext = transform?.Element(A + "ext");
        if (options.IncludeImageSize && label.EndsWith("]") && ext != null
            && long.TryParse((string?)ext.Attribute("cx"), out var cx) && long.TryParse((string?)ext.Attribute("cy"), out var cy) && cx > 0 && cy > 0)
            label = label.Substring(0, label.Length - 1) + ": " + (cx / 914400.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " x " + (cy / 914400.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " in]";
        var spPr = new XElement(P + "spPr");
        if (transform != null) spPr.Add(new XElement(A + "xfrm", transform.Attributes().Where(a => !a.IsNamespaceDeclaration), transform.Elements().Select(e => new XElement(e))));
        spPr.Add(new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")));
        var shape = new XElement(P + "sp",
            new XElement(P + "nvSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", RemovedName)),
                new XElement(P + "cNvSpPr", new XAttribute("txBox", "1")),
                new XElement(P + "nvPr")),
            spPr,
            new XElement(P + "txBody",
                new XElement(A + "bodyPr", new XAttribute("wrap", "square")),
                new XElement(A + "lstStyle"),
                new XElement(A + "p", new XElement(A + "r", new XElement(A + "rPr", new XAttribute("lang", "en-US")), new XElement(A + "t", label)))));
        original.ReplaceWith(shape);
    }
}

internal static class PptxAudit
{
    private static readonly XNamespace P = PptxModel.P;
    private static readonly XNamespace A = PptxModel.A;

    private static readonly Regex AllowedParts = new(
        @"^(\[Content_Types\]\.xml|_rels/\.rels|docProps/core\.xml|ppt/presentation\.xml|ppt/_rels/presentation\.xml\.rels|ppt/presProps\.xml|ppt/viewProps\.xml|ppt/tableStyles\.xml|ppt/commentAuthors\.xml|" +
        @"ppt/slides/slide\d+\.xml|ppt/slides/_rels/slide\d+\.xml\.rels|ppt/slideLayouts/slideLayout\d+\.xml|ppt/slideLayouts/_rels/slideLayout\d+\.xml\.rels|" +
        @"ppt/slideMasters/slideMaster\d+\.xml|ppt/slideMasters/_rels/slideMaster\d+\.xml\.rels|ppt/notesSlides/notesSlide\d+\.xml|ppt/notesSlides/_rels/notesSlide\d+\.xml\.rels|" +
        @"ppt/notesMasters/notesMaster\d+\.xml|ppt/notesMasters/_rels/notesMaster\d+\.xml\.rels|ppt/handoutMasters/handoutMaster\d+\.xml|ppt/handoutMasters/_rels/handoutMaster\d+\.xml\.rels|" +
        @"ppt/theme/theme\d+\.xml|ppt/comments/comment\d+\.xml)$", RegexOptions.IgnoreCase);

    // Attributes that can carry document text.
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal) { "name", "descr", "title", "tooltip", "text", "initials", "label", "desc", "userId", "providerId", "action" };

    public static bool IsAudited(XAttribute attribute) => TextAttributes.Contains(attribute.Name.LocalName);

    public static void Package(byte[] built)
    {
        using var package = OoxmlPackage.OpenBytes(built);
        foreach (var name in package.Names)
            if (!AllowedParts.IsMatch(name)) throw new InvalidDataException("The output contains a part that is not on the allowed list (" + name + "). Export was blocked because its content cannot be checked.");
        foreach (var name in package.Names) package.Xml(name);
        OoxmlPackage.AssertSelfContained(package);
        foreach (var name in package.Names.Where(n => n.EndsWith(".xml")))
            if (package.Xml(name).Descendants().Any(e => e.Name.LocalName is "oleObj" or "blip" or "videoFile" or "audioFile" or "buBlip" || e.Name == P + "pic"))
                throw new InvalidDataException("The output still contains an image or embedded object. Export was blocked.");
    }

    public static void Validate(PptxModel source, byte[] built)
    {
        if (string.IsNullOrEmpty(source.SourcePath)) return;
        var validator = new OpenXmlValidator(FileFormatVersions.Office2021);
        var originalErrors = new Dictionary<string, int>();
        try
        {
            using var original = PresentationDocument.Open(source.SourcePath, false);
            originalErrors = validator.Validate(original).GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        }
        catch (OpenXmlPackageException) { }
        using var protectedDeck = PresentationDocument.Open(new MemoryStream(built, false), false);
        foreach (var error in validator.Validate(protectedDeck))
        {
            var key = Key(error);
            if (originalErrors.TryGetValue(key, out var remaining) && remaining > 0) { originalErrors[key] = remaining - 1; continue; }
            throw new InvalidDataException("The protected copy has a new PowerPoint structure error (code " + error.Id + " in " + error.Part?.Uri + " at " + error.Path?.XPath + "). Export was blocked. No file was released.");
        }
    }
    private static string Key(ValidationErrorInfo e) => string.Join("|", e.Id, e.Description, e.Part?.Uri, e.Path?.XPath);

    public static void Run(PptxModel source, byte[] built, List<Mapping> list, DocumentProcessingOptions options, List<Mapping>? auditMappings = null)
    {
        Package(built);
        using var package = OoxmlPackage.OpenBytes(built);
        var scanner = FormatSupport.Scanner(list);
        foreach (var name in package.Names)
        {
            var hit = scanner.Find(package.Xml(name), IsAudited);
            if (hit != null) throw FormatSupport.Residue(hit, name, "");
        }
        FormatSupport.AuditHiddenPackage(package, auditMappings ?? list);
        Structure(source, package, list, options);
        Validate(source, built);
    }

    private static List<string> Texts(XContainer root, bool skipPlaceholders)
    {
        var result = new List<string>();
        foreach (var (paragraph, text) in PptxModel.Paragraphs(root))
        {
            if (skipPlaceholders && paragraph.Ancestors(P + "sp").Any(sp => (string?)sp.Element(P + "nvSpPr")?.Element(P + "cNvPr")?.Attribute("name") == PptxBuilder.RemovedName)) continue;
            result.Add(text);
        }
        return result;
    }

    private static void Compare(string what, XContainer before, XContainer after, List<Mapping> list, bool placeholders)
    {
        var b = Texts(after, true);
        // Paragraphs inside removed pictures and frames do not exist in the output.
        var removedParagraphs = before.Descendants().Where(e => e.Name == P + "graphicFrame" && !((string?)e.Descendants(A + "graphicData").FirstOrDefault()?.Attribute("uri") ?? "").EndsWith("/table")).SelectMany(e => e.Descendants(A + "p")).ToHashSet();
        var a = PptxModel.Paragraphs(before).Where(p => !removedParagraphs.Contains(p.Paragraph) && !p.Paragraph.Ancestors(P + "pic").Any()).Select(p => p.Text).ToList();
        if (a.Count != b.Count) throw new InvalidDataException("Export blocked: the paragraph structure changed in " + what + ".");
        for (var i = 0; i < a.Count; i++)
            if (FormatSupport.CanonicalSpace(FormatSupport.Protect(a[i], list)) != FormatSupport.CanonicalSpace(b[i]))
                throw new InvalidDataException("Export blocked: text outside the selected replacements changed in " + what + ", paragraph " + (i + 1) + ". No file was released.");
        var shapeNames = new[] { "sp", "cxnSp", "grpSp" };
        int Shapes(XContainer c) => c.Descendants().Count(e => e.Name.Namespace == P && (shapeNames.Contains(e.Name.LocalName) || e.Name.LocalName is "graphicFrame" or "pic"));
        if (placeholders && Shapes(before) != Shapes(after)) throw new InvalidDataException("Export blocked: the number of shapes changed in " + what + ".");
        if (before.Descendants(A + "tr").Count() != after.Descendants(A + "tr").Count() || before.Descendants(A + "tc").Count() != after.Descendants(A + "tc").Count())
            throw new InvalidDataException("Export blocked: table rows or cells changed in " + what + ".");
    }

    private static void Structure(PptxModel source, OoxmlPackage outPackage, List<Mapping> list, DocumentProcessingOptions options)
    {
        using var output = PptxModel.FromPackage(outPackage);
        if (output.Slides.Count != source.Slides.Count) throw new InvalidDataException("Export blocked: the number of slides changed.");
        for (var i = 0; i < source.Slides.Count; i++)
        {
            var a = source.Slides[i];
            var b = output.Slides[i];
            if (a.Hidden != b.Hidden) throw new InvalidDataException("Export blocked: a hidden slide changed.");
            Compare("slide " + (i + 1), source.Pkg.Xml(a.Part), output.Pkg.Xml(b.Part), list, options.PlaceholdersEnabled);
            var notesBefore = source.RelsOf(a.Part, "notesSlide").ToList();
            var notesAfter = output.RelsOf(b.Part, "notesSlide").ToList();
            if (notesBefore.Count != notesAfter.Count) throw new InvalidDataException("Export blocked: speaker notes changed on slide " + (i + 1) + ".");
            for (var k = 0; k < notesBefore.Count; k++) Compare("the notes of slide " + (i + 1), source.Pkg.Xml(notesBefore[k].Resolved), output.Pkg.Xml(notesAfter[k].Resolved), list, options.PlaceholdersEnabled);
        }
        var masterBefore = source.Pkg.Rels(source.PresentationPart).Where(r => r.IsType("slideMaster")).ToList();
        var masterAfter = output.Pkg.Rels(output.PresentationPart).Where(r => r.IsType("slideMaster")).ToList();
        if (masterBefore.Count != masterAfter.Count) throw new InvalidDataException("Export blocked: the number of slide masters changed.");
        for (var m = 0; m < masterBefore.Count; m++)
        {
            Compare("a slide master", source.Pkg.Xml(masterBefore[m].Resolved), output.Pkg.Xml(masterAfter[m].Resolved), list, options.PlaceholdersEnabled);
            var layoutsBefore = source.RelsOf(masterBefore[m].Resolved, "slideLayout").ToList();
            var layoutsAfter = output.RelsOf(masterAfter[m].Resolved, "slideLayout").ToList();
            if (layoutsBefore.Count != layoutsAfter.Count) throw new InvalidDataException("Export blocked: the number of slide layouts changed.");
            for (var l = 0; l < layoutsBefore.Count; l++) Compare("a slide layout", source.Pkg.Xml(layoutsBefore[l].Resolved), output.Pkg.Xml(layoutsAfter[l].Resolved), list, options.PlaceholdersEnabled);
        }
    }
}

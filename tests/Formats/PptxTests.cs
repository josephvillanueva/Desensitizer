using System.IO.Compression;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Desensitizer;

// .pptx. The deck is built from raw XML in test code. Fictional values only.
static class PptxTests
{
    private const string PNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
    private const string Ns = $"xmlns:a=\"{ANs}\" xmlns:r=\"{RNs}\" xmlns:p=\"{PNs}\"";

    private static readonly Mapping[] Maps =
    {
        Kit.Map("Zorvex Holdings", "ORGANIZATION", 1),
        Kit.Map("Mira Quillon", "PERSON", 1),
        Kit.Map("mira.quillon@zorvex.example", "EMAIL", 1),
    };

    private static string Rels(params (string Id, string Type, string Target, bool External)[] rels) =>
        Decl + $"<Relationships xmlns=\"{PkgRel}\">" + string.Concat(rels.Select(r => $"<Relationship Id=\"{r.Id}\" Type=\"{RNs}/{r.Type}\" Target=\"{r.Target}\"" + (r.External ? " TargetMode=\"External\"" : "") + "/>")) + "</Relationships>";

    private static string Shape(int id, string name, string text, string extra = "", string descr = "") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"" + (descr.Length > 0 ? $" descr=\"{descr}\"" : "") + $"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p>{text}</a:p></p:txBody></p:sp>";

    public static List<(string Name, string Content)> Parts()
    {
        var ct = (string part, string type) => $"<Override PartName=\"/{part}\" ContentType=\"{type}\"/>";
        var types = Decl + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
            ct("ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml") +
            ct("ppt/slideMasters/slideMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml") +
            ct("ppt/slideLayouts/slideLayout1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml") +
            ct("ppt/slides/slide1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml") +
            ct("ppt/slides/slide2.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml") +
            ct("ppt/notesSlides/notesSlide1.xml", "application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml") +
            ct("ppt/notesMasters/notesMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.notesMaster+xml") +
            ct("ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml") +
            ct("ppt/theme/theme2.xml", "application/vnd.openxmlformats-officedocument.theme+xml") +
            ct("ppt/comments/comment1.xml", "application/vnd.openxmlformats-officedocument.presentationml.comments+xml") +
            ct("ppt/commentAuthors.xml", "application/vnd.openxmlformats-officedocument.presentationml.commentAuthors+xml") +
            ct("ppt/charts/chart1.xml", "application/vnd.openxmlformats-officedocument.drawingml.chart+xml") +
            ct("docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml") + ct("docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml") + "</Types>";
        var presentation = Decl + $"<p:presentation {Ns}><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:notesMasterIdLst><p:notesMasterId r:id=\"rId4\"/></p:notesMasterIdLst>" +
            "<p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/><p:sldId id=\"257\" r:id=\"rId3\"/></p:sldIdLst><p:sldSz cx=\"9144000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>" +
            "<p:extLst><p:ext uri=\"{521415D9-36F7-43E2-AB2F-B90AF26B5E84}\"><p14:sectionLst xmlns:p14=\"http://schemas.microsoft.com/office/powerpoint/2010/main\"><p14:section name=\"Zorvex Section\" id=\"{11111111-1111-1111-1111-111111111111}\"/></p14:sectionLst></p:ext></p:extLst></p:presentation>";
        var master = Decl + $"<p:sldMaster {Ns}><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" +
            Shape(2, "Footer Placeholder 1", "<a:r><a:t>Zorvex Holdings Confidential</a:t></a:r>") + Shape(3, "Title Placeholder 2", "<a:r><a:t>Click to edit Master title style</a:t></a:r>") +
            "</p:spTree></p:cSld><p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst></p:sldMaster>";
        var layout = Decl + $"<p:sldLayout {Ns} type=\"title\"><p:cSld name=\"Zorvex Holdings Title Layout\"><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld></p:sldLayout>";
        var slide1 = Decl + $"<p:sld {Ns}><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" +
            Shape(2, "Title 1", "<a:r><a:t>Zorvex </a:t></a:r><a:r><a:rPr b=\"1\"/><a:t>Holdings quarterly plan</a:t></a:r>") +
            Shape(3, "Zorvex Holdings logo", "<a:r><a:t>Rule: orders over 500 units need approval</a:t></a:r><a:r><a:rPr><a:hlinkClick r:id=\"rId7\" tooltip=\"Open Zorvex site\"/></a:rPr><a:t> see site</a:t></a:r>", descr: "Photo of Mira Quillon") +
            "<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Picture 3\" descr=\"Headshot of Mira Quillon\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId5\"/></p:blipFill><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1828800\" cy=\"914400\"/></a:xfrm></p:spPr></p:pic>" +
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"5\" name=\"Chart 4\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rId6\"/></a:graphicData></a:graphic></p:graphicFrame>" +
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"6\" name=\"Table 5\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblGrid><a:gridCol w=\"100\"/><a:gridCol w=\"100\"/></a:tblGrid>" +
            "<a:tr h=\"100\"><a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:t>Owner</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc><a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:t>Region</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc></a:tr>" +
            "<a:tr h=\"100\"><a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:t>Mira </a:t></a:r><a:r><a:t>Quillon</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc><a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:t>North</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame>" +
            "</p:spTree></p:cSld></p:sld>";
        var slide2 = Decl + $"<p:sld {Ns} show=\"0\"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" +
            Shape(2, "Title 1", "<a:r><a:t>Backup contact mira.quillon@zorvex.example</a:t></a:r><a:br/><a:r><a:t>Second line</a:t></a:r>") + "</p:spTree></p:cSld></p:sld>";
        var notes = Decl + $"<p:notes {Ns}><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" + Shape(2, "Notes Placeholder 1", "<a:r><a:t>Call Mira Quillon before the meeting</a:t></a:r>") + "</p:spTree></p:cSld></p:notes>";
        var notesMaster = Decl + $"<p:notesMaster {Ns}><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld></p:notesMaster>";
        var theme = Decl + $"<a:theme xmlns:a=\"{ANs}\" name=\"Zorvex Holdings Theme\"><a:themeElements/></a:theme>";
        var comments = Decl + $"<p:cmLst {Ns}><p:cm authorId=\"0\" dt=\"2024-01-01T00:00:00.000\" idx=\"1\"><p:pos x=\"10\" y=\"10\"/><p:text>Check Zorvex Holdings figures</p:text></p:cm></p:cmLst>";
        var authors = Decl + $"<p:cmAuthorLst {Ns}><p:cmAuthor id=\"0\" name=\"Mira Quillon\" initials=\"MQ\" lastIdx=\"1\" clrIdx=\"0\"/></p:cmAuthorLst>";
        var chart = Decl + "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"" + ANs + "\"><c:chart><c:title><c:tx><c:rich><a:p><a:r><a:t>Zorvex Holdings Revenue</a:t></a:r></a:p></c:rich></c:tx></c:title></c:chart></c:chartSpace>";
        return new List<(string, string)>
        {
            ("[Content_Types].xml", types),
            ("_rels/.rels", Rels(("rId1", "officeDocument", "ppt/presentation.xml", false)).Replace("</Relationships>", "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/><Relationship Id=\"rId3\" Type=\"" + RNs + "/extended-properties\" Target=\"docProps/app.xml\"/><Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail\" Target=\"docProps/thumbnail.jpeg\"/></Relationships>")),
            ("docProps/core.xml", Decl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Zorvex Holdings plan</dc:title><dc:creator>Mira Quillon</dc:creator></cp:coreProperties>"),
            ("docProps/app.xml", Decl + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Company>Zorvex Holdings</Company></Properties>"),
            ("docProps/thumbnail.jpeg", "JPEGDATA Zorvex Holdings"),
            ("ppt/presentation.xml", presentation),
            ("ppt/_rels/presentation.xml.rels", Rels(("rId1", "slideMaster", "slideMasters/slideMaster1.xml", false), ("rId2", "slide", "slides/slide1.xml", false), ("rId3", "slide", "slides/slide2.xml", false), ("rId4", "notesMaster", "notesMasters/notesMaster1.xml", false), ("rId5", "theme", "theme/theme1.xml", false), ("rId6", "commentAuthors", "commentAuthors.xml", false))),
            ("ppt/slideMasters/slideMaster1.xml", master),
            ("ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml", false), ("rId2", "theme", "../theme/theme1.xml", false))),
            ("ppt/slideLayouts/slideLayout1.xml", layout),
            ("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Rels(("rId1", "slideMaster", "../slideMasters/slideMaster1.xml", false))),
            ("ppt/slides/slide1.xml", slide1),
            ("ppt/slides/_rels/slide1.xml.rels", Rels(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml", false), ("rId2", "notesSlide", "../notesSlides/notesSlide1.xml", false), ("rId3", "comments", "../comments/comment1.xml", false), ("rId5", "image", "../media/image1.png", false), ("rId6", "chart", "../charts/chart1.xml", false), ("rId7", "hyperlink", "https://zorvex.example/site", true))),
            ("ppt/slides/slide2.xml", slide2),
            ("ppt/slides/_rels/slide2.xml.rels", Rels(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml", false))),
            ("ppt/notesSlides/notesSlide1.xml", notes),
            ("ppt/notesSlides/_rels/notesSlide1.xml.rels", Rels(("rId1", "notesMaster", "../notesMasters/notesMaster1.xml", false), ("rId2", "slide", "../slides/slide1.xml", false))),
            ("ppt/notesMasters/notesMaster1.xml", notesMaster),
            ("ppt/notesMasters/_rels/notesMaster1.xml.rels", Rels(("rId1", "theme", "../theme/theme2.xml", false))),
            ("ppt/theme/theme1.xml", theme), ("ppt/theme/theme2.xml", theme),
            ("ppt/comments/comment1.xml", comments), ("ppt/commentAuthors.xml", authors),
            ("ppt/charts/chart1.xml", chart), ("ppt/media/image1.png", "PNGDATA Zorvex Holdings"),
            ("customXml/item1.xml", "<x>Mira Quillon custom</x>"),
        };
    }

    private static string Write(string dir, string name, IEnumerable<(string Name, string Content)> parts)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Kit.Zip(parts.ToArray()));
        return path;
    }

    public static void Run(CheckFn check, string work)
    {
        var dir = Kit.Dir(work, "pptx");
        var source = Write(dir, "deck.pptx", Parts());
        var format = DocumentFormats.Require(source);
        check("Deck source opens with the SDK", CanOpen(source));

        // ---------------------------------------------------------------- reading
        var text = DocumentProcessor.ReadText(source);
        var lines = text.Split('\n');
        check("Read text is slide by slide and joins split runs", lines.Contains("Slide 1: Zorvex Holdings quarterly plan") && lines.Contains("Slide 1: Rule: orders over 500 units need approval see site"));
        check("Read text includes table cells with positions", lines.Contains("Slide 1 table row 2 column 1: Mira Quillon") && lines.Contains("Slide 1 table row 1 column 2: Region"));
        check("Read text includes speaker notes and comments", lines.Contains("Slide 1 notes: Call Mira Quillon before the meeting") && lines.Contains("Slide 1 comment: Check Zorvex Holdings figures"));
        check("Read text includes hidden slides with a label", lines.Contains("[hidden] Slide 2: Backup contact mira.quillon@zorvex.example Second line"));
        check("Read text includes alt text, shape names and link tooltips", lines.Contains("Slide 1 shape description: Photo of Mira Quillon") && lines.Contains("Slide 1 shape description: Headshot of Mira Quillon") && lines.Contains("Slide 1 shape name: Zorvex Holdings logo") && lines.Contains("Slide 1 link tooltip: Open Zorvex site"));
        check("Read text includes chart titles, master and layout text, and document properties", lines.Contains("Slide 1 chart text: Zorvex Holdings Revenue") && lines.Contains("Slide master 1: Zorvex Holdings Confidential") && lines.Contains("Document property creator: Mira Quillon") && lines.Contains("Document property Company: Zorvex Holdings"));
        check("Read text skips default master prompts and generic shape names", !text.Contains("Click to edit") && !text.Contains("shape name: Title 1") && !text.Contains("shape name: Picture 3"));
        check("Table context and review markup are not provided for pptx", format.ReadTableContext(source, text) == null && !format.InspectReviewMarkup(source).HasReviewMarkup);
        var removals = format.DescribeRemovals(source);
        check("Removal report names pictures, charts, links, comments and hidden slides", new[] { "1 picture", "1 chart", "link", "hidden slide", "Alt text" }.All(k => removals.Any(r => r.Contains(k))) && removals.All(r => !r.Contains('—')));

        // ---------------------------------------------------------------- forward
        var output = Path.Combine(dir, "deck-protected.pptx");
        DocumentProcessor.Desensitize(source, output, Maps);
        var parts = Kit.Read(output);
        var everything = string.Join("\n", parts.Values);
        check("Protected deck holds no selected value in any part", new[] { "Zorvex", "Quillon", "mira.quillon" }.All(v => !everything.Contains(v, StringComparison.OrdinalIgnoreCase)), string.Join(";", parts.Where(p => p.Value.Contains("Zorvex") || p.Value.Contains("Quillon")).Select(p => p.Key)));
        check("Protected deck keeps only allowed parts", parts.Keys.All(k => !Regex.IsMatch(k, @"media|charts|customXml|docProps/app|thumbnail")), string.Join(",", parts.Keys));
        check("Protected deck has no external relationship and no picture or blip", parts.Where(p => p.Key.EndsWith(".rels")).All(p => !p.Value.Contains("TargetMode")) && !everything.Contains("<p:pic") && !everything.Contains("a:blip") && !everything.Contains("r:embed"));
        var s1 = parts["ppt/slides/slide1.xml"];
        check("Pictures and charts become text placeholders of the same size", Regex.Matches(s1, @"\[IMAGE REMOVED: 2\.00 x 1\.00 in\]").Count == 1 && Regex.Matches(s1, @"\[IMAGE REMOVED: 1\.00 x 1\.00 in\]").Count == 1);
        check("Alt text is cleared, other shape names are replaced", !s1.Contains("descr=") && s1.Contains("name=\"[ORGANIZATION_001] logo\""));
        check("External link is removed and the visible text stays", !s1.Contains("hlinkClick") && s1.Contains(" see site"));
        check("Split runs are replaced", s1.Contains("[ORGANIZATION_001] </a:t>") == false && Regex.IsMatch(s1, @"<a:t>\[ORGANIZATION_001\]") && s1.Contains("quarterly plan"));
        check("Table cell split across runs is replaced and the table is intact", s1.Contains("[PERSON_001]") && Regex.Matches(s1, "<a:tr ").Count == 2 && Regex.Matches(s1, "<a:tc>").Count == 4);
        check("Business text is kept", s1.Contains("Rule: orders over 500 units need approval"));
        check("Notes and comments are replaced and the comment author is neutral", parts["ppt/notesSlides/notesSlide1.xml"].Contains("Call [PERSON_001] before") && parts["ppt/comments/comment1.xml"].Contains("Check [ORGANIZATION_001] figures") && parts["ppt/commentAuthors.xml"].Contains("name=\"Author 1\"") && parts["ppt/commentAuthors.xml"].Contains("initials=\"A\""));
        check("Hidden slide stays hidden and its text is replaced", parts["ppt/slides/slide2.xml"].Contains("show=\"0\"") && parts["ppt/slides/slide2.xml"].Contains("[EMAIL_001]") && parts["ppt/slides/slide2.xml"].Contains("<a:br"));
        check("Master, layout and theme names are replaced", parts["ppt/slideMasters/slideMaster1.xml"].Contains("[ORGANIZATION_001] Confidential") && parts["ppt/slideLayouts/slideLayout1.xml"].Contains("[ORGANIZATION_001] Title Layout") && parts["ppt/theme/theme1.xml"].Contains("[ORGANIZATION_001] Theme"));
        check("Section names are removed from the presentation", !parts["ppt/presentation.xml"].Contains("sectionLst") && parts["ppt/presentation.xml"].Contains("sldIdLst"));
        check("Document properties are cleared", !parts["docProps/core.xml"].Contains("creator") && !parts["docProps/core.xml"].Contains("title"));
        using (var zip = ZipFile.OpenRead(output))
            check("Entries carry the normalized timestamp, content types first", zip.Entries.All(e => e.LastWriteTime.Year == 1980) && zip.Entries.First().FullName == "[Content_Types].xml");
        check("Protected deck opens with the SDK and keeps the slide count", CanOpen(output) && SlideCount(output) == 2);
        check("Protected deck adds no new validator errors", NewErrors(source, output) == 0);
        check("Shape counts per slide are unchanged", Regex.Matches(s1, "<p:(sp|pic|graphicFrame)>").Count == Regex.Matches(Kit.Read(source)["ppt/slides/slide1.xml"], "<p:(sp|pic|graphicFrame)>").Count);
        check("Leaves no temporary file", Kit.Leftovers(dir).Length == 0);
        format.Audit(source, output, Maps);
        check("Audit passes on a good copy", true);

        // ---------------------------------------------------------------- rehydrate
        var restored = Path.Combine(dir, "deck-restored.pptx");
        var unknown = DocumentProcessor.Rehydrate(output, restored, Maps);
        var back = DocumentProcessor.ReadText(restored).Split('\n');
        check("Rehydrate restores slide, table, notes and comment text", unknown == 0 && back.Contains("Slide 1: Zorvex Holdings quarterly plan") && back.Contains("Slide 1 table row 2 column 1: Mira Quillon") && back.Contains("Slide 1 notes: Call Mira Quillon before the meeting") && back.Contains("Slide 1 comment: Check Zorvex Holdings figures") && back.Contains("[hidden] Slide 2: Backup contact mira.quillon@zorvex.example Second line"));
        check("Rehydrated deck opens with the SDK and adds no validator errors", CanOpen(restored) && NewErrors(source, restored) == 0);

        // ---------------------------------------------------------------- options
        var silent = Path.Combine(dir, "deck-silent.pptx");
        DocumentProcessor.Desensitize(source, silent, Maps, new DocumentProcessingOptions { ImagePlaceholder = null });
        var silentSlide = Kit.Read(silent)["ppt/slides/slide1.xml"];
        check("Without placeholders pictures and charts are removed silently", !silentSlide.Contains("REMOVED") && !silentSlide.Contains("<p:pic") && Regex.Matches(silentSlide, "<p:(sp|pic|graphicFrame)>").Count == 5 - 2);
        var plain = Path.Combine(dir, "deck-plain.pptx");
        DocumentProcessor.Desensitize(source, plain, new[] { Kit.Map("Nobody Here", "PERSON", 9) });
        check("Export with a mapping that does not occur still strips unsafe parts", Kit.Read(plain).Keys.All(k => !k.Contains("media") && !k.Contains("customXml")) && Kit.Read(plain)["ppt/commentAuthors.xml"].Contains("Author 1"));

        // ---------------------------------------------------------------- negative
        var macros = Parts();
        macros.Add(("ppt/vbaProject.bin", "VBA"));
        var macroPath = Write(dir, "macro.pptx", macros);
        check("Deck with macros is refused", Kit.Throws(() => DocumentProcessor.ReadText(macroPath), "macros") && Kit.Throws(() => DocumentProcessor.Desensitize(macroPath, Path.Combine(dir, "macro-out.pptx"), Maps), "macros") && !File.Exists(Path.Combine(dir, "macro-out.pptx")));
        var pptm = Path.Combine(dir, "deck.pptm");
        File.Copy(source, pptm, true);
        check(".pptm is refused by extension with advice", Kit.Throws(() => DocumentProcessor.ReadText(pptm), "macros"));
        var notZip = Path.Combine(dir, "fake.pptx");
        File.WriteAllText(notZip, "not a zip Zorvex Holdings");
        var notZipMessage = Kit.Message(() => DocumentProcessor.ReadText(notZip));
        check("A file that is not a package gets a friendly message without its content", notZipMessage.Contains("not a valid PowerPoint") && !notZipMessage.Contains("Zorvex"));
        var noSlide = Parts();
        noSlide.RemoveAll(p => p.Name == "ppt/slides/slide2.xml");
        check("Deck with a missing slide part is refused", Kit.Throws(() => DocumentProcessor.ReadText(Write(dir, "missing.pptx", noSlide))));
        var show = Parts();
        var presentationPart = show.First(p => p.Name == "[Content_Types].xml");
        show.Remove(presentationPart);
        show.Add(("[Content_Types].xml", presentationPart.Content.Replace("presentation.main+xml", "slideshow.main+xml")));
        check("Slide show files are refused", Kit.Throws(() => DocumentProcessor.ReadText(Write(dir, "show.pptx", show)), "plain PowerPoint"));

        var tampered = Path.Combine(dir, "t1.pptx");
        Tamper(output, tampered, "ppt/slides/slide1.xml", s => s.Replace("[ORGANIZATION_001] logo", "Zorvex Holdings logo"));
        check("Audit rejects a copy that still holds a selected value in a shape name", Kit.Throws(() => format.Audit(source, tampered, Maps), "Export blocked"));
        var tampered2 = Path.Combine(dir, "t2.pptx");
        Tamper(output, tampered2, "ppt/slides/slide1.xml", s => s.Replace("approval", "approvals"));
        check("Preservation audit rejects changed business text", Kit.Throws(() => format.Audit(source, tampered2, Maps), "outside the selected"));
        var tampered3 = Path.Combine(dir, "t3.pptx");
        Tamper(output, tampered3, "ppt/slides/_rels/slide1.xml.rels", s => s.Replace("</Relationships>", "<Relationship Id=\"rIdX\" Type=\"http://x/hyperlink\" Target=\"https://example.test\" TargetMode=\"External\"/></Relationships>"));
        check("Audit rejects an external relationship", Kit.Throws(() => format.Audit(source, tampered3, Maps), "external"));
        var tampered4 = Path.Combine(dir, "t4.pptx");
        File.WriteAllBytes(tampered4, Kit.Zip(Kit.Read(output).Select(p => (p.Key, p.Value)).Append(("ppt/media/image9.png", "x")).ToArray()));
        check("Audit rejects a part that is not on the allow list", Kit.Throws(() => format.Audit(source, tampered4, Maps), "allowed list"));
        var tampered5 = Path.Combine(dir, "t5.pptx");
        Tamper(output, tampered5, "ppt/slides/slide1.xml", s => s.Replace("</p:spTree>", "<p:pic><p:nvPicPr><p:cNvPr id=\"99\" name=\"x\"/></p:nvPicPr></p:pic></p:spTree>"));
        check("Audit rejects a picture in the output", Kit.Throws(() => format.Audit(source, tampered5, Maps), "image"));
        var tampered6 = Path.Combine(dir, "t6.pptx");
        Tamper(output, tampered6, "ppt/slides/slide1.xml", s => s.Replace("<p:grpSpPr />", "<p:grpSpPr />" + "<p:sp><p:nvSpPr><p:cNvPr id=\"77\" name=\"extra\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/></p:sp>"));
        check("Audit rejects an added shape", Kit.Throws(() => format.Audit(source, tampered6, Maps), "shapes"));
        check("Export never overwrites the source", Kit.Throws(() => DocumentProcessor.Desensitize(source, source, Maps)));

        // A value that only lives in an alt text attribute is cleared, never shipped.
        var altOnly = Path.Combine(dir, "alt.pptx");
        DocumentProcessor.Desensitize(source, altOnly, new[] { Kit.Map("Headshot of Mira Quillon", "PERSON", 5) });
        check("A value found only in alt text is not shipped", !Kit.All(altOnly).Contains("Headshot"));
    }

    private static bool CanOpen(string path)
    {
        try { using var doc = PresentationDocument.Open(path, false); return doc.PresentationPart != null; }
        catch { return false; }
    }

    private static int SlideCount(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        return doc.PresentationPart!.SlideParts.Count();
    }

    private static int NewErrors(string source, string output)
    {
        var validator = new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2021);
        using var a = PresentationDocument.Open(source, false);
        using var b = PresentationDocument.Open(output, false);
        string Key(ValidationErrorInfo e) => e.Id + "|" + e.Description + "|" + e.Path?.XPath;
        var original = validator.Validate(a).GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        var fresh = 0;
        foreach (var e in validator.Validate(b))
        {
            var key = Key(e);
            if (original.TryGetValue(key, out var n) && n > 0) { original[key] = n - 1; continue; }
            fresh++;
        }
        return fresh;
    }

    private static void Tamper(string input, string output, string part, Func<string, string> change)
    {
        var parts = Kit.Read(input).Select(p => (p.Key, p.Key == part ? change(p.Value) : p.Value)).ToArray();
        File.WriteAllBytes(output, Kit.Zip(parts));
    }
}

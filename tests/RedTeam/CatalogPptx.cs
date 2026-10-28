using System.Text;
using static RedTeam.Sentinel;

namespace RedTeam;

public static partial class Catalog
{
    static Attack Px(string name, string desc, Action<Pp> mod, string[]? values = null)
    {
        var a = Add("pptx/" + name, desc, ".pptx", () => { var p = new Pp(); mod(p); return p.ToBytes(); }, values);
        a.BuildVisible = () =>
        {
            var p = new Pp(); mod(p);
            p.Shapes += Pp.Text("Reference", "Reference " + string.Join(" | ", a.Values), 90);
            return p.ToBytes();
        };
        return a;
    }

    const string NotesMasterCt = "application/vnd.openxmlformats-officedocument.presentationml.notesMaster+xml";
    const string NotesSlideCt = "application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml";
    const string PRoot = "xmlns:a=\"" + Pp.ANs + "\" xmlns:r=\"" + Pp.RNs + "\" xmlns:p=\"" + Pp.PNs + "\"";

    static void AddNotes(Pp p, string shapes)
    {
        const string tree = "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";
        p.SetPart("ppt/notesMasters/notesMaster1.xml", OoxmlBuilder.Decl + "<p:notesMaster " + PRoot + "><p:cSld>" + tree + "</p:spTree></p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/></p:notesMaster>", NotesMasterCt);
        p.SetPart("ppt/notesSlides/notesSlide1.xml", OoxmlBuilder.Decl + "<p:notes " + PRoot + "><p:cSld>" + tree + shapes + "</p:spTree></p:cSld></p:notes>", NotesSlideCt);
        p.Rel("ppt/presentation.xml", "rIdNM", "notesMaster", "notesMasters/notesMaster1.xml");
        p.AfterMasterIdLst = "<p:notesMasterIdLst><p:notesMasterId r:id=\"rIdNM\"/></p:notesMasterIdLst>";
        p.Rel("ppt/notesMasters/notesMaster1.xml", "rId1", "theme", "../theme/theme1.xml");
        p.Rel("ppt/notesSlides/notesSlide1.xml", "rId1", "notesMaster", "../notesMasters/notesMaster1.xml");
        p.Rel("ppt/notesSlides/notesSlide1.xml", "rId2", "slide", "../slides/slide1.xml");
        p.Rel("ppt/slides/slide1.xml", "rIdNS", "notesSlide", "../notesSlides/notesSlide1.xml");
    }

    static void Pptx()
    {
        Px("control/text-runs", "value in slide text, split across runs", p => p.Shapes += Pp.Runs("Body", 5, "Zel", "da Quim", "by-Orsino"), new[] { Person });
        Px("control/slide-text-all", "all values in slide text", p => p.Shapes += Pp.Text("Body", Person + " of " + Company + " " + Id, 5));
        Px("control/table-cell", "value in a table cell", p => p.Shapes += "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"20\" name=\"Table\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr/><a:tblGrid><a:gridCol w=\"914400\"/></a:tblGrid><a:tr h=\"370840\"><a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>" + Person + "</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
        Px("notes/slide-notes", "speaker notes hold the values", p => AddNotes(p, Pp.Text("Notes", Person + " " + Company, 7)));
        Px("notes/notes-shape-name-variant", "notes shape name, separator variant", p => AddNotes(p, Pp.Text(Under(Person), "n", 7)));
        Px("comments/legacy-comment-and-author", "legacy comment text and author", p =>
        {
            p.SetPart("ppt/commentAuthors.xml", OoxmlBuilder.Decl + "<p:cmAuthorLst " + PRoot + "><p:cmAuthor id=\"0\" name=\"" + Person + "\" initials=\"ZQO\" lastIdx=\"1\" clrIdx=\"0\"/></p:cmAuthorLst>", "application/vnd.openxmlformats-officedocument.presentationml.commentAuthors+xml");
            p.SetPart("ppt/comments/comment1.xml", OoxmlBuilder.Decl + "<p:cmLst " + PRoot + "><p:cm authorId=\"0\" dt=\"2020-01-01T00:00:00.000\" idx=\"1\"><p:pos x=\"0\" y=\"0\"/><p:text>" + Company + " " + Id + "</p:text></p:cm></p:cmLst>", "application/vnd.openxmlformats-officedocument.presentationml.comments+xml");
            p.Rel("ppt/presentation.xml", "rIdCA", "commentAuthors", "commentAuthors.xml");
            p.Rel("ppt/slides/slide1.xml", "rIdCM", "comments", "../comments/comment1.xml");
        });
        Px("comments/modern-threaded", "modern comment part (documented as blocked)", p =>
        {
            p.SetPart("ppt/comments/modernComment_100_1.xml", OoxmlBuilder.Decl + "<p188:cmLst xmlns:p188=\"http://schemas.microsoft.com/office/powerpoint/2018/8/main\"><p188:cm id=\"{1}\"><p188:txBody>" + Person + "</p188:txBody></p188:cm></p188:cmLst>", "application/vnd.ms-powerpoint.comments+xml");
            p.Rel("ppt/slides/slide1.xml", "rIdMC", "http://schemas.microsoft.com/office/2018/10/relationships/comments", "../comments/modernComment_100_1.xml");
        });
        Px("names/shape-name-exact", "shape name is the exact value", p => p.Shapes += Pp.Text(Person, "x", 5));
        Px("names/shape-name-variant", "shape name, separator variant", p => p.Shapes += Pp.Text("Box_" + Under(Company), "x", 5));
        Px("names/alt-text-title", "alt text and title attributes", p => p.Shapes = Pp.Text("Box", "x", 1).Replace("name=\"Box\"", "name=\"Box\" descr=\"" + Person + "\" title=\"" + Company + "\""));
        Px("names/alt-text-variant", "alt text, separator variant", p => p.Shapes = Pp.Text("Box", "x", 1).Replace("name=\"Box\"", "name=\"Box\" descr=\"" + Under(Person) + "\" title=\"" + Under(Company) + "\""));
        Px("names/layout-name", "layout cSld name holds the value", p => { p.LayoutShapes = ""; p.SlideCSldAttrs = " name=\"" + Person + "\""; });
        Px("names/layout-name-variant", "slide cSld name, separator variant", p => p.SlideCSldAttrs = " name=\"" + Under(Person) + "\"");
        Px("names/theme-name", "theme name attribute holds the value", p => p.Theme = Pp.DefaultTheme(Person));
        Px("names/theme-name-variant", "theme name attribute, separator variant", p => p.Theme = Pp.DefaultTheme(Under(Company)));
        Px("names/theme-font-typeface", "theme font typeface holds the exact value", p => p.Theme = Pp.DefaultTheme("Office").Replace("typeface=\"Calibri Light\"", "typeface=\"" + Company + "\""));
        Px("names/theme-clrscheme-name", "colour scheme name holds the exact value", p => p.Theme = Pp.DefaultTheme("Office").Replace("<a:clrScheme name=\"Office\">", "<a:clrScheme name=\"" + Person + "\">"));
        Px("names/theme-fmtscheme-name", "format scheme name holds the exact value", p => p.Theme = Pp.DefaultTheme("Office").Replace("<a:fmtScheme name=\"Office\">", "<a:fmtScheme name=\"" + Company + "\">"));
        Px("attr/run-typeface", "run properties latin typeface holds the exact value", p => p.Shapes = Pp.Text("Box", "x", 1).Replace("<a:rPr lang=\"en-US\"/>", "<a:rPr lang=\"en-US\"><a:latin typeface=\"" + Company + "\"/></a:rPr>"));
        Px("attr/run-lang-attribute", "run properties lang attribute holds the exact value", p => p.Shapes = Pp.Text("Box", "x", 1).Replace("lang=\"en-US\"", "lang=\"" + Person + "\""));
        Px("attr/timing-strval", "animation timing string value holds the exact value", p => p.SlideExtra = "<p:timing><p:tnLst><p:par><p:cTn id=\"1\" dur=\"indefinite\" restart=\"never\" nodeType=\"tmRoot\"><p:childTnLst><p:set><p:cBhvr><p:cTn id=\"2\" dur=\"1\" fill=\"hold\"/><p:tgtEl><p:spTgt spid=\"2\"/></p:tgtEl><p:attrNameLst><p:attrName>style.visibility</p:attrName></p:attrNameLst></p:cBhvr><p:to><p:strVal val=\"" + Person + "\"/></p:to></p:set></p:childTnLst></p:cTn></p:par></p:tnLst></p:timing>");
        Px("attr/transition-sound", "transition sound name holds the exact value", p => p.SlideExtra = "<p:transition spd=\"med\"><p:fade/><p:sndAc><p:stSnd><p:snd r:embed=\"rIdSnd\" name=\"" + Person + "\"/></p:stSnd></p:sndAc></p:transition>");
        Px("attr/custshow-and-sections", "presentation extLst sections and custom shows (removed)", p => p.PresentationExtra = "<p:extLst><p:ext uri=\"{521415D9-36F7-43E2-AB2F-B90AF26B5E84}\"><p14:sectionLst xmlns:p14=\"http://schemas.microsoft.com/office/powerpoint/2010/main\"><p14:section name=\"" + Person + "\" id=\"{AAAAAAAA-0000-0000-0000-000000000001}\"><p14:sldIdLst><p14:sldId id=\"256\"/></p14:sldIdLst></p14:section></p14:sectionLst></p:ext></p:extLst>");
        Px("attr/tag-parts", "tags part and custDataLst hold the value", p =>
        {
            p.SetPart("ppt/tags/tag1.xml", OoxmlBuilder.Decl + "<p:tagLst " + PRoot + "><p:tag name=\"" + Person + "\" val=\"" + Company + "\"/></p:tagLst>", "application/vnd.openxmlformats-officedocument.presentationml.tags+xml");
            p.Rel("ppt/presentation.xml", "rIdTag", "tags", "tags/tag1.xml");
            p.SlideExtra = "<p:custDataLst xmlns:p=\"" + Pp.PNs + "\"><p:tags r:id=\"rIdTag\"/></p:custDataLst>";
        });
        Px("attr/table-styles-raw", "tableStyles part is copied byte for byte (styleName holds the exact value)", p =>
        {
            p.SetPart("ppt/tableStyles.xml", OoxmlBuilder.Decl + "<a:tblStyleLst xmlns:a=\"" + Pp.ANs + "\" def=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\"><a:tblStyle styleId=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\" styleName=\"" + Person + "\"/></a:tblStyleLst>", "application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml");
            p.Rel("ppt/presentation.xml", "rIdTS", "tableStyles", "tableStyles.xml");
        });
        Px("attr/presprops-raw", "presProps part is copied byte for byte (a showPr attribute holds the exact value)", p =>
        {
            p.SetPart("ppt/presProps.xml", OoxmlBuilder.Decl + "<p:presentationPr " + PRoot + "><p:showPr loop=\"0\" showNarration=\"1\"><p:custShow id=\"0\"/></p:showPr><p:clrMru><a:srgbClr val=\"FF0000\"/></p:clrMru></p:presentationPr>".Replace("<p:custShow id=\"0\"/>", "<p:present/>"), "application/vnd.openxmlformats-officedocument.presentationml.presProps+xml");
            p.Rel("ppt/presentation.xml", "rIdPP", "presProps", "presProps.xml");
        });
        Px("attr/presprops-comment", "XML comment inside the raw presProps part", p =>
        {
            p.SetPart("ppt/presProps.xml", OoxmlBuilder.Decl + "<p:presentationPr " + PRoot + "><!-- " + Person + " --></p:presentationPr>", "application/vnd.openxmlformats-officedocument.presentationml.presProps+xml");
            p.Rel("ppt/presentation.xml", "rIdPP", "presProps", "presProps.xml");
        });
        Px("attr/viewprops-raw", "viewProps part holds the value in an unaudited attribute", p =>
        {
            p.SetPart("ppt/viewProps.xml", OoxmlBuilder.Decl + "<p:viewPr " + PRoot + "><p:normalViewPr><p:restoredLeft sz=\"15620\" autoAdjust=\"0\"/><p:restoredTop sz=\"94660\" lbl=\"" + Under(Person) + "\"/></p:normalViewPr></p:viewPr>", "application/vnd.openxmlformats-officedocument.presentationml.viewProps+xml");
            p.Rel("ppt/presentation.xml", "rIdVP", "viewProps", "viewProps.xml");
        });
        Px("links/hyperlink-external", "external hyperlink on a run", p =>
        {
            p.Rel("ppt/slides/slide1.xml", "rIdL", "hyperlink", "https://example.test/" + Under(Person), true);
            p.Shapes = Pp.Text("Box", "click", 1).Replace("<a:rPr lang=\"en-US\"/>", "<a:rPr lang=\"en-US\"><a:hlinkClick r:id=\"rIdL\" tooltip=\"" + Person + "\"/></a:rPr>");
        });
        Px("links/hyperlink-tooltip-internal", "tooltip on an internal jump link", p => p.Shapes = Pp.Text("Box", "next", 1).Replace("<a:rPr lang=\"en-US\"/>", "<a:rPr lang=\"en-US\"><a:hlinkClick r:id=\"\" action=\"ppaction://hlinkshowjump?jump=nextslide\" tooltip=\"" + Person + "\"/></a:rPr>"));
        Px("links/hyperlink-tooltip-variant", "tooltip on an internal jump link, separator variant", p => p.Shapes = Pp.Text("Box", "next", 1).Replace("<a:rPr lang=\"en-US\"/>", "<a:rPr lang=\"en-US\"><a:hlinkClick r:id=\"\" action=\"ppaction://hlinkshowjump?jump=nextslide\" tooltip=\"" + Under(Person) + "\"/></a:rPr>"));
        Px("links/action-attribute", "hlinkClick action carries the value", p => p.Shapes = Pp.Text("Box", "next", 1).Replace("<a:rPr lang=\"en-US\"/>", "<a:rPr lang=\"en-US\"><a:hlinkClick r:id=\"\" action=\"ppaction://hlinkshowjump?jump=" + Under(Person) + "\"/></a:rPr>"));
        Px("masters/master-and-layout-text", "master and layout text hold the value", p => { p.MasterShapes = Pp.Text("MasterBox", Person, 5); p.LayoutShapes = Pp.Text("LayoutBox", Company, 6); });
        Px("masters/master-shape-name-variant", "master shape name, separator variant", p => p.MasterShapes = Pp.Text(Under(Person), "x", 5));
        Px("masters/footer-field", "footer field text", p => p.Shapes += "<p:sp><p:nvSpPr><p:cNvPr id=\"30\" name=\"Footer\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:fld id=\"{AAAAAAAA-0000-0000-0000-000000000002}\" type=\"datetime1\"><a:rPr lang=\"en-US\"/><a:t>" + Person + "</a:t></a:fld></a:p></p:txBody></p:sp>");
        Px("xml/comment-in-slide", "XML comment in slide part", p => p.Shapes += "<!-- " + Person + " -->");
        Px("xml/comment-in-theme", "XML comment in theme part", p => p.Theme = Pp.DefaultTheme("Office").Replace("<a:themeElements>", "<!-- " + Company + " --><a:themeElements>"));
        Px("xml/pi-in-slide", "processing instruction in slide part", p => p.Shapes += "<?note " + Person + "?>");
        Px("xml/split-by-comment", "text node split by a comment inside a:t", p => p.Shapes += "<p:sp><p:nvSpPr><p:cNvPr id=\"40\" name=\"S\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>Zel<!-- x -->da Quimby-Orsino</a:t></a:r></a:p></p:txBody></p:sp>", new[] { Person });
        Px("xml/alt-content-text", "AlternateContent runs holding the value", p => p.Shapes += "<p:sp><p:nvSpPr><p:cNvPr id=\"41\" name=\"S\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>" + Person + "</a:t></a:r></a:p></p:txBody></p:sp>");
        Px("xml/across-paragraphs", "value split across two paragraphs", p => p.Shapes = "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"B\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>Zelda</a:t></a:r></a:p><a:p><a:r><a:t>Quimby-Orsino</a:t></a:r></a:p></p:txBody></p:sp>", new[] { Person });
        Px("xml/line-break-inside", "a:br inside the value", p => p.Shapes = "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"B\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>Zelda</a:t></a:r><a:br/><a:r><a:t>Quimby-Orsino</a:t></a:r></a:p></p:txBody></p:sp>", new[] { Person });
        Px("unicode/zero-width", "zero width space inside the value", p => p.Shapes += Pp.Text("U", "Zel​da Quimby-Orsino", 5), new[] { Person });
        Px("unicode/nbsp-and-dash", "no-break space and en dash", p => p.Shapes += Pp.Text("U", "Zelda Quimby–Orsino", 5), new[] { Person });
        Px("unicode/cyrillic", "Cyrillic homoglyph", p => p.Shapes += Pp.Text("U", "Zеlda Quimby-Orsino", 5), new[] { Person });
        Px("media/picture-alt", "picture with alt text and name", p =>
        {
            p.Defaults["png"] = "image/png"; p.SetBytes("ppt/media/image1.png", Png);
            p.Rel("ppt/slides/slide1.xml", "rIdImg", "image", "../media/image1.png");
            p.Shapes += "<p:pic><p:nvPicPr><p:cNvPr id=\"50\" name=\"" + Person + "\" descr=\"" + Company + "\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rIdImg\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></a:xfrm></p:spPr></p:pic>";
        });
        Px("media/chart-and-embedded-workbook", "chart part with embedded workbook holding the value", p =>
        {
            p.SetPart("ppt/charts/chart1.xml", OoxmlBuilder.Decl + "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"" + Pp.ANs + "\"><c:chart><c:title><c:tx><c:rich><a:p><a:r><a:t>" + Person + "</a:t></a:r></a:p></c:rich></c:tx></c:title></c:chart></c:chartSpace>", "application/vnd.openxmlformats-officedocument.drawingml.chart+xml");
            p.SetBytes("ppt/embeddings/Microsoft_Excel_Worksheet1.xlsx", new Xl().ToBytes());
            p.Defaults["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            p.Rel("ppt/slides/slide1.xml", "rIdCh", "chart", "../charts/chart1.xml");
            p.Shapes += "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"51\" name=\"Chart\" descr=\"" + Company + "\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rIdCh\"/></a:graphicData></a:graphic></p:graphicFrame>";
        });
        Px("media/embedded-ole-object", "OLE object frame and embedded package", p =>
        {
            p.SetBytes("ppt/embeddings/oleObject1.bin", Encoding.Unicode.GetBytes(Person));
            p.Defaults["bin"] = "application/vnd.openxmlformats-officedocument.oleObject";
            p.Rel("ppt/slides/slide1.xml", "rIdOle", "oleObject", "../embeddings/oleObject1.bin");
            p.Shapes += "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"52\" name=\"Obj\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/presentationml/2006/ole\"><p:oleObj r:id=\"rIdOle\" progId=\"Word.Document.12\"/></a:graphicData></a:graphic></p:graphicFrame>";
        });
        Px("media/custom-xml-and-fonts", "customXml and embedded font data", p =>
        {
            p.SetPart("customXml/item1.xml", OoxmlBuilder.Decl + "<root>" + Person + "</root>", "application/xml");
            p.Defaults["fntdata"] = "application/x-fontdata"; p.SetBytes("ppt/fonts/font1.fntdata", Encoding.UTF8.GetBytes(Company));
            p.Rel("ppt/presentation.xml", "rIdCx", "customXml", "../customXml/item1.xml");
        });
        Px("media/app-props", "docProps/app.xml and custom.xml", p =>
        {
            p.SetPart("docProps/app.xml", OoxmlBuilder.Decl + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Company>" + Company + "</Company><Manager>" + Person + "</Manager></Properties>", "application/vnd.openxmlformats-officedocument.extended-properties+xml");
            p.Rel("", "rIdA", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties", "docProps/app.xml");
        });
        Px("media/core-props", "core properties", p => p.SetPart("docProps/core.xml", OoxmlBuilder.Decl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:creator>" + Person + "</dc:creator><dc:title>" + Company + "</dc:title></cp:coreProperties>"));
        Px("media/macro-vba", "vbaProject.bin (documented as blocked)", p => p.SetBytes("ppt/vbaProject.bin", Encoding.UTF8.GetBytes(Person)));
        Px("media/hidden-slide", "hidden slide with the value", p => p.SlideCSldAttrs = "", null);
        Px("media/unreferenced-slide-part", "slide part that nothing points to", p => p.SetPart("ppt/slides/slide9.xml", OoxmlBuilder.Decl + "<p:sld " + PRoot + "><p:cSld><p:spTree/></p:cSld></p:sld>".Replace("<p:spTree/>", "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" + Pp.Text("X", Person, 3) + "</p:spTree>"), "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"));
        Add("pptx/zip/comment", "pptx zip comment and extra fields", ".pptx", () => { var p = new Pp { ZipComment = Person }; p.Shapes += Pp.Text("A", Company, 5); var e = p.Entries(); e[1].ExtraCentral = new byte[] { 0x99, 0x99, 4, 0 }.Concat(Encoding.UTF8.GetBytes(Id.Substring(0, 4))).ToArray(); return RawZip.Write(e, Person); });
        Add("pptx/zip/not-a-zip", "plain text named .pptx", ".pptx", () => Encoding.UTF8.GetBytes(Person));
    }
}


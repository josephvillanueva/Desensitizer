using System.Text;
using static RedTeam.Sentinel;

namespace RedTeam;

public static partial class Catalog
{
    static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    static string InlineDrawing(string name, string descr, string title = "", string extra = "") =>
        "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"914400\" cy=\"914400\"/><wp:docPr id=\"1\" name=\"" + Docx.Esc(name) + "\" descr=\"" + Docx.Esc(descr) + "\" title=\"" + Docx.Esc(title) + "\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"0\" name=\"" + Docx.Esc(name) + "\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"rIdImg\"/></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></a:xfrm></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>" + extra;

    static void WithImage(Docx d, byte[]? bytes = null, string file = "image1.png")
    {
        d.Defaults["png"] = "image/png";
        d.SetBytes("word/media/" + file, bytes ?? Png);
        d.Rel("rIdImg", "image", "media/" + file);
    }

    static void DocxParts()
    {
        Dx("image/alt-text-exact", "picture alt text and title with exact values", d => { WithImage(d); d.Body("<w:p>" + InlineDrawing("Picture 1", Person, Company) + "</w:p>"); });
        Dx("image/alt-text-variant", "picture alt text with separator variants", d => { WithImage(d); d.Body("<w:p>" + InlineDrawing("Picture 1", Under(Person) + " badge", Under(Company)) + "</w:p>"); });
        Dx("image/name-attribute", "picture shape name holds the value", d => { WithImage(d); d.Body("<w:p>" + InlineDrawing(Person, "") + "</w:p>"); });
        Dx("image/file-name", "media file name holds the value", d => { WithImage(d, null, Under(Person) + ".png"); d.Body("<w:p>" + InlineDrawing("p", "") + "</w:p>"); });
        Dx("image/exif-xmp-bytes", "image bytes carry the value in XMP", d => { WithImage(d, Png.Concat(Encoding.UTF8.GetBytes("<x:xmpmeta><dc:creator>" + Person + "</dc:creator></x:xmpmeta>")).ToArray()); d.Body("<w:p>" + InlineDrawing("p", "") + "</w:p>"); });
        Dx("image/orphan-media", "media part with no relationship", d => { d.Defaults["png"] = "image/png"; d.SetBytes("word/media/orphan.png", Png.Concat(Encoding.UTF8.GetBytes(Person)).ToArray()); });
        Dx("image/emf-text", "EMF image with the value as UTF-16 text", d => { d.Defaults["emf"] = "image/x-emf"; d.SetBytes("word/media/image1.emf", Encoding.Unicode.GetBytes(Person)); d.Rel("rIdImg", "image", "media/image1.emf"); d.Body("<w:p>" + InlineDrawing("p", "") + "</w:p>"); });
        Dx("image/vml-shape-alt", "VML picture with alt, title, href and o:title attributes", d => { WithImage(d); d.Body("<w:p><w:r><w:pict><v:shape id=\"s1\" type=\"#t\" alt=\"" + Person + "\" title=\"" + Company + "\" href=\"https://example.test/" + Id + "\" o:title=\"" + Person + "\" style=\"width:10pt;height:10pt\"><v:imagedata r:id=\"rIdImg\" o:title=\"" + Person + "\"/></v:shape></w:pict></w:r></w:p>"); });
        Dx("image/vml-textbox", "VML shape with a textbox holding the value", d => d.Body("<w:p><w:r><w:pict><v:shape id=\"s1\" style=\"width:100pt;height:20pt\"><v:textbox><w:txbxContent><w:p><w:r><w:t>" + Person + "</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>"));
        Dx("image/drawingml-textbox", "DrawingML wps textbox holding the value, with AlternateContent fallback", d => d.Body("<w:p><w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing><wp:inline><wp:extent cx=\"914400\" cy=\"914400\"/><wp:docPr id=\"2\" name=\"TextBox\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent><w:p><w:r><w:t>" + Person + "</w:t></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></mc:Choice><mc:Fallback><w:pict><v:shape><v:textbox><w:txbxContent><w:p><w:r><w:t>" + Company + "</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback></mc:AlternateContent></w:r></w:p>"));
        Dx("image/textbox-with-visible-sibling", "textbox beside visible text in the same paragraph", d => d.Body("<w:p><w:r><w:t>Hello " + Person + "</w:t></w:r><w:r><w:pict><v:shape><v:textbox><w:txbxContent><w:p><w:r><w:t>" + Company + "</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>"));
        Dx("image/chart-part", "chart part with title, cached values and an embedded workbook", d =>
        {
            d.Defaults["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            d.Set("word/charts/chart1.xml", Docx.Decl + "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><c:chart><c:title><c:tx><c:rich><a:p><a:r><a:t>" + Person + "</a:t></a:r></a:p></c:rich></c:tx></c:title><c:plotArea><c:barChart><c:ser><c:tx><c:strRef><c:f>Sheet1!$B$1</c:f><c:strCache><c:pt idx=\"0\"><c:v>" + Company + "</c:v></c:pt></c:strCache></c:strRef></c:tx></c:ser></c:barChart></c:plotArea></c:chart></c:chartSpace>", "application/vnd.openxmlformats-officedocument.drawingml.chart+xml");
            d.SetBytes("word/embeddings/Microsoft_Excel_Worksheet.xlsx", Encoding.UTF8.GetBytes(Id));
            d.Rel("rIdChart", "chart", "charts/chart1.xml");
            d.Body("<w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"914400\" cy=\"914400\"/><wp:docPr id=\"3\" name=\"Chart\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rIdChart\"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
        });
        Dx("image/smartart-parts", "SmartArt data and drawing parts holding the value", d =>
        {
            d.Set("word/diagrams/data1.xml", Docx.Decl + "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><dgm:ptLst><dgm:pt modelId=\"{00000000-0000-0000-0000-000000000001}\"><dgm:t><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>" + Person + "</a:t></a:r></a:p></dgm:t></dgm:pt></dgm:ptLst></dgm:dataModel>", "application/vnd.openxmlformats-officedocument.drawingml.diagramData+xml");
            d.Set("word/diagrams/drawing1.xml", Docx.Decl + "<dsp:drawing xmlns:dsp=\"http://schemas.microsoft.com/office/drawing/2008/diagram\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:t>" + Company + "</a:t></dsp:drawing>", "application/vnd.ms-office.drawingml.diagramDrawing+xml");
            d.Rel("rIdD1", "diagramData", "diagrams/data1.xml"); d.Rel("rIdD2", "http://schemas.microsoft.com/office/2007/relationships/diagramDrawing", "diagrams/drawing1.xml");
        });
        Dx("ole/embedded-object", "OLE object bin and an embedded package holding the value", d =>
        {
            d.Defaults["bin"] = "application/vnd.openxmlformats-officedocument.oleObject";
            d.Defaults["png"] = "image/png"; d.SetBytes("word/media/ole.png", Png); d.Rel("rIdImgX", "image", "media/ole.png");
            d.SetBytes("word/embeddings/oleObject1.bin", Encoding.Unicode.GetBytes(Person));
            d.SetBytes("word/embeddings/Microsoft_Word_Document1.docx", new Docx().Body(Docx.Para(Company)).ToBytes());
            d.Rel("rIdO1", "oleObject", "embeddings/oleObject1.bin");
            d.Body("<w:p><w:r><w:object><v:shape><v:imagedata r:id=\"rIdImgX\"/></v:shape><o:OLEObject Type=\"Embed\" ProgID=\"Word.Document.12\" ShapeID=\"s1\" DrawAspect=\"Content\" ObjectID=\"_1\" r:id=\"rIdO1\"/></w:object></w:r></w:p>");
        });
        Dx("ole/embedded-package-no-object", "embedded package with no object element", d =>
        {
            d.SetBytes("word/embeddings/Package1.docx", new Docx().Body(Docx.Para(Person)).ToBytes());
            d.Defaults["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            d.Rel("rIdP", "package", "embeddings/Package1.docx");
        });
        Dx("parts/glossary-document", "glossary document with a docPart holding the value", d =>
        {
            d.Set("word/glossary/document.xml", Docx.Decl + "<w:glossaryDocument " + Docx.Ns + "><w:docParts><w:docPart><w:docPartPr><w:name w:val=\"" + Person + "\"/><w:description w:val=\"" + Company + "\"/></w:docPartPr><w:docPartBody><w:p><w:r><w:t>" + Id + "</w:t></w:r></w:p></w:docPartBody></w:docPart></w:docParts></w:glossaryDocument>", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.glossary+xml");
            d.Rel("rIdG", "glossaryDocument", "glossary/document.xml");
        });
        Dx("parts/customxml-item", "customXml item and itemProps holding the value", d =>
        {
            d.Set("customXml/item1.xml", Docx.Decl + "<root><name>" + Person + "</name><org>" + Company + "</org></root>", "application/xml");
            d.Set("customXml/itemProps1.xml", Docx.Decl + "<ds:datastoreItem xmlns:ds=\"http://schemas.openxmlformats.org/officeDocument/2006/customXml\" ds:itemID=\"{AAAAAAAA-0000-0000-0000-000000000001}\"/>", "application/vnd.openxmlformats-officedocument.customXmlProperties+xml");
            d.Rel("rIdC", "customXml", "../customXml/item1.xml");
        });
        Dx("parts/printer-settings", "printerSettings binary holding the value", d =>
        {
            d.SetBytes("word/printerSettings/printerSettings1.bin", Encoding.Unicode.GetBytes(Person), "application/vnd.openxmlformats-officedocument.wordprocessingml.printerSettings");
            d.Rel("rIdPr", "printerSettings", "printerSettings/printerSettings1.bin");
        });
        Dx("parts/macro-vba", "vbaProject.bin (documented as blocked)", d => { d.SetBytes("word/vbaProject.bin", Encoding.UTF8.GetBytes(Person)); d.Rel("rIdV", "http://schemas.microsoft.com/office/2006/relationships/vbaProject", "vbaProject.bin"); });
        Dx("parts/activex", "ActiveX control part (documented as blocked)", d => d.Set("word/activeX/activeX1.xml", Docx.Decl + "<ax:ocx xmlns:ax=\"http://schemas.microsoft.com/office/2006/activeX\" ax:classid=\"{1}\" ax:persistence=\"persistPropertyBag\"><ax:ocxPr ax:name=\"Caption\" ax:value=\"" + Person + "\"/></ax:ocx>", "application/vnd.ms-office.activeX+xml"));
        Dx("parts/altchunk", "altChunk importing another part holding the value", d =>
        {
            d.Set("word/afchunk.htm", "<html><body>" + Person + "</body></html>");
            d.Defaults["htm"] = "text/html";
            d.Rel("rIdAF", "aFChunk", "afchunk.htm");
            d.Body("<w:altChunk r:id=\"rIdAF\"/>" + Docx.Para("x"));
        });
        Dx("parts/endnote-comment-style-names", "unusual word/*.xml part names holding the value", d =>
        {
            d.Set("word/" + Under(Person) + ".xml", Docx.Decl + "<w:x " + Docx.Ns + ">" + Person + "</w:x>", "application/xml");
        });
        Dx("parts/main-part-elsewhere", "main document stored outside word/ with the value", d =>
        {
            d.RootRelsOverride = Docx.RelsXml(new[] { ("rId1", Docx.RelNs + "officeDocument", "content/document.xml", false) });
            d.Set("content/document.xml", DocWith(Docx.Para("See " + Person)), Docx.MainCt);
        });
        Dx("parts/second-main-part", "a second document part reachable by a relationship", d =>
        {
            d.Set("word/document2.xml", DocWith(Docx.Para("See " + Person)), Docx.MainCt);
            d.Rel("rIdM2", "subDocument", "document2.xml");
        });
        Dx("parts/stylesWithEffects-variant", "stylesWithEffects holding variants", d => d.Part("word/stylesWithEffects.xml", Docx.Decl + "<w:styles " + Docx.Ns + "><w:style w:type=\"paragraph\" w:styleId=\"" + Under(Person) + "\"><w:name w:val=\"n\"/></w:style></w:styles>", "application/vnd.ms-word.stylesWithEffects+xml", "stylesWithEffects"));
        Dx("parts/footnote-settings-separator", "footnotes part with exact and variant values in footnotePr", d =>
        {
            d.Part("word/footnotes.xml", Docx.Decl + "<w:footnotes " + Docx.Ns + "><w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote><w:footnote w:id=\"1\" w:rsidR=\"" + Under(Person) + "\"><w:p><w:r><w:t>n</w:t></w:r></w:p></w:footnote></w:footnotes>", "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml", "footnotes");
        });
    }
}


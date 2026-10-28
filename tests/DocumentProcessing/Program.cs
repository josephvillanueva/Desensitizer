using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Desensitizer;

try { return Run(); }
catch(Exception failure)
{
    Console.Error.WriteLine("FAIL document-processing infrastructure: "+failure.GetType().Name+". The test run did not complete.");
    return 1;
}

static int Run()
{

// Deterministic checks for DocumentProcessor: image placeholders, bookmark
// renaming, review markup inspection, the combined residue audit, the
// per-operation package context and text encoding behavior. Every fixture is
// generated here from small XML strings. Exit code is 0 only if all checks pass.
var failures = 0;
var total = 0;
void Check(string name, bool passed, string detail = "")
{
    total++;
    if (!passed) failures++;
    Console.WriteLine((passed ? "PASS " : "FAIL ") + name + (!passed && detail.Length > 0 ? " :: " + detail : ""));
}
void Throws<T>(string name, Action action, string? mustContain = null) where T : Exception
{
    try { action(); Check(name, false, "no exception"); }
    catch (T ex) { Check(name, mustContain == null || ex.Message.Contains(mustContain), ex.Message); }
    catch (Exception ex) { Check(name, false, "wrong exception " + ex.GetType().Name + ": " + ex.Message); }
}

var work = Path.Combine(Path.GetTempPath(), "dp-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
string P(string name) => Path.Combine(work, name);

const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
const string Namespaces = "xmlns:w=\"" + W + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:v=\"urn:schemas-microsoft-com:vml\"";
var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

// Writes a minimal but valid .docx. Parts maps a name to XML, for example
// "word/header1.xml". Relationships are given as (id, type suffix, target, external).
void WriteDocx(string path, string body, Dictionary<string, string>? parts = null, bool withImage = true, params (string Id, string Type, string Target, bool External)[] relationships)
{
    parts ??= new();
    var types = new StringBuilder("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>");
    foreach (var name in parts.Keys)
    {
        var kind = name.Contains("header") ? "header" : name.Contains("footnotes") ? "footnotes" : name.Contains("comments") ? "comments" : "";
        if (kind.Length > 0) types.Append("<Override PartName=\"/" + name + "\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml." + kind + "+xml\"/>");
    }
    types.Append("</Types>");
    var rels = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
    if (withImage) rels.Append("<Relationship Id=\"rIdImg1\" Type=\"" + RelBase + "image\" Target=\"media/image1.png\"/>");
    foreach (var rel in relationships)
        rels.Append("<Relationship Id=\"" + rel.Id + "\" Type=\"" + RelBase + rel.Type + "\" Target=\"" + rel.Target + "\"" + (rel.External ? " TargetMode=\"External\"" : "") + "/>");
    rels.Append("</Relationships>");
    if (File.Exists(path)) File.Delete(path);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    void Add(string name, string text) { var e = zip.CreateEntry(name); using var w = new StreamWriter(e.Open(), new UTF8Encoding(false)); w.Write(text); }
    Add("[Content_Types].xml", types.ToString());
    Add("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"" + RelBase + "officeDocument\" Target=\"word/document.xml\"/></Relationships>");
    Add("word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document " + Namespaces + "><w:body>" + body + "</w:body></w:document>");
    Add("word/_rels/document.xml.rels", rels.ToString());
    if (withImage) { var e = zip.CreateEntry("word/media/image1.png"); using var s = e.Open(); s.Write(png, 0, png.Length); }
    foreach (var (name, xml) in parts) Add(name, xml);
}
string ReadPart(string path, string name)
{
    using var zip = ZipFile.OpenRead(path);
    var entry = zip.GetEntry(name) ?? throw new FileNotFoundException(name);
    using var r = new StreamReader(entry.Open());
    return r.ReadToEnd();
}
List<string> EntryNames(string path) { using var zip = ZipFile.OpenRead(path); return zip.Entries.Select(e => e.FullName).ToList(); }
string AllText(string path) { using var zip = ZipFile.OpenRead(path); var b = new StringBuilder(); foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels"))) { using var r = new StreamReader(e.Open()); b.Append(r.ReadToEnd()); } return b.ToString(); }
string Para(string text) => "<w:p><w:r><w:t xml:space=\"preserve\">" + text + "</w:t></w:r></w:p>";
string Picture(int id, string alt, long cx, long cy) =>
    "<w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"" + cx + "\" cy=\"" + cy + "\"/><wp:docPr id=\"" + id + "\" name=\"Picture " + id + "\"" + (alt.Length > 0 ? " descr=\"" + alt + "\"" : "") + "/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"0\" name=\"logo_acme.png\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"rIdImg1\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"" + cx + "\" cy=\"" + cy + "\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>";
Mapping Map(string value, string token, string category = "ORGANIZATION", bool manual = true) =>
    new Mapping { OriginalValue = value, Token = token, Category = category, Approved = true, IsManual = manual };
string Normalize(string s) => Regex.Replace(s, @"\s+", " ").Trim();

try
{
    // ---------------------------------------------------------------- Item 1: image placeholders
    {
        var source = P("img-source.docx");
        var body = Para("Overview of Acme Holdings platform.") +
            "<w:p><w:r><w:t xml:space=\"preserve\">See chart: </w:t></w:r><w:r>" + Picture(1, "Org chart for Acme Holdings", 2286000, 1371600) + "</w:r><w:r><w:t xml:space=\"preserve\"> end.</w:t></w:r></w:p>" +
            "<w:p><w:r>" + Picture(2, "", 914400, 457200) + "</w:r></w:p>";
        WriteDocx(source, body);
        var mappings = new[] { Map("Acme Holdings", "[ORGANIZATION_001]") };
        var output = P("img-output.docx");
        DocumentProcessor.Desensitize(source, output, mappings, new DocumentProcessingOptions { IncludeImageAltText = true });
        var text = DocumentProcessor.ReadText(output);
        Check("Image: placeholder with size and mapped alt text", text.Contains("See chart: [IMAGE REMOVED: 2.50 x 1.50 in; alt text: Org chart for [ORGANIZATION_001]] end."), text);
        Check("Image: picture without alt text gets size only", text.Contains("[IMAGE REMOVED: 1.00 x 0.50 in]"), text);
        Check("Image: original name absent from every part", !AllText(output).Contains("Acme"));
        Check("Image: picture file name never copied into the placeholder", !text.Contains("logo_acme") && !text.Contains("Picture 1"));
        Check("Image: no image part, drawing or external link remains", !EntryNames(output).Any(n => n.Contains("media/")) && !ReadPart(output, "word/document.xml").Contains("<w:drawing") && !AllText(output).Contains("TargetMode"));
        Check("Image: placeholder is one plain text run", ReadPart(output, "word/document.xml").Contains("<w:t xml:space=\"preserve\">[IMAGE REMOVED: 1.00 x 0.50 in]</w:t>"));

        // Alt text carries a name that appears nowhere else. Saved (non-manual) mappings that apply only there still apply.
        var altOnly = P("img-alt-only.docx");
        WriteDocx(altOnly, Para("Quarterly summary.") + "<w:p><w:r>" + Picture(1, "Northwind Traders logo", 914400, 914400) + "</w:r></w:p>");
        var altOnlyOut = P("img-alt-only-out.docx");
        DocumentProcessor.Desensitize(altOnly, altOnlyOut, new[] { Map("Northwind Traders", "[ORGANIZATION_002]", "ORGANIZATION", false) }, new DocumentProcessingOptions { IncludeImageAltText = true });
        var altOnlyText = DocumentProcessor.ReadText(altOnlyOut);
        Check("Image: mapping found only in alt text is applied", altOnlyText.Contains("alt text: [ORGANIZATION_002] logo]") && !AllText(altOnlyOut).Contains("Northwind"), altOnlyText);

        // Options.
        var privateAltOut = P("img-private-alt-default.docx");
        DocumentProcessor.Desensitize(altOnly, privateAltOut, Array.Empty<Mapping>());
        Check("Image default: unreviewed metadata is not copied into visible text", !AllText(privateAltOut).Contains("Northwind") && !DocumentProcessor.ReadText(privateAltOut).Contains("alt text:"));
        var custom = P("img-custom.docx");
        DocumentProcessor.Desensitize(source, custom, mappings, new DocumentProcessingOptions { ImagePlaceholder = "[PICTURE OMITTED]", IncludeImageSize = false, IncludeImageAltText = false });
        var customText = DocumentProcessor.ReadText(custom);
        Check("Image option: custom label without size or alt text", customText.Contains("chart: [PICTURE OMITTED] end.") && !customText.Contains("IMAGE REMOVED"), customText);
        var sizeOnly = P("img-size-only.docx");
        DocumentProcessor.Desensitize(source, sizeOnly, mappings, new DocumentProcessingOptions { IncludeImageAltText = false });
        Check("Image option: size without alt text", DocumentProcessor.ReadText(sizeOnly).Contains("[IMAGE REMOVED: 2.50 x 1.50 in]"));
        var plainLabel = P("img-plain-label.docx");
        DocumentProcessor.Desensitize(source, plainLabel, mappings, new DocumentProcessingOptions { ImagePlaceholder = "Image removed", IncludeImageAltText = true });
        Check("Image option: label without brackets gets details in parentheses", DocumentProcessor.ReadText(plainLabel).Contains("Image removed (2.50 x 1.50 in; alt text: Org chart for [ORGANIZATION_001])"));
        var silent = P("img-silent.docx");
        DocumentProcessor.Desensitize(source, silent, mappings, new DocumentProcessingOptions { ImagePlaceholder = "" });
        var silentText = DocumentProcessor.ReadText(silent);
        Check("Image option: empty label removes images silently", !silentText.Contains("IMAGE") && silentText.Contains("See chart:  end.") && !EntryNames(silent).Any(n => n.Contains("media/")), silentText);
        Throws<ArgumentException>("Image option: a label shaped like a replacement token is rejected", () => DocumentProcessor.Desensitize(source, P("img-bad.docx"), mappings, new DocumentProcessingOptions { ImagePlaceholder = "[IMAGE_001]" }));
        Throws<ArgumentException>("Image option: a multi-line label is rejected", () => new DocumentProcessingOptions { ImagePlaceholder = "[A\nB]" }.Validate());

        // Placeholders are not identifiers and are not unknown tokens.
        var samples = new[] { "[IMAGE REMOVED]", "[IMAGE REMOVED: 2.50 x 1.50 in]", "[IMAGE REMOVED: 2.50 x 1.50 in; alt text: Org chart for [ORGANIZATION_001]]", "[IMAGE REMOVED: 0.25 x 10.00 in; alt text: Screenshot of the approval screen]" };
        var detected = samples.SelectMany(s => Detector.Find(s)).Select(c => c.Category + ":" + c.Value).ToList();
        Check("Image: built-in detector finds no identifiers in placeholder text", detected.All(d => d.StartsWith("ORGANIZATION") || d.Contains("[ORGANIZATION_001]")) && !detected.Any(d => d.Contains("IMAGE") || d.Contains("2.50") || d.Contains("REMOVED")), string.Join(",", detected));
        var restored = P("img-restored.docx");
        var unknown = DocumentProcessor.Rehydrate(output, restored, mappings);
        var restoredText = DocumentProcessor.ReadText(restored);
        Check("Image: placeholders are not unknown tokens during rehydration", unknown == 0, "unknown=" + unknown);
        Check("Image: rehydration restores the alt text name and keeps the placeholder", restoredText.Contains("[IMAGE REMOVED: 2.50 x 1.50 in; alt text: Org chart for Acme Holdings]") && restoredText.Contains("Overview of Acme Holdings platform."), restoredText);
        var withUnknownToken = P("img-unknown.docx");
        WriteDocx(withUnknownToken, Para("Value [PERSON_009] stays unknown, [IMAGE REMOVED: 1.00 x 1.00 in] is fine."), withImage: false);
        Check("Image: an unrelated unknown token is still counted", DocumentProcessor.Rehydrate(withUnknownToken, P("img-unknown-out.docx"), mappings) == 1);

        // Audit: a mapped value remaining in alt text blocks the export; fixed wording does not.
        var generic = P("img-generic.docx");
        DocumentProcessor.Desensitize(source, generic, new[] { Map("REMOVED", "[WORD_001]", "IDENTIFIER"), Map("alt", "[WORD_002]", "IDENTIFIER"), Map("Acme Holdings", "[ORGANIZATION_001]") }, new DocumentProcessingOptions { IncludeImageAltText = true });
        Check("Image audit: mapped words in fixed placeholder wording do not block export", DocumentProcessor.ReadText(generic).Contains("[IMAGE REMOVED: 2.50 x 1.50 in; alt text: Org chart for [ORGANIZATION_001]]"));
        var tampered = P("img-tampered.docx");
        File.Copy(output, tampered, true);
        using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("word/document.xml")!;
            string xml; using (var r = new StreamReader(entry.Open())) xml = r.ReadToEnd();
            entry.Delete();
            var n = zip.CreateEntry("word/document.xml"); using var w = new StreamWriter(n.Open());
            w.Write(xml.Replace("Org chart for [ORGANIZATION_001]", "Org chart for Acme Holdings"));
        }
        Throws<InvalidDataException>("Image audit: a leaked name inside alt text is blocked by the preservation audit", () => PreservationAudit.AssertPreserved(source, tampered, mappings), "placeholder");
        var withObject = P("img-object.docx");
        File.Copy(output, withObject, true);
        using (var zip = ZipFile.Open(withObject, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("word/document.xml")!;
            string xml; using (var r = new StreamReader(entry.Open())) xml = r.ReadToEnd();
            entry.Delete();
            var n = zip.CreateEntry("word/document.xml"); using var w = new StreamWriter(n.Open());
            w.Write(xml.Replace("</w:body>", "<w:p><w:r><w:object/></w:r></w:p></w:body>"));
        }
        Throws<InvalidDataException>("Image audit: an embedded object left in the output blocks release", () => PreservationAudit.AssertPreserved(source, withObject, mappings));

        // Attribute residue still blocks, naming the token and the attribute.
        var attr = P("attr-source.docx");
        // A table caption is now stripped on export (see the hidden channel checks), so use an attribute that survives: a paragraph style name.
        WriteDocx(attr, "<w:tbl><w:tr><w:tc><w:p><w:pPr><w:pStyle w:val=\"Acme Holdings fees\"/></w:pPr><w:r><w:t>Acme Holdings</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p/>", withImage: false);
        Throws<InvalidDataException>("Audit: a mapped value left in an attribute blocks export", () => DocumentProcessor.Desensitize(attr, P("attr-out.docx"), mappings), "val attribute");
        try { DocumentProcessor.Desensitize(attr, P("attr-out2.docx"), mappings); Check("Audit: the blocking message names the token and never the original value", false, "no exception"); }
        catch (InvalidDataException ex) { Check("Audit: the blocking message names the token and never the original value", ex.Message.Contains("[ORGANIZATION_001]") && !ex.Message.Contains("Acme") && !File.Exists(P("attr-out2.docx")), ex.Message); }

        // Alternate content: one picture, not two. Text boxes and OLE objects.
        var alt = P("img-alt-content.docx");
        var choice = "<mc:AlternateContent><mc:Choice Requires=\"wps\">" + Picture(7, "Diagram", 1828800, 914400) + "</mc:Choice><mc:Fallback><w:pict><v:shape id=\"s1\" alt=\"Diagram\" style=\"width:144pt;height:72pt\"><v:imagedata r:id=\"rIdImg1\"/></v:shape></w:pict></mc:Fallback></mc:AlternateContent>";
        var ole = "<w:object><v:shape id=\"o1\" style=\"width:72pt;height:36pt\"/></w:object>";
        WriteDocx(alt, "<w:p><w:r>" + choice + "</w:r></w:p><w:p><w:r>" + ole + "</w:r></w:p><w:p><w:r><w:pict><v:shape id=\"s2\" alt=\"Seal of Acme Holdings\" style=\"width:1in;height:2cm\"/></w:pict></w:r></w:p>");
        var altOut = P("img-alt-content-out.docx");
        DocumentProcessor.Desensitize(alt, altOut, mappings, new DocumentProcessingOptions { IncludeImageAltText = true });
        var altContentText = DocumentProcessor.ReadText(altOut);
        Check("Image: drawing with VML fallback becomes one placeholder", altContentText.Split('\n')[0] == "[IMAGE REMOVED: 2.00 x 1.00 in; alt text: Diagram]", altContentText);
        Check("Image: OLE object and VML picture get placeholders with sizes", altContentText.Split('\n')[1] == "[IMAGE REMOVED: 1.00 x 0.50 in]" && altContentText.Split('\n')[2] == "[IMAGE REMOVED: 1.00 x 0.79 in; alt text: Seal of [ORGANIZATION_001]]", altContentText);
        {
            var longAlt = string.Join(" ", Enumerable.Repeat("Quarterly revenue chart for [ORGANIZATION_001] and partners", 8));
            var doc = XDocument.Parse("<w:p xmlns:w=\"" + W + "\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"><w:r><w:drawing><wp:inline><wp:extent cx=\"914400\" cy=\"914400\"/><wp:docPr id=\"1\" name=\"x\" descr=\"" + longAlt + "\"/></wp:inline></w:drawing></w:r></w:p>");
            var unit = ImagePlaceholders.FindUnits(doc).Single();
            var truncated = ImagePlaceholders.Build(unit, new DocumentProcessingOptions { MaxAltTextLength = 60, IncludeImageAltText = true }, s => s);
            var intact = Regex.Matches(truncated, @"\[ORGANIZATION_001\]").Count == Regex.Matches(truncated, @"ORGANIZATION").Count && truncated.EndsWith("...]");
            Check("Image: truncated alt text keeps every token whole and ends with an ellipsis", intact, truncated);
        }
    }

    // ---------------------------------------------------------------- Item 2: bookmarks
    {
        string Field(string instruction, string result) => "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> " + instruction + " </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>" + result + "</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";
        var source = P("bm-source.docx");
        var body =
            "<w:p><w:bookmarkStart w:id=\"1\" w:name=\"Section_Acme_Pricing\"/><w:r><w:t>Pricing for Acme</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p>" +
            "<w:p><w:bookmarkStart w:id=\"2\" w:name=\"_Toc123456\"/><w:r><w:t>Scope</w:t></w:r><w:bookmarkEnd w:id=\"2\"/></w:p>" +
            "<w:p><w:bookmarkStart w:id=\"3\" w:name=\"Appendix_B\"/><w:r><w:t>Appendix</w:t></w:r><w:bookmarkEnd w:id=\"3\"/></w:p>" +
            "<w:p>" + Field("REF Section_Acme_Pricing \\h", "Pricing for Acme") + "</w:p>" +
            "<w:p>" + Field("PAGEREF Section_Acme_Pricing \\h", "1") + "</w:p>" +
            "<w:p>" + Field("REF \"Appendix_B\" \\r \\h", "B") + "</w:p>" +
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> REF Sect</w:instrText></w:r><w:r><w:instrText xml:space=\"preserve\">ion_Acme_Pricing \\h </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>split</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" +
            "<w:p>" + Field("HYPERLINK \\l \"Section_Acme_Pricing\"", "jump") + "</w:p>" +
            "<w:p>" + Field("TOC \\o \"1-3\" \\b Appendix_B \\h \\z", "") + "<w:hyperlink w:anchor=\"_Toc123456\" w:history=\"1\" w:tooltip=\"Acme scope\"><w:r><w:t>Scope entry</w:t></w:r></w:hyperlink><w:hyperlink w:anchor=\"Section_Acme_Pricing\"><w:r><w:t>Pricing entry</w:t></w:r></w:hyperlink></w:p>" +
            "<w:p><w:fldSimple w:instr=\"REF Appendix_B \\h\"><w:r><w:t>B</w:t></w:r></w:fldSimple></w:p>" +
            "<w:p>" + Field("HYPERLINK \"http://acme.example/secret\"", "outside link") + "</w:p>" +
            "<w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>external text</w:t></w:r></w:hyperlink></w:p>" +
            "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdHdr\"/></w:sectPr>";
        var header = "<w:hdr " + Namespaces + "><w:p>" + Field("REF Section_Acme_Pricing \\h", "Pricing") + "</w:p></w:hdr>";
        WriteDocx(source, body, new() { ["word/header1.xml"] = header }, false, ("rIdHdr", "header", "header1.xml", false), ("rIdLink", "hyperlink", "http://acme.example/", true));
        var mappings = new[] { Map("Acme", "[ORGANIZATION_001]") };
        var output = P("bm-output.docx");
        DocumentProcessor.Desensitize(source, output, mappings);
        var package = AllText(output);
        var names = new List<string>();
        foreach (var part in new[] { "word/document.xml", "word/header1.xml" })
            names.AddRange(XDocument.Parse(ReadPart(output, part)).Descendants().Where(e => e.Name.LocalName == "bookmarkStart").Select(e => (string)e.Attribute(XName.Get("name", W))!));
        Check("Bookmarks: every bookmark has a neutral or Word-generated name", names.Count == 3 && names.All(n => Regex.IsMatch(n, @"^(_bm\d{4}|_Toc123456)$")), string.Join(",", names));
        Check("Bookmarks: no original bookmark name remains anywhere", !package.Contains("Section_Acme") && !package.Contains("Appendix_B") && !package.Contains("Acme"), "");
        Check("Bookmarks: hidden Word-generated name without a mapped value is kept", names.Contains("_Toc123456"));
        Check("Bookmarks: bookmark ends keep their ids so ranges stay valid", Regex.Matches(ReadPart(output, "word/document.xml"), "bookmarkEnd").Count == 3);

        // Every reference must resolve to an existing bookmark.
        var known = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var references = new List<string>();
        foreach (var part in new[] { "word/document.xml", "word/header1.xml" })
        {
            var doc = XDocument.Parse(ReadPart(output, part));
            var fieldText = new StringBuilder();
            foreach (var e in doc.Descendants())
            {
                if (e.Name.LocalName == "instrText") fieldText.Append(e.Value);
                else if (e.Name.LocalName == "fldChar" && (string?)e.Attribute(XName.Get("fldCharType", W)) == "separate") { references.Add(fieldText.ToString()); fieldText.Clear(); }
                else if (e.Name.LocalName == "fldSimple") references.Add((string?)e.Attribute(XName.Get("instr", W)) ?? "");
                else if (e.Name.LocalName == "hyperlink" && e.Attribute(XName.Get("anchor", W)) != null) references.Add("ANCHOR \"" + (string)e.Attribute(XName.Get("anchor", W))! + "\"");
            }
        }
        var targets = references.Select(r => Regex.Match(r, @"(?:^\s*(?:REF|PAGEREF|NOTEREF)\s+""?([^""\s\\]+))|(?:HYPERLINK\s+\\l\s+""([^""]+))|(?:\\b\s+([^\s\\]+))|(?:^ANCHOR ""([^""]+)"")")).Where(m => m.Success)
            .Select(m => m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value).ToList();
        Check("Bookmarks: fields and links were found to check", targets.Count == 10, targets.Count + ": " + string.Join(" | ", references));
        Check("Bookmarks: REF, PAGEREF, HYPERLINK \\l, TOC \\b, simple fields and anchors all resolve", targets.All(known.Contains), string.Join(",", targets.Where(t => !known.Contains(t))));
        Check("Bookmarks: an instruction split across runs is rewritten as one", references.Any(r => r.Contains("REF _bm0001 \\h")) && !package.Contains("Sect</w:instrText>"));
        Check("Bookmarks: both references to one bookmark use the same new name", targets.Count(t => t == names.First()) >= 5, string.Join(",", targets));
        Check("Bookmarks: external hyperlinks and field targets are still removed", !package.Contains("acme.example") && !package.Contains("TargetMode") && !Regex.IsMatch(package, @"HYPERLINK\s+""") && ReadPart(output, "word/document.xml").Contains("external text") && ReadPart(output, "word/document.xml").Contains("outside link"));
        Check("Bookmarks: internal hyperlink keeps only its anchor and history", !package.Contains("tooltip") && ReadPart(output, "word/document.xml").Contains("w:history=\"1\""));
        Check("Bookmarks: text outside bookmarks is unchanged apart from the mapping", DocumentProcessor.ReadText(output).Contains("Pricing for [ORGANIZATION_001]") && DocumentProcessor.ReadText(output).Contains("Appendix"));

        // A hidden name that contains a mapped value is renamed too.
        var hidden = P("bm-hidden.docx");
        WriteDocx(hidden, "<w:p><w:bookmarkStart w:id=\"1\" w:name=\"_Toc123456\"/><w:r><w:t>Scope</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p><w:p>" + Field("PAGEREF _Toc123456 \\h", "1") + "</w:p>", withImage: false);
        var hiddenOut = P("bm-hidden-out.docx");
        DocumentProcessor.Desensitize(hidden, hiddenOut, new[] { Map("_Toc123456", "[IDENTIFIER_001]", "IDENTIFIER") });
        var hiddenXml = ReadPart(hiddenOut, "word/document.xml");
        Check("Bookmarks: a Word-generated name that contains a mapped value is renamed with its references", hiddenXml.Contains("w:name=\"_bm0001\"") && hiddenXml.Contains("PAGEREF _bm0001") && !hiddenXml.Contains("_Toc123456"), hiddenXml);

        // Map behavior.
        var map = new BookmarkMap(name => name.Contains("Secret"));
        var doc1 = XDocument.Parse("<w:p xmlns:w=\"" + W + "\"><w:bookmarkStart w:id=\"1\" w:name=\"_bm0001\"/><w:bookmarkStart w:id=\"2\" w:name=\"Other\"/></w:p>");
        map.Collect(doc1);
        var renamedOther = map.Rename("Other");
        var again = map.Rename("other");
        Check("Bookmark map: generated names skip names already present", renamedOther == "_bm0002" && again == renamedOther, renamedOther + "/" + again);
        Check("Bookmark map: a generated name is stable when seen again", map.Rename(renamedOther) == renamedOther);
        Check("Bookmark map: kept hidden names are identity and secrets are renamed", map.Rename("_Ref987654") == "_Ref987654" && map.Rename("_GoBack") == "_GoBack" && map.Rename("_Ref1Secret") != "_Ref1Secret");
    }

    // ---------------------------------------------------------------- Item 3: review markup inspection
    {
        var source = P("review-source.docx");
        var body =
            "<w:p><w:r><w:t>Kept. </w:t></w:r><w:ins w:id=\"1\" w:author=\"A\" w:date=\"2026-01-01T00:00:00Z\"><w:r><w:t>Added words.</w:t></w:r></w:ins></w:p>" +
            "<w:p><w:ins w:id=\"2\" w:author=\"A\"><w:r><w:t>Second add.</w:t></w:r></w:ins><w:del w:id=\"3\" w:author=\"A\"><w:r><w:delText>Old text</w:delText></w:r></w:del></w:p>" +
            "<w:p><w:moveFromRangeStart w:id=\"4\" w:name=\"m1\"/><w:moveFrom w:id=\"5\" w:author=\"A\"><w:r><w:t>Moved away</w:t></w:r></w:moveFrom><w:moveFromRangeEnd w:id=\"4\"/></w:p>" +
            "<w:p><w:moveToRangeStart w:id=\"6\" w:name=\"m1\"/><w:moveTo w:id=\"7\" w:author=\"A\"><w:r><w:t>Moved here</w:t></w:r></w:moveTo><w:moveToRangeEnd w:id=\"6\"/></w:p>" +
            "<w:p><w:pPr><w:rPr><w:ins w:id=\"8\" w:author=\"A\"/></w:rPr></w:pPr><w:r><w:rPr><w:b/><w:rPrChange w:id=\"9\" w:author=\"A\"><w:rPr/></w:rPrChange></w:rPr><w:t>Formatted</w:t></w:r></w:p>" +
            "<w:p><w:commentRangeStart w:id=\"0\"/><w:r><w:t>Commented</w:t></w:r><w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r><w:commentRangeStart w:id=\"1\"/><w:r><w:t> twice</w:t></w:r><w:commentRangeEnd w:id=\"1\"/><w:r><w:commentReference w:id=\"1\"/></w:r></w:p>";
        var comments = "<w:comments " + Namespaces + "><w:comment w:id=\"0\" w:author=\"Reviewer\"><w:p><w:r><w:t>Ask Acme</w:t></w:r></w:p></w:comment><w:comment w:id=\"1\" w:author=\"Reviewer\"><w:p><w:r><w:t>Second</w:t></w:r></w:p></w:comment></w:comments>";
        WriteDocx(source, body, new() { ["word/comments.xml"] = comments }, false, ("rIdCom", "comments", "comments.xml", false));
        var summary = DocumentProcessor.InspectReviewMarkup(source);
        Check("Review markup: insertions are counted", summary.Insertions == 2 && summary.InsertedCharacters == "Added words.Second add.".Length, summary.Insertions + "/" + summary.InsertedCharacters);
        Check("Review markup: deletions are counted with their length", summary.Deletions == 1 && summary.DeletedCharacters == 8, summary.Deletions + "/" + summary.DeletedCharacters);
        Check("Review markup: a move is counted once", summary.Moves == 1, summary.Moves.ToString());
        Check("Review markup: paragraph mark revisions and formatting revisions are separate", summary.ParagraphMarkChanges == 1 && summary.FormattingChanges == 1, summary.ParagraphMarkChanges + "/" + summary.FormattingChanges);
        Check("Review markup: comments removed come from the comments part", summary.CommentsRemoved == 2, summary.CommentsRemoved.ToString());
        Check("Review markup: summary flags text affecting changes", summary.HasReviewMarkup && summary.HasTextAffectingChanges && summary.TableCellChanges == 0);
        var message = summary.Describe();
        Check("Review markup: warning text lists the counts and has no em dash", message.Contains("2 tracked insertions accepted") && message.Contains("1 tracked deletion dropped") && message.Contains("2 comments removed") && !message.Contains('\u2014') && !message.Contains('\u2013'), message);
        var text = DocumentProcessor.ReadText(source);
        Check("Review markup: reading still accepts insertions and drops deletions and comments", text.Contains("Kept. Added words.") && text.Contains("Second add.") && !text.Contains("Old text") && !text.Contains("Moved away") && text.Contains("Moved here") && !text.Contains("Ask Acme"), text);
        var clean = P("review-clean.docx");
        WriteDocx(clean, Para("Nothing tracked."), withImage: false);
        var cleanSummary = DocumentProcessor.InspectReviewMarkup(clean);
        Check("Review markup: a clean document reports nothing", !cleanSummary.HasReviewMarkup && cleanSummary.Describe() == "");
        var txt = P("review.txt");
        File.WriteAllText(txt, "plain");
        Check("Review markup: text files report nothing", !DocumentProcessor.InspectReviewMarkup(txt).HasReviewMarkup);
        var cells = P("review-cells.docx");
        WriteDocx(cells, "<w:tbl><w:tr><w:trPr/><w:tc><w:tcPr><w:cellIns w:id=\"1\" w:author=\"A\"/></w:tcPr><w:p/></w:tc></w:tr></w:tbl><w:p/>", withImage: false);
        var cellSummary = DocumentProcessor.InspectReviewMarkup(cells);
        Check("Review markup: tracked cell changes are reported", cellSummary.TableCellChanges == 1 && cellSummary.Describe().Contains("manual resolution"));
        Throws<InvalidDataException>("Review markup: reading a file with tracked cell changes is still refused", () => DocumentProcessor.ReadText(cells), "tracked table-cell");
        var before = File.ReadAllBytes(source);
        DocumentProcessor.InspectReviewMarkup(source);
        Check("Review markup: inspection does not modify the file", before.SequenceEqual(File.ReadAllBytes(source)));
    }

    // ---------------------------------------------------------------- Item 4: audit scanner against the previous implementation
    {
        // The previous audit, kept as the oracle.
        bool OldContains(string value, string sensitive, string[] tokens)
        {
            foreach (var token in tokens) value = value.Replace(token, " ", StringComparison.OrdinalIgnoreCase);
            return Regex.IsMatch(value, DocumentPrivacyPolicy.Pattern(sensitive, true), RegexOptions.IgnoreCase);
        }
        (Mapping? Mapping, bool InText, XAttribute? Attribute) OldFind(XDocument doc, List<Mapping> mappings, Func<XAttribute, bool> audited, string[] tokens)
        {
            foreach (var mapping in mappings.Where(m => !string.IsNullOrWhiteSpace(m.OriginalValue)))
            {
                var inText = doc.DescendantNodes().OfType<XText>().Any(n => OldContains(n.Value, mapping.OriginalValue, tokens));
                var attribute = doc.Descendants().Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && audited(a) && OldContains(a.Value, mapping.OriginalValue, tokens));
                if (inText || attribute != null) return (mapping, inText, attribute);
            }
            return (null, false, null);
        }
        string[] TokensOf(IEnumerable<Mapping> mappings) => mappings.Select(m => m.Token).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Length).ToArray();
        bool Same((Mapping? Mapping, bool InText, XAttribute? Attribute) old, ResidueHit? now) =>
            old.Mapping == null ? now == null : now != null && ReferenceEquals(old.Mapping, now.Mapping) && old.InText == now.InText && ReferenceEquals(old.Attribute, now.Attribute);
        XDocument Doc(IEnumerable<string> texts, IEnumerable<string>? attributes = null)
        {
            var root = new XElement(XName.Get("body", W));
            foreach (var t in texts) root.Add(new XElement(XName.Get("p", W), new XElement(XName.Get("t", W), t)));
            var i = 0;
            foreach (var a in attributes ?? Array.Empty<string>()) root.Add(new XElement(XName.Get("x", W), new XAttribute(XName.Get("v" + i++, W), a)));
            return new XDocument(root);
        }
        Func<XAttribute, bool> all = _ => true;

        var corpus = new List<(string Name, string[] Texts, string[] Attributes, Mapping[] Mappings)>
        {
            ("plain hit", new[] { "Contact Acme Holdings today" }, new string[0], new[] { Map("Acme Holdings", "[ORGANIZATION_001]") }),
            ("case-insensitive hit", new[] { "ACME HOLDINGS" }, new string[0], new[] { Map("Acme Holdings", "[ORGANIZATION_001]") }),
            ("flexible whitespace", new[] { "Acme \t\n  Holdings" }, new string[0], new[] { Map("Acme Holdings", "[ORGANIZATION_001]") }),
            ("token masks mapped text", new[] { "[ORGANIZATION_001] only" }, new string[0], new[] { Map("ORGANIZATION", "[ORGANIZATION_001]"), Map("Zed", "[PERSON_001]", "PERSON") }),
            ("token masked, other remains", new[] { "[ORGANIZATION_001] and Zed" }, new string[0], new[] { Map("ORGANIZATION", "[ORGANIZATION_001]"), Map("Zed", "[PERSON_001]", "PERSON") }),
            ("word boundary left", new[] { "ZAcme" }, new string[0], new[] { Map("Acme", "[ORGANIZATION_001]") }),
            ("word boundary right", new[] { "Acmes" }, new string[0], new[] { Map("Acme", "[ORGANIZATION_001]") }),
            ("numeric decimal part", new[] { "Version 3.14 and 2/14" }, new string[0], new[] { Map("14", "[IDENTIFIER_001]", "IDENTIFIER") }),
            ("numeric standalone", new[] { "Total 14 items" }, new string[0], new[] { Map("14", "[IDENTIFIER_001]", "IDENTIFIER") }),
            ("attribute only", new[] { "clean" }, new[] { "Acme Holdings fees" }, new[] { Map("Acme Holdings", "[ORGANIZATION_001]") }),
            ("text and attribute", new[] { "Acme" }, new[] { "Acme" }, new[] { Map("Acme", "[ORGANIZATION_001]") }),
            ("second mapping wins when first is absent", new[] { "only Zed here" }, new string[0], new[] { Map("Acme", "[ORGANIZATION_001]"), Map("Zed", "[PERSON_001]", "PERSON") }),
            ("first mapping wins when both present", new[] { "Zed and Acme" }, new string[0], new[] { Map("Zed", "[PERSON_001]", "PERSON"), Map("Acme", "[ORGANIZATION_001]") }),
            ("duplicate mappings", new[] { "Acme" }, new string[0], new[] { Map("Acme", "[ORGANIZATION_001]"), Map("Acme", "[ORGANIZATION_009]") }),
            ("blank mapping ignored", new[] { "text" }, new string[0], new[] { Map("  ", "[X_001]"), Map("text", "[Y_001]") }),
            ("token with a space", new[] { "[ORG ANIZATION] stays masked" }, new string[0], new[] { Map("ANIZATION", "[ORG ANIZATION]") }),
            ("masking creates a space that matches", new[] { "AB[T_001]CD" }, new string[0], new[] { Map("AB CD", "[T_001]") }),
            ("regex metacharacters", new[] { "price (a+b)* [x]?" }, new string[0], new[] { Map("(a+b)*", "[T_002]"), Map("[x]?", "[T_003]") }),
            ("unicode", new[] { "Café Müller GmbH" }, new string[0], new[] { Map("Müller", "[PERSON_002]", "PERSON") }),
            ("empty document", new string[0], new string[0], new[] { Map("Acme", "[ORGANIZATION_001]") }),
            ("no mappings", new[] { "anything" }, new[] { "anything" }, new Mapping[0]),
        };
        var corpusOk = 0;
        foreach (var (name, texts, attributes, mappings) in corpus)
        {
            var list = mappings.ToList();
            var tokens = TokensOf(list);
            var doc = Doc(texts, attributes);
            var old = OldFind(doc, list, all, tokens);
            var now = new ResidueScanner(list, tokens).Find(doc, all);
            var same = Same(old, now);
            if (same) corpusOk++;
            Check("Audit oracle: " + name, same, "old=" + old.Mapping?.OriginalValue + "/" + old.InText + " new=" + now?.Mapping.OriginalValue + "/" + now?.InText);
        }
        Check("Audit oracle: the whole handwritten corpus agrees", corpusOk == corpus.Count);

        // Attribute filter is honored like the old predicate.
        var filtered = Doc(new[] { "clean" }, new[] { "Acme" });
        var filterOld = OldFind(filtered, new List<Mapping> { Map("Acme", "[ORGANIZATION_001]") }, a => false, new string[0]);
        var filterNew = new ResidueScanner(new[] { Map("Acme", "[ORGANIZATION_001]") }, new string[0]).Find(filtered, a => false);
        Check("Audit oracle: unaudited attributes are skipped", filterOld.Mapping == null && filterNew == null);

        var variantScanner=new ResidueScanner(new[]{Map("Zelda Quimby-Orsino","[PERSON_001]","PERSON")},new[]{"[PERSON_001]"});
        foreach(var value in new[]{"Zelda_Quimby_Orsino","Ｚｅｌｄａ Quimby-Orsino","Zel\u200Bda Quimby-Orsino",
            "Zеlda Quimby-Orsino","Quimby-Orsino, Zelda",new string("Zelda Quimby-Orsino".Reverse().ToArray()),
            Convert.ToBase64String(Encoding.UTF8.GetBytes("Zelda Quimby-Orsino")),Convert.ToHexString(Encoding.UTF8.GetBytes("Zelda Quimby-Orsino"))})
            Check("Hidden audit catches identifying variant",variantScanner.FindHiddenVariant(value)!=null);
        Check("Hidden audit shields replacement tokens",variantScanner.FindHiddenVariant("[PERSON_001] approved the request")==null);
        Check("Hidden audit retains business quantities and rules",variantScanner.FindHiddenVariant("The system shall approve amounts below 5000 within 3 days.")==null);
        Check("Hidden audit joins split XML text",variantScanner.FindPackageVariant(XDocument.Parse("<r><a>Zelda Quimby</a><b>-Orsino</b></r>"))!=null);
        Check("Hidden audit checks attribute separator variants",variantScanner.FindPackageVariant(XDocument.Parse("<r owner='Zelda_Quimby_Orsino'/>"))!=null);

        // Randomized comparison with a fixed seed.
        var random = new Random(20261010);
        string[] words = { "Acme", "Holdings", "acme holdings", "ACME", "Jo Tan", "Tan", "[PERSON_001]", "[ORG_002]", "4", "12", "3.5", "x@y.com", "a b", "_", "Zed", "[Z 1]", "é", "AB", "CD" };
        string[] glue = { " ", "  ", "\t", "\n", ".", ",", "-", "/", "_", "", "(", ")" };
        string RandomText() => string.Concat(Enumerable.Range(0, random.Next(0, 6)).Select(_ => words[random.Next(words.Length)] + glue[random.Next(glue.Length)]));
        var mismatches = 0;
        const int Rounds = 3000;
        for (var round = 0; round < Rounds; round++)
        {
            var mappings = Enumerable.Range(0, random.Next(0, 5)).Select(i => Map(words[random.Next(words.Length)] + (random.Next(4) == 0 ? " " + words[random.Next(words.Length)] : ""), random.Next(3) == 0 ? "" : "[T_" + random.Next(1, 4) + "]")).ToList();
            var tokens = TokensOf(mappings.Concat(new[] { Map("x", "[PERSON_001]"), Map("y", "[Z 1]") }));
            var doc = Doc(Enumerable.Range(0, random.Next(0, 4)).Select(_ => RandomText()), Enumerable.Range(0, random.Next(0, 3)).Select(_ => RandomText()));
            var old = OldFind(doc, mappings, all, tokens);
            var now = new ResidueScanner(mappings, tokens).Find(doc, all);
            if (!Same(old, now)) { mismatches++; if (mismatches <= 3) Console.WriteLine("  mismatch round " + round + ": " + string.Join("|", mappings.Select(m => m.OriginalValue)) + " / " + doc); }
        }
        Check("Audit oracle: " + Rounds + " randomized documents agree with the previous implementation", mismatches == 0, mismatches + " mismatches");

        // Timing with a few hundred mappings.
        var many = Enumerable.Range(0, 400).Select(i => Map("Person" + i + " Surname" + i, "[PERSON_" + (i + 1).ToString("D3") + "]", "PERSON")).ToList();
        var manyTokens = TokensOf(many);
        var bigTexts = Enumerable.Range(0, 6000).Select(i => i % 7 == 0 ? "Contact [PERSON_" + ((i % 400) + 1).ToString("D3") + "] about order " + i + " in the finance system" : "Ordinary requirement text number " + i + " describing a workflow step and its conditions").ToList();
        var bigAttributes = Enumerable.Range(0, 2000).Select(i => "style" + i).ToList();
        var big = Doc(bigTexts, bigAttributes);
        var watch = Stopwatch.StartNew();
        var scanner = new ResidueScanner(many, manyTokens);
        var clear = scanner.Find(big, all);
        watch.Stop();
        var newMs = watch.ElapsedMilliseconds;
        Check("Audit timing: 400 mappings over 6000 text nodes and 2000 attributes finish quickly with no false hit", clear == null && newMs < 5000, newMs + " ms");
        big.Root!.Add(new XElement(XName.Get("p", W), new XElement(XName.Get("t", W), "late leak of person250  surname250 here")));
        var planted = scanner.Find(big, all);
        Check("Audit timing: a planted residue is found and attributed to the right mapping", planted != null && planted.InText && planted.Mapping.OriginalValue == "Person250 Surname250", planted?.Mapping.OriginalValue);
        var sample = Doc(bigTexts.Take(60), bigAttributes.Take(20));
        watch.Restart();
        var oldSample = OldFind(sample, many, all, manyTokens);
        watch.Stop();
        var oldMsPerNode = watch.Elapsed.TotalMilliseconds / 80.0;
        var newMsPerNode = newMs / 8000.0;
        Console.WriteLine("  timing: new " + newMs + " ms for 8000 nodes (" + newMsPerNode.ToString("F4") + " ms/node), previous " + oldMsPerNode.ToString("F2") + " ms/node, about " + (oldMsPerNode / Math.Max(newMsPerNode, 0.0001)).ToString("F0") + "x slower per node");
        Check("Audit timing: per node cost is far below the previous implementation", oldSample.Mapping == null && newMsPerNode * 10 < oldMsPerNode, "old " + oldMsPerNode + " new " + newMsPerNode);
    }

    // ---------------------------------------------------------------- Item 5: package context
    {
        var source = P("ctx-source.docx");
        var body = "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Cell Acme</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Value</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" + Para("Body text about Acme.") + "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdHdr\"/></w:sectPr>";
        var header = "<w:hdr " + Namespaces + ">" + Para("Header for Acme") + "</w:hdr>";
        var notes = "<w:footnotes " + Namespaces + "><w:footnote w:id=\"1\">" + Para("Note about Acme") + "</w:footnote></w:footnotes>";
        WriteDocx(source, body, new() { ["word/header1.xml"] = header, ["word/footnotes.xml"] = notes }, false, ("rIdHdr", "header", "header1.xml", false));
        var mappings = new[] { Map("Acme", "[ORGANIZATION_001]") };
        var textNoContext = DocumentProcessor.ReadText(source);
        var tableNoContext = DocumentProcessor.ReadTableContext(source, textNoContext);
        string tableSection = "";
        var searchStart = 0;
        using (var package = DocumentProcessor.OpenContext(source))
        {
            var text = DocumentProcessor.ReadText(source, package);
            var table = DocumentProcessor.ReadTableContext(source, text, package);
            var review = DocumentProcessor.InspectReviewMarkup(source, package);
            var textAgain = DocumentProcessor.ReadText(source, package);
            Check("Context: results match the version without a context", text == textNoContext && textAgain == text && table != null && tableNoContext != null && table.TableCount == tableNoContext.TableCount && table.RowCount == tableNoContext.RowCount && !review.HasReviewMarkup);
            tableSection = table!.ForSection(text, ref searchStart);
            Check("Context: each of three parts is read once and parsed once for four calls", package.EntriesRead == 3 && package.DocumentsParsed == 3, package.EntriesRead + "/" + package.DocumentsParsed);
            var output = P("ctx-output.docx");
            DocumentProcessor.Desensitize(source, output, mappings, null, package);
            Check("Context: export reuses cached reads and parses each part once more only for whitespace-preserving mode", package.EntriesRead == 3 && package.DocumentsParsed == 6, package.EntriesRead + "/" + package.DocumentsParsed);
            var plain = P("ctx-plain.docx");
            DocumentProcessor.Desensitize(source, plain, mappings);
            Check("Context: export output is identical with and without a context", new[] { "word/document.xml", "word/header1.xml", "word/footnotes.xml" }.All(part => ReadPart(output, part) == ReadPart(plain, part)) && DocumentProcessor.ReadText(output) == DocumentProcessor.ReadText(plain));
            Check("Context: protected text is mapped", !DocumentProcessor.ReadText(output).Contains("Acme") && DocumentProcessor.ReadText(output).Contains("Header for [ORGANIZATION_001]"));
            Check("Context: the table section is available from the shared parse", tableSection.Length > 0 || table.TableCount == 1);
        }
        var otherFile = P("ctx-other.docx");
        WriteDocx(otherFile, Para("Other"), withImage: false);
        using (var package = DocumentProcessor.OpenContext(otherFile))
            Throws<ArgumentException>("Context: a context for a different file is rejected", () => DocumentProcessor.ReadText(source, package), "different file");
        var disposed = DocumentProcessor.OpenContext(source);
        disposed.Dispose();
        Throws<ObjectDisposedException>("Context: a disposed context cannot be used", () => DocumentProcessor.ReadText(source, disposed));
        Throws<InvalidDataException>("Context: text files cannot be opened as a package", () => DocumentProcessor.OpenContext(P("review.txt")));
        // Without a context, nothing stays open: the file can be deleted right after a call.
        var temp = P("ctx-delete.docx");
        File.Copy(source, temp, true);
        DocumentProcessor.ReadText(temp);
        DocumentProcessor.Desensitize(temp, P("ctx-delete-out.docx"), mappings);
        File.Delete(temp);
        Check("Context: calls without a context release the file", !File.Exists(temp));
    }

    // ---------------------------------------------------------------- Item 6: behavior after reformatting
    {
        var mappings = new[] { Map("Acme", "[ORGANIZATION_001]") };
        var cases = new (string Name, byte[] Preamble, Encoding Encoding)[]
        {
            ("UTF-8 with BOM", new byte[] { 0xEF, 0xBB, 0xBF }, new UTF8Encoding(true)),
            ("UTF-16 LE", new byte[] { 0xFF, 0xFE }, Encoding.Unicode),
            ("UTF-16 BE", new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode),
            ("UTF-8 without BOM", new byte[0], new UTF8Encoding(false)),
        };
        foreach (var (name, preamble, encoding) in cases)
        {
            var path = P("enc-" + name.Replace(' ', '-') + ".txt");
            File.WriteAllBytes(path, preamble.Concat(encoding.GetBytes("Acme line one\nCafé Acme two")).ToArray());
            var output = path + ".out.txt";
            DocumentProcessor.Desensitize(path, output, mappings);
            var bytes = File.ReadAllBytes(output);
            Check("Text encoding preserved: " + name, bytes.Take(preamble.Length).SequenceEqual(preamble) && encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length).TrimStart('\uFEFF') == "[ORGANIZATION_001] line one\nCafé [ORGANIZATION_001] two", Convert.ToHexString(bytes.Take(8).ToArray()));
            Check("Text read for " + name, DocumentProcessor.ReadText(path) == "Acme line one\nCafé Acme two");
        }
        Throws<InvalidDataException>("Unsupported extension is refused", () => DocumentProcessor.ReadText(P("a.pdf")), "supported");
    }

    // ---------------------------------------------------------------- Export gate items G1 to G5 (GateTests.cs)
    {
        var gate = GateTests.Run(work);
        total += gate.Total;
        failures += gate.Failures;
    }
}
finally
{
    try { Directory.Delete(work, true); } catch { }
}

Console.WriteLine($"Document processing checks: {total - failures} passed, {failures} failed, {total} total.");
return failures == 0 ? 0 : 1;
}

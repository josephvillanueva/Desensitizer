using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Desensitizer;

// Export gate checks (items G1 to G5): hidden text channels, no unredacted
// content on disk, dropped mappings, the output allow-list, zip safety and
// exception messages. Every fixture is a small synthetic .docx with fictional
// values. Includes negative checks: structural fields and ordinary text stay.
public static class GateTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Ns = "xmlns:w=\"" + W + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
    private const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string Wml = "application/vnd.openxmlformats-officedocument.wordprocessingml.";

    private static int total, failures;
    private static void Check(string name, bool passed, string detail = "")
    {
        total++;
        if (!passed) failures++;
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name + (!passed && detail.Length > 0 ? " :: " + detail : ""));
    }
    private static void Throws<T>(string name, Action action, Func<string, bool>? messageOk = null) where T : Exception
    {
        try { action(); Check(name, false, "no exception"); }
        catch (T ex) { Check(name, messageOk == null || messageOk(ex.Message), ex.Message); }
        catch (Exception ex) { Check(name, false, "wrong exception " + ex.GetType().Name + ": " + ex.Message); }
    }

    // A part to add: name, bytes, content type (null uses the default for its extension),
    // and an optional relationship from the main document (type suffix, target relative to word/).
    private sealed record Extra(string Name, byte[] Data, string? ContentType = null, string? RelType = null, string? RelTarget = null, bool PackageRel = false);
    private static Extra Xml(string name, string xml, string? contentType = null, string? relType = null, string? relTarget = null) =>
        new(name, Encoding.UTF8.GetBytes(xml), contentType, relType, relTarget);

    private static void Build(string path, string body, IEnumerable<Extra>? extras = null, string? zipComment = null)
    {
        extras ??= Array.Empty<Extra>();
        var types = new StringBuilder("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"bin\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.printerSettings\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/><Override PartName=\"/word/document.xml\" ContentType=\"" + Wml + "document.main+xml\"/>");
        var rels = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        var packageRels = new StringBuilder("<Relationship Id=\"rId1\" Type=\"" + RelBase + "officeDocument\" Target=\"word/document.xml\"/>");
        var n = 0;
        foreach (var extra in extras)
        {
            n++;
            if (extra.ContentType != null) types.Append("<Override PartName=\"/" + extra.Name + "\" ContentType=\"" + extra.ContentType + "\"/>");
            if (extra.PackageRel) packageRels.Append("<Relationship Id=\"rIdP" + n + "\" Type=\"" + RelBase + extra.RelType + "\" Target=\"" + extra.Name + "\"/>");
            else if (extra.RelType != null) rels.Append("<Relationship Id=\"rIdX" + n + "\" Type=\"" + RelBase + extra.RelType + "\" Target=\"" + extra.RelTarget + "\"/>");
        }
        types.Append("</Types>");
        rels.Append("</Relationships>");
        if (File.Exists(path)) File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data) { var e = zip.CreateEntry(name); using var s = e.Open(); s.Write(data, 0, data.Length); }
            Add("[Content_Types].xml", Encoding.UTF8.GetBytes(types.ToString()));
            Add("_rels/.rels", Encoding.UTF8.GetBytes("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + packageRels + "</Relationships>"));
            Add("word/document.xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document " + Ns + "><w:body>" + body + "</w:body></w:document>"));
            Add("word/_rels/document.xml.rels", Encoding.UTF8.GetBytes(rels.ToString()));
            foreach (var extra in extras) Add(extra.Name, extra.Data);
        }
        if (zipComment != null) SetZipComment(path, zipComment);
    }

    // Writes a comment into the end-of-central-directory record.
    private static void SetZipComment(string path, string comment)
    {
        var bytes = File.ReadAllBytes(path);
        var eocd = -1;
        for (var i = bytes.Length - 22; i >= 0; i--)
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 5 && bytes[i + 3] == 6) { eocd = i; break; }
        if (eocd < 0) throw new InvalidOperationException("no end of central directory");
        var text = Encoding.UTF8.GetBytes(comment);
        bytes[eocd + 20] = (byte)(text.Length & 255);
        bytes[eocd + 21] = (byte)(text.Length >> 8);
        File.WriteAllBytes(path, bytes.Take(eocd + 22).Concat(text).ToArray());
    }

    private static string Para(string text) => "<w:p><w:r><w:t xml:space=\"preserve\">" + text + "</w:t></w:r></w:p>";
    private static Mapping Map(string value, string token, string category = "ORGANIZATION", bool manual = true) =>
        new() { OriginalValue = value, Token = token, Category = category, Approved = true, IsManual = manual };
    private static string AllText(string path) { using var zip = ZipFile.OpenRead(path); var b = new StringBuilder(); foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels"))) { using var r = new StreamReader(e.Open()); b.Append(r.ReadToEnd()); } return b.ToString(); }
    private static string ReadPart(string path, string name) { using var zip = ZipFile.OpenRead(path); using var r = new StreamReader((zip.GetEntry(name) ?? throw new FileNotFoundException(name)).Open()); return r.ReadToEnd(); }
    private static List<string> Names(string path) { using var zip = ZipFile.OpenRead(path); return zip.Entries.Select(e => e.FullName).ToList(); }
    private static bool Leftovers(string folder, params string[] allowed) =>
        Directory.EnumerateFiles(folder).Any(f => !allowed.Contains(Path.GetFileName(f)));

    public static (int Total, int Failures) Run(string work)
    {
        total = 0; failures = 0;
        string P(string name) => Path.Combine(work, name);
        string Fresh(string name) { var d = P(name); if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); return d; }
        var acme = new[] { Map("Acme Holdings", "[ORGANIZATION_001]") };

        // ------------------------------------------------------------ G1 hidden text channels
        {
            var folder = Fresh("g1");
            var body =
                Para("Intro for Acme Holdings.") +
                "<w:sdt><w:sdtPr><w:alias w:val=\"Zephyrion client\"/><w:tag w:val=\"zephyrion-tag\"/><w:id w:val=\"11\"/><w:dropDownList><w:listItem w:displayText=\"Zephyrion LLC\" w:value=\"zl\"/></w:dropDownList></w:sdtPr><w:sdtContent>" + Para("Control text") + "</w:sdtContent></w:sdt>" +
                "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> MERGEFIELD ClientZephyrion \\* MERGEFORMAT </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>Cached merge result</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" +
                "<w:p><w:fldSimple w:instr=\" DOCPROPERTY ZephyrionProp \"><w:r><w:t>Property result</w:t></w:r></w:fldSimple></w:p>" +
                "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> IF \"Zephyrion\" = \"x\" \"yes\" \"no\" </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>no</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" +
                "<w:p><w:bookmarkStart w:id=\"0\" w:name=\"_Toc123\"/><w:r><w:t>Heading text</w:t></w:r><w:bookmarkEnd w:id=\"0\"/></w:p>" +
                "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" +
                "<w:p><w:fldSimple w:instr=\" PAGEREF _Toc123 \\h \"><w:r><w:t>2</w:t></w:r></w:fldSimple></w:p>" +
                "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> HYPERLINK \\l \"_Toc123\" </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>Jump to heading</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" +
                "<w:p><w:smartTag w:uri=\"urn:zephyrion:tags\" w:element=\"place\"><w:smartTagPr><w:attr w:name=\"Zephyrion\" w:val=\"x\"/></w:smartTagPr><w:r><w:t>Smart tagged text</w:t></w:r></w:smartTag></w:p>" +
                "<w:tbl><w:tblPr><w:tblCaption w:val=\"Zephyrion table caption\"/><w:tblDescription w:val=\"Zephyrion table description\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"3000\"/></w:tblGrid><w:tr><w:tc>" + Para("Cell text") + "</w:tc></w:tr></w:tbl>" +
                "<w:p/>";
            var extras = new[]
            {
                Xml("word/settings.xml", "<w:settings " + Ns + "><w:docVars><w:docVar w:name=\"ClientZephyrion\" w:val=\"Zephyrion Holdings\"/></w:docVars></w:settings>", Wml + "settings+xml", "settings", "settings.xml"),
                Xml("word/numbering.xml", "<w:numbering " + Ns + "><w:abstractNum w:abstractNumId=\"0\"><w:name w:val=\"Zephyrion list\"/><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"Zephyrion-%1\"/></w:lvl><w:lvl w:ilvl=\"1\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"Article %2.\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>", Wml + "numbering+xml", "numbering", "numbering.xml"),
                Xml("word/glossary/document.xml", "<w:glossaryDocument " + Ns + "><w:docParts><w:docPart><w:docPartPr><w:name w:val=\"ZephyrionBlock\"/><w:description w:val=\"Zephyrion boilerplate\"/></w:docPartPr><w:docPartBody>" + Para("Zephyrion glossary text") + "</w:docPartBody></w:docPart></w:docParts></w:glossaryDocument>", Wml + "document.glossary+xml", "glossaryDocument", "glossary/document.xml"),
            };
            var source = P("g1/source.docx");
            Build(source, body, extras);
            var output = P("g1/output.docx");
            DocumentProcessor.Desensitize(source, output, acme);
            var all = AllText(output);
            Check("G1: a value that appears only in hidden channels is absent from every part", !all.Contains("Zephyrion", StringComparison.OrdinalIgnoreCase) && !all.Contains("zephyrion"), Regex.Match(all, ".{30}[Zz]ephyrion.{30}").Value);
            Check("G1: the mapped body value is still replaced", !all.Contains("Acme") && DocumentProcessor.ReadText(output).Contains("Intro for [ORGANIZATION_001]."));
            Check("G1: the glossary part and its relationship are gone", !Names(output).Any(x => x.Contains("glossary")) && !ReadPart(output, "word/_rels/document.xml.rels").Contains("glossary") && !ReadPart(output, "[Content_Types].xml").Contains("glossary"));
            var document = ReadPart(output, "word/document.xml");
            Check("G1: sdt alias, tag, list items and data binding are removed but the control and its text stay", !document.Contains("w:alias") && !document.Contains("w:tag ") && !document.Contains("listItem") && document.Contains("<w:sdt>") && document.Contains("Control text"));
            Check("G1: table caption and description removed, the table stays", !document.Contains("tblCaption") && !document.Contains("tblDescription") && document.Contains("<w:tbl>") && document.Contains("Cell text"));
            Check("G1: smart tag wrapper removed, its text kept", !document.Contains("smartTag") && document.Contains("Smart tagged text"));
            Check("G1: document variables removed from settings", !ReadPart(output, "word/settings.xml").Contains("docVar"));
            var numbering = ReadPart(output, "word/numbering.xml");
            Check("G1: list number text keeps placeholders and structural words, drops other literals", numbering.Contains("w:val=\"-%1\"") && numbering.Contains("w:val=\"Article %2.\"") && !numbering.Contains("w:name"), numbering);
            Check("G1: non-structural field instructions (MERGEFIELD, DOCPROPERTY, IF) are blank and keep their cached results", !document.Contains("MERGEFIELD") && !document.Contains("DOCPROPERTY") && !document.Contains(" IF ") && !document.Contains("fldSimple w:instr=\" DOCPROPERTY") && DocumentProcessor.ReadText(output).Contains("Cached merge result") && DocumentProcessor.ReadText(output).Contains("Property result"));
            // Negative: structural fields still work.
            Check("G1 negative: PAGE, PAGEREF and internal HYPERLINK \\l fields keep their instructions", document.Contains(" PAGE ") && document.Contains("PAGEREF _Toc123") && Regex.IsMatch(document, "HYPERLINK \\\\l \"_Toc123\""), document.Length > 0 ? Regex.Match(document, "instr[^>]*>[^<]*").Value : "");
            Check("G1 negative: bookmark and paragraph text are preserved", document.Contains("w:name=\"_Toc123\"") && DocumentProcessor.ReadText(output).Contains("Heading text") && DocumentProcessor.ReadText(output).Contains("Jump to heading"));

            // The same channels as labelled lines for detectors.
            var lines = DocumentProcessor.ReadHiddenChannels(source);
            bool Has(string expected) => lines.Any(l => l == expected);
            Check("G1 describe: sdt alias, tag and list item", Has("sdt alias: Zephyrion client") && Has("sdt tag: zephyrion-tag") && Has("sdt list item: Zephyrion LLC"), string.Join(" | ", lines));
            Check("G1 describe: document variable name and value", Has("document variable name: ClientZephyrion") && Has("document variable value: Zephyrion Holdings"));
            Check("G1 describe: field codes", lines.Any(l => l.StartsWith("field code: MERGEFIELD ClientZephyrion")) && Has("field code: DOCPROPERTY ZephyrionProp") && lines.Any(l => l.StartsWith("field code: IF \"Zephyrion\"")));
            Check("G1 describe: glossary entry name and description", Has("glossary entry name: ZephyrionBlock") && Has("glossary entry description: Zephyrion boilerplate"));
            Check("G1 describe: list number text and list name", Has("list number text: Zephyrion-%1") && Has("list name: Zephyrion list"));
            Check("G1 describe: smart tag attributes and table text", Has("smart tag uri: urn:zephyrion:tags") && Has("tag property: Zephyrion") && Has("table caption: Zephyrion table caption") && Has("table description: Zephyrion table description"));
            Check("G1 describe negative: structural fields and ordinary numbering are not listed", !lines.Any(l => l.Contains("PAGE") || l.Contains("HYPERLINK") || l.Contains("Article")), string.Join(" | ", lines));
            using (var context = DocumentProcessor.OpenContext(source))
                Check("G1 describe: works with a shared context and gives the same lines", DocumentProcessor.ReadHiddenChannels(source, context).SequenceEqual(lines));

            // A value mapped and present in a channel the stripper does not handle still blocks.
            var surviving = P("g1/surviving.docx");
            Build(surviving, "<w:p><w:pPr><w:pStyle w:val=\"AcmeHoldingsStyle\"/></w:pPr><w:r><w:t>Plain</w:t></w:r></w:p>");
            Throws<InvalidDataException>("G1 audit: a mapped value surviving in an unstripped attribute still blocks", () => DocumentProcessor.Desensitize(surviving, P("g1/surviving-out.docx"), new[] { Map("AcmeHoldingsStyle", "[ORGANIZATION_002]") }), m => m.Contains("[ORGANIZATION_002]") && !m.Contains("AcmeHoldingsStyle"));
            // The scanner itself sees mapped values in every one of these channels.
            var hits = 0;
            foreach (var xml in new[] {
                "<w:sdt " + Ns + "><w:sdtPr><w:alias w:val=\"Acme Holdings\"/></w:sdtPr></w:sdt>",
                "<w:docVars " + Ns + "><w:docVar w:name=\"a\" w:val=\"Acme Holdings\"/></w:docVars>",
                "<w:p " + Ns + "><w:r><w:instrText>MERGEFIELD Acme Holdings</w:instrText></w:r></w:p>" })
                if (new ResidueScanner(acme, new string[0]).Find(XDocument.Parse(xml), _ => true) != null) hits++;
            Check("G1 audit: the residue scanner finds mapped values in alias, docVar and instrText", hits == 3, hits.ToString());
        }

        // ------------------------------------------------------------ G2 nothing unredacted on disk
        {
            var folder = Fresh("g2");
            var source = P("g2/source.docx");
            Build(source, Para("Acme Holdings signed."));
            var output = P("g2/output.docx");
            var created = new List<string>();
            using (var watcher = new FileSystemWatcher(folder) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.FileName })
            {
                watcher.Created += (_, e) => { lock (created) created.Add(e.Name ?? ""); };
                DocumentProcessor.Desensitize(source, output, acme);
                Thread.Sleep(400);
            }
            Check("G2: the export never creates the output path by copying the source", !created.Contains("output.docx"), string.Join(",", created));
            Check("G2: only the finished output remains beside the source", File.Exists(output) && !Leftovers(folder, "source.docx", "output.docx"), string.Join(",", Directory.GetFiles(folder)));
            Check("G2: the finished output holds no unredacted value", !AllText(output).Contains("Acme"));

            // A blocked export (audit failure) leaves no output and no temporary file.
            var blockedSource = P("g2/blocked.docx");
            Build(blockedSource, "<w:p><w:pPr><w:pStyle w:val=\"AcmeHoldingsStyle\"/></w:pPr><w:r><w:t>Plain</w:t></w:r></w:p>");
            var blockedOut = P("g2/blocked-out.docx");
            Throws<InvalidDataException>("G2: a blocked export throws", () => DocumentProcessor.Desensitize(blockedSource, blockedOut, new[] { Map("AcmeHoldingsStyle", "[ORGANIZATION_002]") }));
            Check("G2: a blocked export leaves no output file and no temporary file", !File.Exists(blockedOut) && !Leftovers(folder, "source.docx", "output.docx", "blocked.docx"), string.Join(",", Directory.GetFiles(folder)));

            // An exception halfway (a damaged part read after document.xml was already transformed) leaves nothing.
            var damaged = P("g2/damaged.docx");
            Build(damaged, Para("Acme Holdings in body."), new[] { Xml("word/footer1.xml", "<w:ftr " + Ns + "><w:p><w:r><w:t>unclosed", Wml + "footer+xml", "footer", "footer1.xml") });
            var damagedOut = P("g2/damaged-out.docx");
            Throws<InvalidDataException>("G2: a damaged part throws a readable error without document text", () => DocumentProcessor.Desensitize(damaged, damagedOut, acme), m => !m.Contains("unclosed") && !m.Contains("Acme") && m.Contains("word/footer1.xml"));
            Check("G2: an exception halfway leaves no output file and no temporary file", !File.Exists(damagedOut) && !Leftovers(folder, "source.docx", "output.docx", "blocked.docx", "damaged.docx"), string.Join(",", Directory.GetFiles(folder)));

            // An unwritable destination fails without leaving a temporary file.
            Throws<DirectoryNotFoundException>("G2: an unreachable output folder fails cleanly", () => DocumentProcessor.Desensitize(source, P("g2/missing-folder/out.docx"), acme));

            // Text files and rehydration follow the same rule.
            var txt = P("g2/source.txt");
            File.WriteAllText(txt, "Acme Holdings agreed.");
            var txtOut = P("g2/out.txt");
            DocumentProcessor.Desensitize(txt, txtOut, acme);
            Check("G2: a text export works and leaves no temporary file", File.ReadAllText(txtOut) == "[ORGANIZATION_001] agreed." && !Leftovers(folder, "source.docx", "output.docx", "blocked.docx", "damaged.docx", "source.txt", "out.txt"));
            var rehydrated = P("g2/restored.docx");
            DocumentProcessor.Rehydrate(output, rehydrated, acme);
            Check("G2: rehydration still restores the value", DocumentProcessor.ReadText(rehydrated).Contains("Acme Holdings signed."));
            var badRestore = P("g2/bad-restore.docx");
            Throws<InvalidDataException>("G2: rehydrating a damaged document throws", () => DocumentProcessor.Rehydrate(damaged, badRestore, acme));
            Check("G2: a failed rehydration leaves no output file", !File.Exists(badRestore));

            // Startup sweep.
            var sweep = Fresh("g2-sweep");
            var oldTemp = Path.Combine(sweep, "report.docx.0123456789abcdef.dstmp");
            var oldApp = Path.Combine(sweep, "Desensitizer-0123456789abcdef0123456789abcdef.docx");
            var freshTemp = Path.Combine(sweep, "fresh.docx.fedcba.dstmp");
            var unrelated = Path.Combine(sweep, "Quarterly report.docx");
            var similar = Path.Combine(sweep, "Desensitizer-notes.docx");
            foreach (var f in new[] { oldTemp, oldApp, freshTemp, unrelated, similar }) File.WriteAllText(f, "x");
            foreach (var f in new[] { oldTemp, oldApp, unrelated, similar }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-3));
            var removed = DocumentProcessor.SweepStaleTempFiles(sweep);
            Check("G2 sweep: stale temporary files are removed", !File.Exists(oldTemp) && !File.Exists(oldApp) && removed == 2, removed.ToString());
            Check("G2 sweep negative: recent temporary files and unrelated documents are kept", File.Exists(freshTemp) && File.Exists(unrelated) && File.Exists(similar));
            Check("G2 sweep: a zero age removes recent files too, and a missing folder is harmless", DocumentProcessor.SweepStaleTempFiles(sweep, TimeSpan.Zero) == 1 && DocumentProcessor.SweepStaleTempFiles(P("no-such-folder")) == 0);
        }

        // ------------------------------------------------------------ G3 dropped mappings fail closed
        {
            var folder = Fresh("g3");
            var source = P("g3/source.docx");
            Build(source, Para("The organization approves the Acme Holdings request."));
            var generic = Map("organization", "[ORGANIZATION_005]", "ORGANIZATION", false);
            var output = P("g3/output.docx");
            try
            {
                DocumentProcessor.Desensitize(source, output, new[] { generic, Map("Acme Holdings", "[ORGANIZATION_001]", "ORGANIZATION", false) });
                Check("G3: an approved mapping dropped by the policy blocks the export", false, "no exception");
            }
            catch (InvalidDataException ex)
            {
                Check("G3: an approved mapping dropped by the policy blocks the export", true);
                Check("G3: the message names the count and category, never the value", ex.Message.Contains("1 approved replacement") && ex.Message.Contains("ORGANIZATION: 1") && !ex.Message.Contains("organization approves") && !ex.Message.Contains("'organization'") && !ex.Message.Contains("Acme"), ex.Message);
                Check("G3: nothing was written", !File.Exists(output) && !Leftovers(folder, "source.docx"));
            }
            DocumentProcessor.Desensitize(source, output, new[] { generic, Map("Acme Holdings", "[ORGANIZATION_001]", "ORGANIZATION", false) }, null, null, true);
            var text = DocumentProcessor.ReadText(output);
            Check("G3: the opt-in parameter proceeds, applying the others and leaving the dropped one", text == "The organization approves the [ORGANIZATION_001] request.", text);
            // Negative checks.
            var absent = Map("Globex Corporation", "[ORGANIZATION_007]", "ORGANIZATION", false);
            var absentOut = P("g3/absent.docx");
            DocumentProcessor.Desensitize(source, absentOut, new[] { absent, Map("Acme Holdings", "[ORGANIZATION_001]", "ORGANIZATION", false) });
            Check("G3 negative: a saved mapping whose value is not in this document does not block", File.Exists(absentOut));
            var manualOut = P("g3/manual.docx");
            DocumentProcessor.Desensitize(source, manualOut, new[] { Map("organization", "[ORGANIZATION_005]") });
            Check("G3 negative: a manual mapping for a common word is applied, not dropped", DocumentProcessor.ReadText(manualOut).StartsWith("The [ORGANIZATION_005] approves"));
            var txt = P("g3/source.txt");
            File.WriteAllText(txt, "The organization approves.");
            Throws<InvalidDataException>("G3: text files fail closed as well", () => DocumentProcessor.Desensitize(txt, P("g3/out.txt"), new[] { generic }), m => m.Contains("ORGANIZATION: 1"));
            Check("G3: a blocked text export leaves no file", !File.Exists(P("g3/out.txt")));
        }

        // ------------------------------------------------------------ G4 output allow-list
        {
            var folder = Fresh("g4");
            var source = P("g4/source.docx");
            var extras = new[]
            {
                new Extra("word/printerSettings/printerSettings1.bin", Encoding.UTF8.GetBytes("PRINTER-QUEUE-ZEPHYRION \\\\server\\hq-printer"), null, "printerSettings", "printerSettings/printerSettings1.bin"),
                new Extra("docProps/thumbnail.jpeg", Encoding.UTF8.GetBytes("THUMBNAIL-ZEPHYRION"), null, "metadata/thumbnail", null, true),
                new Extra("customXml/item1.xml", Encoding.UTF8.GetBytes("<root>Zephyrion custom</root>"), null, "customXml", "../customXml/item1.xml"),
                new Extra("word/vendor/unknown.dat", Encoding.UTF8.GetBytes("unknown ZEPHYRION part"), "application/x-vendor", "vendorThing", "vendor/unknown.dat"),
                new Extra("word/fonts/font1.odttf", Encoding.UTF8.GetBytes("FONT-ZEPHYRION"), "application/vnd.openxmlformats-officedocument.obfuscatedFont", "font", "fonts/font1.odttf"),
                Xml("word/styles.xml", "<w:styles " + Ns + "><w:style w:type=\"paragraph\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style></w:styles>", Wml + "styles+xml", "styles", "styles.xml"),
                Xml("word/header1.xml", "<w:hdr " + Ns + ">" + Para("Header Acme Holdings") + "</w:hdr>", Wml + "header+xml", "header", "header1.xml"),
                Xml("word/footnotes.xml", "<w:footnotes " + Ns + "><w:footnote w:id=\"1\">" + Para("Note") + "</w:footnote></w:footnotes>", Wml + "footnotes+xml", "footnotes", "footnotes.xml"),
                Xml("word/theme/theme1.xml", "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Office\"/>", "application/vnd.openxmlformats-officedocument.theme+xml", "theme", "theme/theme1.xml"),
            };
            Build(source, Para("Acme Holdings body."), extras, "COMMENT-ZEPHYRION-ARCHIVE");
            var output = P("g4/output.docx");
            DocumentProcessor.Desensitize(source, output, acme);
            var names = Names(output);
            Check("G4: printerSettings, thumbnail, custom XML, fonts and unknown parts are dropped", !names.Any(n => n.Contains("printerSettings") || n.Contains("thumbnail") || n.Contains("customXml") || n.Contains("unknown") || n.Contains("fonts/")), string.Join(",", names));
            var raw = Encoding.Latin1.GetString(File.ReadAllBytes(output));
            Check("G4: the zip archive comment is not copied", !raw.Contains("COMMENT-ZEPHYRION") && !raw.Contains("ZEPHYRION", StringComparison.OrdinalIgnoreCase));
            Check("G4: every output entry is on the allow-list", names.All(PackageSafety.IsAllowedOutputPart), string.Join(",", names.Where(n => !PackageSafety.IsAllowedOutputPart(n))));
            using (var zip = ZipFile.OpenRead(output))
                Check("G4: entry timestamps are normalised", zip.Entries.All(e => e.LastWriteTime.DateTime == new DateTime(1980, 1, 1)), string.Join(",", zip.Entries.Select(e => e.LastWriteTime)));
            Check("G4 negative: styles, header, footnotes and theme are kept", new[] { "word/styles.xml", "word/header1.xml", "word/footnotes.xml", "word/theme/theme1.xml", "word/document.xml", "[Content_Types].xml" }.All(names.Contains) && ReadPart(output, "word/header1.xml").Contains("Header [ORGANIZATION_001]"));
            // No relationship or content type points at a dropped part, and the file still opens.
            string[] relParts = names.Where(n => n.EndsWith(".rels")).ToArray();
            var dangling = relParts.SelectMany(rp =>
            {
                var folderOf = rp == "_rels/.rels" ? "" : rp.Substring(0, rp.IndexOf("_rels/"));
                return XDocument.Parse(ReadPart(output, rp)).Descendants().Where(e => e.Name.LocalName == "Relationship")
                    .Select(e => (folderOf + e.Attribute("Target")!.Value).Replace("word/../", "")).Where(t => !names.Contains(t));
            }).ToList();
            Check("G4: no relationship points at a dropped part", dangling.Count == 0, string.Join(",", dangling));
            Check("G4: no content type override names a dropped part", XDocument.Parse(ReadPart(output, "[Content_Types].xml")).Descendants().Where(e => e.Name.LocalName == "Override").All(e => names.Contains(e.Attribute("PartName")!.Value.TrimStart('/'))));
            try { using var word = WordprocessingDocument.Open(output, false); Check("G4: the output still opens as a Word document", word.MainDocumentPart != null && word.MainDocumentPart.HeaderParts.Any()); }
            catch (Exception ex) { Check("G4: the output still opens as a Word document", false, ex.Message); }
            // The audit refuses any part that is not allowed.
            Check("G4 allow-list rules", PackageSafety.IsAllowedOutputPart("word/footer12.xml") && !PackageSafety.IsAllowedOutputPart("word/glossary/document.xml") && !PackageSafety.IsAllowedOutputPart("word/charts/chart1.xml") && !PackageSafety.IsAllowedOutputPart("docProps/app.xml") && !PackageSafety.IsAllowedOutputPart("word/media/image1.png") && !PackageSafety.IsAllowedOutputPart("word/comments.xml") && !PackageSafety.IsAllowedOutputPart("word/footerX.xml"));
        }

        // ------------------------------------------------------------ G5 zip safety and exception content
        {
            var folder = Fresh("g5");
            // Many entries.
            var many = P("g5/many.docx");
            Build(many, Para("Text"));
            using (var zip = ZipFile.Open(many, ZipArchiveMode.Update))
                for (var i = 0; i <= PackageSafety.MaxEntries; i++) zip.CreateEntry("word/extra/p" + i + ".xml");
            Throws<InvalidDataException>("G5: too many entries is refused with a readable message", () => DocumentProcessor.ReadText(many), m => m.Contains("too many internal parts") && m.Contains("cannot be processed safely"));
            // Compression ratio.
            var ratio = P("g5/ratio.docx");
            Build(ratio, Para("Text"));
            using (var zip = ZipFile.Open(ratio, ZipArchiveMode.Update))
            {
                var e = zip.CreateEntry("word/bomb.xml", CompressionLevel.SmallestSize);
                using var s = e.Open();
                var zeros = new byte[1024 * 1024];
                for (var i = 0; i < 8; i++) s.Write(zeros, 0, zeros.Length);
            }
            Throws<InvalidDataException>("G5: an extreme compression ratio is refused", () => DocumentProcessor.ReadText(ratio), m => m.Contains("compressed far more than normal"));
            // Entry size.
            var big = P("g5/big.docx");
            Build(big, Para("Text"));
            using (var zip = ZipFile.Open(big, ZipArchiveMode.Update))
            {
                var e = zip.CreateEntry("word/media/large.bin", CompressionLevel.NoCompression);
                using var s = e.Open();
                var chunk = new byte[1024 * 1024];
                for (var i = 0; i < 129; i++) s.Write(chunk, 0, chunk.Length);
            }
            Throws<InvalidDataException>("G5: an entry above the size limit is refused", () => DocumentProcessor.ReadText(big), m => m.Contains("larger than 128 MB"));
            File.Delete(big);
            // Duplicate names.
            var dup = P("g5/dup.docx");
            Build(dup, Para("Visible text"));
            using (var zip = ZipFile.Open(dup, ZipArchiveMode.Update))
            {
                var e = zip.CreateEntry("word/document.xml");
                using var w = new StreamWriter(e.Open());
                w.Write("<?xml version=\"1.0\"?><w:document " + Ns + "><w:body>" + Para("Hidden second body") + "</w:body></w:document>");
            }
            Throws<InvalidDataException>("G5: duplicate entry names are refused", () => DocumentProcessor.ReadText(dup), m => m.Contains("same name"));
            Throws<InvalidDataException>("G5: a duplicate-name document cannot be exported", () => DocumentProcessor.Desensitize(dup, P("g5/dup-out.docx"), acme));
            var upper = P("g5/dup-case.docx");
            Build(upper, Para("Visible text"));
            using (var zip = ZipFile.Open(upper, ZipArchiveMode.Update)) zip.CreateEntry("WORD/DOCUMENT.XML");
            Throws<InvalidDataException>("G5: names that differ only in case count as duplicates", () => DocumentProcessor.ReadText(upper), m => m.Contains("same name"));
            var notZip = P("g5/not-a-zip.docx");
            File.WriteAllText(notZip, "this is plain text, not a zip");
            Throws<InvalidDataException>("G5: a file that is not a package gives a plain-language error", () => DocumentProcessor.ReadText(notZip), m => m.Contains("not a valid Word document"));
            Check("G5 negative: a normal document is within every limit", DocumentProcessor.ReadText(P("g1/source.docx")).Contains("Intro for Acme Holdings."));

            // No document text in exceptions.
            var source = P("g5/source.docx");
            Build(source, Para("Alpha Acme Holdings omega.") + Para("Second paragraph."));
            var output = P("g5/output.docx");
            DocumentProcessor.Desensitize(source, output, acme);
            var tampered = P("g5/tampered.docx");
            File.Copy(output, tampered, true);
            using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("word/document.xml")!;
                string xml; using (var r = new StreamReader(entry.Open())) xml = r.ReadToEnd();
                entry.Delete();
                using var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
                w.Write(xml.Replace("Second paragraph.", "Second CHANGED paragraph."));
            }
            try { PreservationAudit.AssertPreserved(source, tampered, acme); Check("G5: a changed paragraph is reported without its text", false, "no exception"); }
            catch (InvalidDataException ex)
            {
                var keys = ex.Data.Keys.Cast<object>().Select(k => k.ToString()).OrderBy(k => k).ToArray();
                var everything = ex + string.Join("|", ex.Data.Values.Cast<object>().Select(v => v.ToString()));
                Check("G5: a changed paragraph is reported by part and index only", keys.SequenceEqual(new[] { "paragraph", "part" }) && ex.Message.Contains("word/document.xml") && ex.Message.Contains("paragraph 2"), string.Join(",", keys) + " " + ex.Message);
                Check("G5: the exception carries no source, expected or actual text", !everything.Contains("Second") && !everything.Contains("Acme") && !everything.Contains("CHANGED") && !everything.Contains("omega"), everything);
            }
            // The audit message never has the original value (also covered for attribute residue above).
            var leak = P("g5/leak.docx");
            Build(leak, "<w:p><w:pPr><w:pStyle w:val=\"Zephyrion Corporation\"/></w:pPr><w:r><w:t>x</w:t></w:r></w:p>");
            Throws<InvalidDataException>("G5: the export block message names the token and category, not the value", () => DocumentProcessor.Desensitize(leak, P("g5/leak-out.docx"), new[] { Map("Zephyrion Corporation", "[ORGANIZATION_009]") }), m => m.Contains("ORGANIZATION, replacement [ORGANIZATION_009]") && !m.Contains("Zephyrion"));
            var fingerprint = PackageSafety.Fingerprint("Alpha Acme Holdings");
            Check("G5: a fingerprint is short, stable in one process and does not contain the value", fingerprint.Length == 8 && fingerprint == PackageSafety.Fingerprint("Alpha Acme Holdings") && fingerprint != PackageSafety.Fingerprint("Alpha Acme Holding") && !fingerprint.Contains("Acme"));
        }

        return (total, failures);
    }
}

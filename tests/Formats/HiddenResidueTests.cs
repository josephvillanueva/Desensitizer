using System.Text;
using Desensitizer;

// Hidden package channels must not bypass the identifying-value audit.
// All examples are fictional. Blocked copies leave the original untouched.
static class HiddenResidueTests
{
    private const string Person = "Elara Voss-Winter";

    public static void Run(CheckFn check, string work)
    {
        var index = 0;
        void Fixture(string label, string extension, List<(string Name, string Content)> parts, bool blocked, bool manual = false)
        {
            var stem = Path.Combine(work, "hidden-" + ++index);
            var source = stem + extension;
            var output = stem + "-out" + extension;
            var before = Kit.Zip(parts.ToArray());
            File.WriteAllBytes(source, before);
            var error = Kit.Message(() => DocumentProcessor.Desensitize(source, output, new[] { Kit.Map(Person, "PERSON", 80, manual) }));
            check(label + ": expected release decision", blocked ? error.StartsWith("Export blocked") && !File.Exists(output) : error.Length == 0 && File.Exists(output), error);
            check(label + ": source unchanged and diagnostics scrubbed", File.ReadAllBytes(source).SequenceEqual(before) && !error.Contains(Person));
            if (!blocked && File.Exists(output))
                check(label + ": hidden metadata removed", Kit.Read(output).Values.All(xml => !xml.Contains(Person) && !xml.Contains("<?note") && !xml.Contains("<!--")));
        }

        List<(string Name, string Content)> Change(List<(string Name, string Content)> parts, string name, Func<string, string> change) =>
            parts.Select(p => (p.Name, p.Name == name ? change(p.Content) : p.Content)).ToList();

        Fixture("XLSX comment", ".xlsx", Change(XlsxTests.Parts(), "xl/worksheets/sheet1.xml", x => x.Replace("</worksheet>", "<!-- " + Person + " --></worksheet>")), false);
        Fixture("XLSX processing instruction", ".xlsx", Change(XlsxTests.Parts(), "xl/workbook.xml", x => x.Replace("</workbook>", "<?note " + Person + "?></workbook>")), false);
        foreach (var value in new[] { Person, Person.Replace(' ', '_'), Convert.ToBase64String(Encoding.UTF8.GetBytes(Person)) })
            Fixture("XLSX hidden protection value", ".xlsx", Change(XlsxTests.Parts(), "xl/workbook.xml", x => x.Replace("<workbookPr/>", "<workbookPr/><workbookProtection workbookAlgorithmName='" + value + "'/>")), true);
        Fixture("XLSX structural UID", ".xlsx", Change(XlsxTests.Parts(), "xl/worksheets/sheet1.xml", x => x.Replace("<row r=\"1\">", "<row r=\"1\" uid='" + Person + "'>")), true, true);
        Fixture("XLSX encoded formula reconstruction", ".xlsx", Change(XlsxTests.Parts(), "xl/worksheets/sheet1.xml", x => x.Replace("</sheetData>", "<row r='20'><c r='A20' t='str'><f>CHAR(69)&amp;UNICHAR(108)&amp;\"ara Voss-Winter\"</f><v>x</v></c></row></sheetData>")), true);
        Fixture("XLSX formatting splits identifier", ".xlsx", Change(XlsxTests.Parts(), "xl/worksheets/sheet1.xml", x => x.Replace("</worksheet>", "<headerFooter><evenHeader>&amp;LElara &amp;\"Arial\"Voss-Winter</evenHeader></headerFooter></worksheet>")), true);
        Fixture("XLSX harmless formula retained", ".xlsx", Change(XlsxTests.Parts(), "xl/worksheets/sheet1.xml", x => x.Replace("</sheetData>", "<row r='20'><c r='A20' t='str'><f>CHAR(65)&amp;\"business rule\"</f><v>x</v></c></row></sheetData>")), false);

        Fixture("PPTX comment", ".pptx", Change(PptxTests.Parts(), "ppt/slides/slide1.xml", x => x.Replace("</p:sld>", "<!-- " + Person + " --></p:sld>")), false);
        Fixture("PPTX processing instruction", ".pptx", Change(PptxTests.Parts(), "ppt/slides/slide1.xml", x => x.Replace("</p:sld>", "<?note " + Person + "?></p:sld>")), false);
        foreach (var value in new[] { Person, Person.Replace(' ', '_'), Convert.ToBase64String(Encoding.UTF8.GetBytes(Person)) })
            Fixture("PPTX hidden font value", ".pptx", Change(PptxTests.Parts(), "ppt/theme/theme1.xml", x => x.Replace("<a:themeElements/>", "<a:themeElements><a:fontScheme name='Default'><a:majorFont><a:latin typeface='" + value + "'/></a:majorFont></a:fontScheme></a:themeElements>")), true);

        foreach (var extension in new[] { ".xlsx", ".pptx" })
        {
            var stem = Path.Combine(work, "standalone-hidden-" + ++index);
            var source = stem + extension;
            var clean = stem + "-clean" + extension;
            File.WriteAllBytes(source, Kit.Zip((extension == ".xlsx" ? XlsxTests.Parts() : PptxTests.Parts()).ToArray()));
            var mappings = new[] { Kit.Map(Person, "PERSON", 80, false) };
            DocumentProcessor.Desensitize(source, clean, mappings);
            var partName = extension == ".xlsx" ? "xl/workbook.xml" : "ppt/slides/slide1.xml";
            foreach (var annotation in new[] { "<!-- " + Person + " -->", "<?note " + Person + "?>" })
            {
                var tampered = stem + "-tampered-" + ++index + extension;
                File.WriteAllBytes(tampered, Kit.Zip(Kit.Read(clean).Select(p => (p.Key, p.Key == partName ? p.Value + annotation : p.Value)).ToArray()));
                var error = Kit.Message(() => DocumentFormats.Require(source).Audit(source, tampered, mappings));
                check(extension + " standalone audit rejects XML annotations", error.StartsWith("Export blocked") && !error.Contains(Person));
            }
            var hidden = stem + "-attribute" + extension;
            File.WriteAllBytes(hidden, Kit.Zip(Kit.Read(clean).Select(p => (p.Key, p.Key == partName
                ? p.Value.Replace(extension == ".xlsx" ? "<workbookPr" : "<p:sld ", extension == ".xlsx" ? "<workbookPr codeName='" + Person.Replace(' ', '_') + "' " : "<p:sld hiddenOwner='" + Person.Replace(' ', '_') + "' ") : p.Value)).ToArray()));
            var hiddenError = Kit.Message(() => DocumentFormats.Require(source).Audit(source, hidden, mappings));
            check(extension + " standalone audit includes hidden-only requested mapping", hiddenError.StartsWith("Export blocked") && !hiddenError.Contains(Person));
        }
    }
}

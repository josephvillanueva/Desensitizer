using System.Text;
using Desensitizer;

// .md and .csv. Fictional values only.
static class TextFormatTests
{
    public static void Run(CheckFn check, string work)
    {
        Markdown(check, Kit.Dir(work, "md"));
        Csv(check, Kit.Dir(work, "csv"));
    }

    private static readonly Mapping[] People =
    {
        Kit.Map("Mira Quillon", "PERSON", 1),
        Kit.Map("Zorvex Holdings", "ORGANIZATION", 1),
        Kit.Map("mira.quillon@zorvex.example", "EMAIL", 1),
    };

    private static void Markdown(CheckFn check, string dir)
    {
        var source = Path.Combine(dir, "notes.md");
        var text = "# Kickoff with Zorvex Holdings\r\n\r\n- Owner: Mira Quillon (mira.quillon@zorvex.example)\r\n- Rule: orders over 500 units need approval.\r\n\r\n```\r\nZorvex Holdings stays in code blocks too\r\n```\r\n";
        File.WriteAllText(source, text, new UTF8Encoding(false));
        var format = DocumentFormats.Require(source);
        check("Markdown text is read as is", format.ReadText(source) == text);
        check("Markdown is routed through DocumentProcessor.ReadText", DocumentProcessor.ReadText(source) == text);
        check("Markdown has no table context, markup or hidden channels", format.ReadTableContext(source, text) == null && !format.InspectReviewMarkup(source).HasReviewMarkup && format.ReadHiddenChannels(source).Count == 0);

        var output = Path.Combine(dir, "notes-protected.md");
        DocumentProcessor.Desensitize(source, output, People);
        var protectedText = File.ReadAllText(output);
        check("Markdown keeps its extension and line endings", Path.GetExtension(output) == ".md" && protectedText.Contains("\r\n") && protectedText.Count(c => c == '\r') == text.Count(c => c == '\r'));
        check("Markdown replaces values everywhere including code blocks", !protectedText.Contains("Zorvex") && !protectedText.Contains("Quillon") && protectedText.Contains("# Kickoff with [ORGANIZATION_001]") && protectedText.Contains("[EMAIL_001]"));
        check("Markdown keeps business rules", protectedText.Contains("orders over 500 units need approval"));
        check("Markdown leaves no temporary file", Kit.Leftovers(dir).Length == 0);
        var restored = Path.Combine(dir, "notes-restored.md");
        var unknown = DocumentProcessor.Rehydrate(output, restored, People);
        check("Markdown rehydrate round trips exactly", File.ReadAllText(restored) == text && unknown == 0);
        format.Audit(source, output, People);
        check("Markdown audit passes on a good copy", true);

        // BOM is kept.
        var bom = Path.Combine(dir, "bom.md");
        File.WriteAllText(bom, "Mira Quillon wrote this.", new UTF8Encoding(true));
        var bomOut = Path.Combine(dir, "bom-out.md");
        DocumentProcessor.Desensitize(bom, bomOut, People);
        var bomBytes = File.ReadAllBytes(bomOut);
        check("Markdown keeps a UTF-8 byte order mark", bomBytes[0] == 0xEF && bomBytes[1] == 0xBB && bomBytes[2] == 0xBF && Encoding.UTF8.GetString(bomBytes, 3, bomBytes.Length - 3) == "[PERSON_001] wrote this.");

        // Negative: a value split by a line break would ship under line by line replacement. The export must stop.
        var split = Path.Combine(dir, "split.md");
        File.WriteAllText(split, "Prepared by Mira\nQuillon for the team.\n");
        var splitOut = Path.Combine(dir, "split-out.md");
        var message = Kit.Message(() => DocumentProcessor.Desensitize(split, splitOut, People));
        check("Markdown value split across lines blocks the export", message.StartsWith("Export blocked") && !message.Contains("Quillon"), message);
        check("Blocked markdown export leaves no output and no temp file", !File.Exists(splitOut) && Kit.Leftovers(dir).Length == 0);

        // Negative: output over the source is refused.
        check("Markdown export never overwrites the source", Kit.Throws(() => DocumentProcessor.Desensitize(source, source, People)) && File.ReadAllText(source) == text);
    }

    private static void Csv(CheckFn check, string dir)
    {
        var source = Path.Combine(dir, "orders.csv");
        var text = "Customer,Contact,Notes,Qty\r\n" +
                   "Zorvex Holdings,Mira Quillon,\"Ships to Zorvex Holdings, dock 4\",12\r\n" +
                   "\"Plain, Co\",,\"Line one\r\nMira Quillon says \"\"hi\"\"\",3\r\n" +
                   ",,,\r\n" +
                   "Last row,mira.quillon@zorvex.example,ok,";
        File.WriteAllText(source, text, new UTF8Encoding(false));
        var format = DocumentFormats.Require(source);
        var read = format.ReadText(source);
        check("CSV text has one line per row with tab separated cells", read.Split('\n').Length == 5 && read.Split('\n')[0] == "Customer\tContact\tNotes\tQty");
        check("CSV quoted cell with comma and line break is one cell", read.Contains("Ships to Zorvex Holdings, dock 4") && read.Contains("Line one Mira Quillon says \"hi\""));
        check("CSV table context is not provided", format.ReadTableContext(source, read) == null);

        var output = Path.Combine(dir, "orders-protected.csv");
        DocumentProcessor.Desensitize(source, output, People);
        var protectedText = File.ReadAllText(output);
        var expected = "Customer,Contact,Notes,Qty\r\n" +
                       "[ORGANIZATION_001],[PERSON_001],\"Ships to [ORGANIZATION_001], dock 4\",12\r\n" +
                       "\"Plain, Co\",,\"Line one\r\n[PERSON_001] says \"\"hi\"\"\",3\r\n" +
                       ",,,\r\n" +
                       "Last row,[EMAIL_001],ok,";
        check("CSV protected copy differs only inside cells", protectedText == expected, protectedText);
        var before = CsvDocument.Parse(text);
        var after = CsvDocument.Parse(protectedText);
        check("CSV row and column counts are unchanged", before.Records.Count == after.Records.Count && before.Records.Select(r => r.Fields.Count).SequenceEqual(after.Records.Select(r => r.Fields.Count)));
        check("CSV quoting and line endings are unchanged", before.Records.SelectMany(r => r.Fields).Select(f => f.Quoted).SequenceEqual(after.Records.SelectMany(r => r.Fields).Select(f => f.Quoted)) && before.Records.Select(r => r.Terminator).SequenceEqual(after.Records.Select(r => r.Terminator)));
        check("CSV leaves no temporary file", Kit.Leftovers(dir).Length == 0);
        var restored = Path.Combine(dir, "orders-restored.csv");
        var unknown = DocumentProcessor.Rehydrate(output, restored, People);
        check("CSV rehydrate round trips exactly", File.ReadAllText(restored) == text && unknown == 0);
        format.Audit(source, output, People);
        check("CSV audit passes on a good copy", true);

        // Restored text that now needs quotes gets them; counts stay.
        var tokenOnly = Path.Combine(dir, "token.csv");
        File.WriteAllText(tokenOnly, "a,[ORGANIZATION_001],c\n");
        var tokenOut = Path.Combine(dir, "token-out.csv");
        var comma = new[] { Kit.Map("Zorvex, Inc", "ORGANIZATION", 1) };
        DocumentProcessor.Rehydrate(tokenOnly, tokenOut, comma);
        check("CSV restored value with a comma is quoted", File.ReadAllText(tokenOut) == "a,\"Zorvex, Inc\",c\n" && CsvDocument.Parse(File.ReadAllText(tokenOut)).Records[0].Fields.Count == 3);

        // Other delimiters, kept exactly.
        foreach (var (delimiter, name) in new[] { (';', "semicolon"), ('\t', "tab"), ('|', "pipe") })
        {
            var path = Path.Combine(dir, name + ".csv");
            var body = $"Name{delimiter}Org{delimiter}Qty\n\"Mira Quillon\"{delimiter}Zorvex Holdings{delimiter}4\nx{delimiter}y{delimiter}5\n";
            File.WriteAllText(path, body);
            var outPath = Path.Combine(dir, name + "-out.csv");
            DocumentProcessor.Desensitize(path, outPath, People);
            check("CSV with " + name + " delimiter keeps delimiters and quotes", File.ReadAllText(outPath) == $"Name{delimiter}Org{delimiter}Qty\n\"[PERSON_001]\"{delimiter}[ORGANIZATION_001]{delimiter}4\nx{delimiter}y{delimiter}5\n");
        }

        // Encodings.
        var bom = Path.Combine(dir, "bom.csv");
        File.WriteAllText(bom, "Name,Org\nMira Quillon,Zorvex Holdings\n", new UTF8Encoding(true));
        var bomOut = Path.Combine(dir, "bom-out.csv");
        DocumentProcessor.Desensitize(bom, bomOut, People);
        var bomBytes = File.ReadAllBytes(bomOut);
        check("CSV keeps a UTF-8 byte order mark", bomBytes[0] == 0xEF && Encoding.UTF8.GetString(bomBytes, 3, bomBytes.Length - 3) == "Name,Org\n[PERSON_001],[ORGANIZATION_001]\n");
        var ansi = Path.Combine(dir, "ansi.csv");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(ansi, Encoding.GetEncoding(1252).GetBytes("Name,City\nMira Quillon,Zürich\n"));
        var ansiOut = Path.Combine(dir, "ansi-out.csv");
        DocumentProcessor.Desensitize(ansi, ansiOut, People);
        check("CSV in Windows-1252 keeps its accents and encoding", Encoding.GetEncoding(1252).GetString(File.ReadAllBytes(ansiOut)) == "Name,City\n[PERSON_001],Zürich\n");
        var utf16 = Path.Combine(dir, "utf16.csv");
        File.WriteAllText(utf16, "Name,Org\nMira Quillon,Zorvex Holdings\n", Encoding.Unicode);
        var utf16Out = Path.Combine(dir, "utf16-out.csv");
        DocumentProcessor.Desensitize(utf16, utf16Out, People);
        check("CSV in UTF-16 keeps its encoding", File.ReadAllText(utf16Out, Encoding.Unicode).Contains("[PERSON_001]") && File.ReadAllBytes(utf16Out)[0] == 0xFF);

        // Negative: malformed quoting fails closed on read and on export, with no output.
        var malformed = new Dictionary<string, string>
        {
            ["unterminated"] = "a,b\n\"never closed,c\n",
            ["stray"] = "a,5\" pipe,c\n",
            ["after"] = "a,\"b\"x,c\n",
        };
        foreach (var (name, body) in malformed)
        {
            var path = Path.Combine(dir, "bad-" + name + ".csv");
            File.WriteAllText(path, body);
            var outPath = Path.Combine(dir, "bad-" + name + "-out.csv");
            check("Malformed CSV (" + name + ") is refused on read", Kit.Throws(() => DocumentProcessor.ReadText(path), "CSV"));
            check("Malformed CSV (" + name + ") is refused on export with no output", Kit.Throws(() => DocumentProcessor.Desensitize(path, outPath, People), "CSV") && !File.Exists(outPath) && Kit.Leftovers(dir).Length == 0);
        }
        check("Malformed message does not quote the file content", !Kit.Message(() => DocumentProcessor.ReadText(Path.Combine(dir, "bad-after.csv"))).Contains("\"b\"x"));

        // Negative: a value that only exists spread over cells is not replaced, so the export must stop.
        var spread = Path.Combine(dir, "spread.csv");
        File.WriteAllText(spread, "Name,Org\nZorvex,Holdings\n");
        var spreadMap = new[] { Kit.Map("Zorvex,Holdings", "ORGANIZATION", 1) };
        var spreadOut = Path.Combine(dir, "spread-out.csv");
        var spreadMessage = Kit.Message(() => DocumentProcessor.Desensitize(spread, spreadOut, spreadMap));
        check("CSV value spread across cells blocks the export", spreadMessage.StartsWith("Export blocked") && !File.Exists(spreadOut), spreadMessage);
        check("CSV export never overwrites the source", Kit.Throws(() => DocumentProcessor.Desensitize(source, source, People)) && File.ReadAllText(source) == text);

        // Empty file and header-only file.
        var empty = Path.Combine(dir, "empty.csv");
        File.WriteAllText(empty, "");
        check("Empty CSV reads as empty text", DocumentProcessor.ReadText(empty) == "");
        var emptyOut = Path.Combine(dir, "empty-out.csv");
        DocumentProcessor.Desensitize(empty, emptyOut, People);
        check("Empty CSV exports as an empty file", File.Exists(emptyOut) && new FileInfo(emptyOut).Length == 0);
    }
}

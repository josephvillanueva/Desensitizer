using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Desensitizer;

static class RegistryTests
{
    public static void Run(CheckFn check, string work)
    {
        var dir = Kit.Dir(work, "registry");

        // The table and the registry agree.
        foreach (var entry in SupportedFormats.Supported)
            check("Registry has a format for " + entry.Extension, DocumentFormats.Find(entry.Extension) != null);
        check("Registry lookup ignores case and takes a path", DocumentFormats.Find(@"C:\x\Report.DOCX")?.Extension == ".docx");
        check("Registry has no format for .pdf", DocumentFormats.Find("a.pdf") == null);
        check("Every registered format is in the supported table", DocumentFormats.All.All(f => SupportedFormats.IsSupported("x" + f.Extension)));

        // Word and text delegate to the existing code and give the same answers.
        var docx = Path.Combine(dir, "plain.docx");
        using (var word = WordprocessingDocument.Create(docx, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Prepared for Zorvex Holdings by Mira Quillon."))),
                new Paragraph(new Run(new Text("Second paragraph.")))));
            main.Document.Save();
        }
        var format = DocumentFormats.Require(docx);
        check("Word format is built in", format.IsBuiltIn && format is WordFormat);
        check("Word format ReadText equals DocumentProcessor.ReadText", format.ReadText(docx) == DocumentProcessor.ReadText(docx));
        check("Word format reports no markup for a plain file", !format.InspectReviewMarkup(docx).HasReviewMarkup);
        var map = new[] { Kit.Map("Zorvex Holdings", "ORGANIZATION", 1) };
        var wordOut = Path.Combine(dir, "plain-protected.docx");
        format.Transform(docx, wordOut, map, true);
        check("Word transform through the registry removes the value", !DocumentProcessor.ReadText(wordOut).Contains("Zorvex") && DocumentProcessor.ReadText(wordOut).Contains("[ORGANIZATION_001]"));
        var wordBack = Path.Combine(dir, "plain-restored.docx");
        format.Transform(wordOut, wordBack, map, false);
        check("Word rehydrate through the registry round trips", DocumentProcessor.ReadText(wordBack) == DocumentProcessor.ReadText(docx));

        var txt = Path.Combine(dir, "note.txt");
        File.WriteAllText(txt, "Call Mira Quillon.\nThen stop.");
        var textFormat = DocumentFormats.Require(txt);
        check("Text format is built in", textFormat.IsBuiltIn && textFormat is TextFormat);
        check("Text format ReadText equals DocumentProcessor.ReadText", textFormat.ReadText(txt) == DocumentProcessor.ReadText(txt));
        check("Text format has no table context", textFormat.ReadTableContext(txt, "x") == null);
        var txtOut = Path.Combine(dir, "note-protected.txt");
        textFormat.Transform(txt, txtOut, new[] { Kit.Map("Mira Quillon", "PERSON", 1) }, true);
        check("Text transform through the registry replaces the value", File.ReadAllText(txtOut) == "Call [PERSON_001].\nThen stop.");
        textFormat.Audit(txt, txtOut, new[] { Kit.Map("Mira Quillon", "PERSON", 1) });
        check("Text audit through the registry passes on a good copy", true);

        // Unsupported files.
        var pdf = Path.Combine(dir, "a.pdf");
        File.WriteAllText(pdf, "%PDF-1.4");
        check("ReadText refuses a pdf with advice", Kit.Message(() => DocumentProcessor.ReadText(pdf)).Contains("Do not use Save as PDF"));
        check("Desensitize refuses a pdf", Kit.Throws(() => DocumentProcessor.Desensitize(pdf, Path.Combine(dir, "x.pdf"), map)));
        check("No output after a refused file", !File.Exists(Path.Combine(dir, "x.pdf")));

        // The supported table drives the dialogs.
        check("Open filter lists every supported type", new[] { "*.docx", "*.xlsx", "*.pptx", "*.csv", "*.md", "*.txt" }.All(SupportedFormats.OpenFilter().Contains));
        check("Open filter starts with the combined entry", SupportedFormats.OpenFilter().StartsWith("Supported documents|*.docx;"));
        check("Save filter follows the source type", SupportedFormats.SaveFilter(SupportedFormats.OutputExtension("a.XLSX")) == "Excel workbook|*.xlsx");
        check("Save filter falls back to text", SupportedFormats.OutputExtension("a.unknown") == ".txt");
        foreach (var ext in new[] { ".doc", ".pdf", ".xls", ".ppt", ".rtf", ".odt", ".ods", ".odp", ".xlsm", ".docm", ".pptm", ".xlsb", ".pages", ".numbers", ".key" })
        {
            var message = SupportedFormats.RejectionMessage("file" + ext);
            check("Rejection message for " + ext + " is friendly and has no em dash", message.Length > 20 && !message.Contains('\u2014') && !message.Contains('\u2013') && (message.Contains(".docx") || message.Contains(".xlsx") || message.Contains(".pptx")));
            check(ext + " is not supported", !SupportedFormats.IsSupported("file" + ext) && SupportedFormats.IsKnownUnsupported("file" + ext));
        }
        check("PDF advice says to save the original as docx, xlsx or pptx", SupportedFormats.RejectionMessage("a.pdf").Contains(".docx, .xlsx or .pptx"));
        check("Unknown type message lists the supported types", SupportedFormats.RejectionMessage("a.exe").Contains("Word (.docx)") && SupportedFormats.RejectionMessage("noext").Contains("no file type"));
        check("Summary for one skipped pdf is the pdf advice", SupportedFormats.SkippedSummary(new[] { "a.pdf" }).Contains("PDF files are not supported"));
        check("Summary for mixed skipped files is general", SupportedFormats.SkippedSummary(new[] { "a.pdf", "b.doc" }).Contains("2 items were skipped"));
        check("Help list of formats has no em dash", !SupportedFormats.ListForHumans().Contains('\u2014') && SupportedFormats.ListOfExtensions() == ".docx, .xlsx, .pptx, .csv, .md or .txt");
        check("Sweep also removes stale protected copies of new types", SweepRemovesNewTypes(dir));
    }

    private static bool SweepRemovesNewTypes(string dir)
    {
        var sweep = Kit.Dir(dir, "sweep");
        var names = new[] { "xlsx", "pptx", "csv", "md", "docx", "txt" }.Select(e => "Desensitizer-" + Guid.NewGuid().ToString("N") + "." + e).ToArray();
        foreach (var n in names) { var p = Path.Combine(sweep, n); File.WriteAllText(p, "x"); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddHours(-2)); }
        var removed = DocumentProcessor.SweepStaleTempFiles(sweep);
        return removed == names.Length && Directory.GetFiles(sweep).Length == 0;
    }
}

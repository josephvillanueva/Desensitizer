using System.IO.Compression;
using System.Text;
using Desensitizer;

// UI-1 (every approved mapping applies to every document) and UI-2 (the protected copy keeps the source type).
static class ExportPlanTests
{
    private static Mapping Map(string value, string token, bool approved, bool manual, params string[] files) => new()
    {
        OriginalValue = value, NormalizedValue = Project.Normalize(value), Category = "ORGANIZATION", Token = token,
        Approved = approved, IsManual = manual, SourceFileIds = files.ToList(), IdentityFileIds = files.ToList(),
    };

    // A small workbook with one inline-string cell, built from raw XML.
    private static void WriteWorkbook(string path, string cellText)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        void Part(string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        const string decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
        Part("[Content_Types].xml", decl + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        Part("_rels/.rels", decl + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Part("xl/workbook.xml", decl + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sales\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels", decl + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
        Part("xl/worksheets/sheet1.xml", decl + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>" + cellText + "</t></is></c></row></sheetData></worksheet>");
    }

    public static void Run(CheckFn Check, string work)
    {
        var folder = Path.Combine(work, "export-plan");
        Directory.CreateDirectory(folder);

        // ---- UI-1: mapping scope --------------------------------------------------------------------------------
        var manualFromA = Map("Zorvex Holdings", "[ORGANIZATION_001]", true, true, "fileA");
        var autoFromA = Map("Quillon Bank", "[ORGANIZATION_002]", true, false, "fileA");
        var kept = Map("Orion Portal", "[ORGANIZATION_003]", false, false, "fileA"); kept.Ignored = true;
        var review = Map("Maple Group", "[ORGANIZATION_004]", false, false, "fileA");
        var context = Map("Vantor Ltd", "[ORGANIZATION_005]", true, false, "fileA"); context.RequiresContextReview = true;
        var all = new List<Mapping> { manualFromA, autoFromA, kept, review, context };
        var scope = UiExportScope.Approved(all);
        Check("scope: a manual and an automatic approved mapping apply whichever file is exported", scope.Contains(manualFromA) && scope.Contains(autoFromA) && scope.Count == 2);
        Check("scope: Keep as-is, Review and context-review mappings stay out", !scope.Contains(kept) && !scope.Contains(review) && !scope.Contains(context));
        Check("scope: the export button count equals the applied list", UiExportScope.ApprovedCount(all) == scope.Count);

        var docB = Path.Combine(folder, "docB.txt");
        File.WriteAllText(docB, "Contract with Zorvex Holdings and Quillon Bank. Orion Portal stays.");
        var outB = Path.Combine(folder, "outB.txt");
        DocumentProcessor.Desensitize(docB, outB, scope);
        var textB = File.ReadAllText(outB);
        Check("scope: a mapping added in document A is applied when document B is exported", textB.Contains("[ORGANIZATION_001]") && textB.Contains("[ORGANIZATION_002]") && !textB.Contains("Zorvex") && textB.Contains("Orion Portal"), textB);

        Check("scope: no warning when an approved value is present", UiExportScope.ZeroAppliedWarning(scope, "Zorvex   Holdings signed.") == "");
        Check("scope: a warning when no approved value is present in the document", UiExportScope.ZeroAppliedWarning(scope, "Nothing sensitive here.").Contains("None of the 2 approved"));
        Check("scope: a warning when nothing is approved at all", UiExportScope.ZeroAppliedWarning(new List<Mapping>(), "text").Contains("No approved replacements"));

        // ---- UI-2: output type -----------------------------------------------------------------------------------
        var source = Path.Combine(folder, "Report.xlsx");
        WriteWorkbook(source, "Zorvex Holdings pays Quillon Bank");
        string Forced(string chosen) => UiOutputPath.ForceExtension(Path.Combine(folder, chosen), source, out _);
        Check("output: a typed .txt is replaced by the source extension", Path.GetFileName(Forced("copy.txt")) == "copy.xlsx");
        Check("output: a name with extra dots gets the extension appended", Path.GetFileName(Forced("Q3.2026 report")) == "Q3.2026 report.xlsx");
        Check("output: a name with no extension gets one", Path.GetFileName(Forced("plain")) == "plain.xlsx");
        Check("output: the right extension is kept in any case", Path.GetFileName(Forced("Done.XLSX")) == "Done.XLSX");
        Check("output: a trailing dot or space is dropped before the extension", Path.GetFileName(Forced("name. ")) == "name.xlsx");
        Check("output: a different supported type is swapped, an unknown one appended", Path.GetFileName(Forced("a.docx")) == "a.xlsx" && Path.GetFileName(Forced("a.b c")) == "a.b c.xlsx");
        UiOutputPath.ForceExtension(Path.Combine(folder, "ok.xlsx"), source, out var unchanged);
        Check("output: a correct name is reported as not corrected", !unchanged);

        foreach (var typed in new[] { "typed.txt", "a.b c", "no-extension" })
        {
            var plan = UiOutputPath.Plan(Path.Combine(folder, typed), source, folder);
            var temp = plan.TempPath;
            Check("output: " + typed + " gives a .xlsx output and a .xlsx temp file", plan.OutputPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) && temp.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) && plan.Corrected);
            var map = Map("Zorvex Holdings", "[ORGANIZATION_001]", true, true, "other-file");
            DocumentProcessor.Desensitize(source, temp, new[] { map });
            var scanned = DocumentProcessor.ReadText(temp);
            Check("output: the final scan reads decoded cell text for a copy typed as " + typed, scanned.Contains("[ORGANIZATION_001]") && scanned.Contains("Quillon Bank") && !scanned.Contains("<c ") && !scanned.Contains("PK"), scanned);
            File.Delete(temp);
        }
        var misnamed = Path.Combine(folder, "bug.txt");
        File.Copy(source, misnamed, true);
        Check("output: reading the workbook by a typed .txt name would NOT decode cells (the bug this guards)", !DocumentProcessor.ReadText(misnamed).Contains("Quillon Bank") || DocumentProcessor.ReadText(misnamed).Contains("PK"));
        var guard = false;
        try { UiOutputPath.RequireSameFormat(source, misnamed); } catch (InvalidOperationException) { guard = true; }
        Check("output: a temp file with a different type than the source is refused", guard);
        Check("output: the temp file lives in the given temp folder", UiOutputPath.Plan(Path.Combine(folder, "x"), source, folder).TempPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }
}

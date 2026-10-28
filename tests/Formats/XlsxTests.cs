using System.IO.Compression;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Desensitizer;

// .xlsx. The workbook is built from raw XML in test code. Fictional values only.
static class XlsxTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    private static readonly Mapping[] Maps =
    {
        Kit.Map("Zorvex Holdings", "ORGANIZATION", 1),
        Kit.Map("Mira Quillon", "PERSON", 1),
        Kit.Map("5551234", "IDENTIFIER", 1),
        Kit.Map("mira.quillon@zorvex.example", "EMAIL", 1),
    };

    public static List<(string Name, string Content)> Parts(string sheetName = "Sales", string definedNameSheet = "Sales", string extraFormula = "")
    {
        var types = Decl + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"vml\" ContentType=\"application/vnd.openxmlformats-officedocument.vmlDrawing\"/><Default Extension=\"bin\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.printerSettings\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "<Override PartName=\"/xl/comments1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.comments+xml\"/>" +
            "<Override PartName=\"/xl/tables/table1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml\"/>" +
            "<Override PartName=\"/xl/drawings/drawing1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>" +
            "<Override PartName=\"/xl/charts/chart1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawingml.chart+xml\"/>" +
            "<Override PartName=\"/xl/pivotCache/pivotCacheDefinition1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.pivotCacheDefinition+xml\"/>" +
            "<Override PartName=\"/xl/externalLinks/externalLink1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.externalLink+xml\"/>" +
            "<Override PartName=\"/xl/calcChain.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.calcChain+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/></Types>";
        var rootRels = Decl + $"<Relationships xmlns=\"{PkgRel}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            $"<Relationship Id=\"rId3\" Type=\"{RelNs}/extended-properties\" Target=\"docProps/app.xml\"/></Relationships>";
        var core = Decl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\"><dc:title>Zorvex Holdings forecast</dc:title><dc:creator>Mira Quillon</dc:creator></cp:coreProperties>";
        var app = Decl + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Microsoft Excel</Application><Company>Zorvex Holdings</Company></Properties>";
        var workbook = Decl + $"<workbook xmlns=\"{Main}\" xmlns:r=\"{RelNs}\"><fileVersion appName=\"xl\"/><workbookPr/><bookViews><workbookView activeTab=\"0\"/></bookViews>" +
            $"<sheets><sheet name=\"{sheetName}\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Archive\" sheetId=\"2\" state=\"hidden\" r:id=\"rId2\"/></sheets>" +
            $"<definedNames><definedName name=\"Total_Range\">{definedNameSheet}!$B$2:$B$3</definedName></definedNames>" +
            "<externalReferences><externalReference r:id=\"rId7\"/></externalReferences><pivotCaches><pivotCache cacheId=\"1\" r:id=\"rId8\"/></pivotCaches><calcPr calcId=\"191029\"/></workbook>";
        var workbookRels = Decl + $"<Relationships xmlns=\"{PkgRel}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"{RelNs}/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
            $"<Relationship Id=\"rId3\" Type=\"{RelNs}/sharedStrings\" Target=\"sharedStrings.xml\"/><Relationship Id=\"rId4\" Type=\"{RelNs}/styles\" Target=\"styles.xml\"/>" +
            $"<Relationship Id=\"rId6\" Type=\"{RelNs}/calcChain\" Target=\"calcChain.xml\"/><Relationship Id=\"rId7\" Type=\"{RelNs}/externalLink\" Target=\"externalLinks/externalLink1.xml\"/>" +
            $"<Relationship Id=\"rId8\" Type=\"{RelNs}/pivotCacheDefinition\" Target=\"pivotCache/pivotCacheDefinition1.xml\"/></Relationships>";
        var sst = Decl + $"<sst xmlns=\"{Main}\" count=\"6\" uniqueCount=\"6\">" +
            "<si><t>Customer</t></si><si><t>Zorvex Holdings</t></si><si><r><t xml:space=\"preserve\">Mira </t></r><r><rPr><b/></rPr><t>Quillon</t></r></si>" +
            "<si><t>Stale Zorvex Holdings text nobody uses</t></si><si><t>Amount</t></si><si><t>Quarterly review</t></si></sst>";
        var styles = Decl + $"<styleSheet xmlns=\"{Main}\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
            "<cellStyles count=\"2\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/><cellStyle name=\"Zorvex Holdings Style\" xfId=\"0\"/></cellStyles></styleSheet>";
        var sheet1 = Decl + $"<worksheet xmlns=\"{Main}\" xmlns:r=\"{RelNs}\"><dimension ref=\"A1:D3\"/><sheetViews><sheetView workbookViewId=\"0\"/></sheetViews><sheetFormatPr defaultRowHeight=\"15\"/>" +
            "<sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>4</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c><c r=\"B2\"><v>120</v></c><c r=\"C2\" t=\"str\"><f>IF(A2=\"Zorvex Holdings\",\"big\",\"small\")" + extraFormula + "</f><v>big</v></c><c r=\"D2\" t=\"inlineStr\"><is><t>Write to mira.quillon@zorvex.example</t></is></c></row>" +
            "<row r=\"3\"><c r=\"A3\" t=\"s\"><v>2</v></c><c r=\"B3\"><v>5551234</v></c><c r=\"C3\"><f>SUM(B2:B3)</f><v>5551354</v></c><c r=\"D3\" t=\"b\"><v>1</v></c></row>" +
            "</sheetData><mergeCells count=\"1\"><mergeCell ref=\"C3:D3\"/></mergeCells>" +
            "<dataValidations count=\"1\"><dataValidation type=\"list\" sqref=\"A2\" showInputMessage=\"1\" promptTitle=\"Pick\" prompt=\"Choose a Zorvex Holdings entity\"><formula1>\"Zorvex Holdings,Other\"</formula1></dataValidation></dataValidations>" +
            "<hyperlinks><hyperlink ref=\"A2\" r:id=\"rId5\" display=\"Zorvex site\"/><hyperlink ref=\"A3\" location=\"Archive!A1\" display=\"Go to archive\"/></hyperlinks>" +
            "<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/><pageSetup orientation=\"portrait\" r:id=\"rId6\"/>" +
            "<headerFooter><oddHeader>&amp;LZorvex Holdings confidential</oddHeader></headerFooter><drawing r:id=\"rId2\"/><legacyDrawing r:id=\"rId3\"/><tableParts count=\"1\"><tablePart r:id=\"rId4\"/></tableParts></worksheet>";
        var sheet1Rels = Decl + $"<Relationships xmlns=\"{PkgRel}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/comments\" Target=\"../comments1.xml\"/><Relationship Id=\"rId2\" Type=\"{RelNs}/drawing\" Target=\"../drawings/drawing1.xml\"/>" +
            $"<Relationship Id=\"rId3\" Type=\"{RelNs}/vmlDrawing\" Target=\"../drawings/vmlDrawing1.vml\"/><Relationship Id=\"rId4\" Type=\"{RelNs}/table\" Target=\"../tables/table1.xml\"/>" +
            $"<Relationship Id=\"rId5\" Type=\"{RelNs}/hyperlink\" Target=\"https://zorvex.example/portal\" TargetMode=\"External\"/>" +
            $"<Relationship Id=\"rId6\" Type=\"{RelNs}/printerSettings\" Target=\"../printerSettings/printerSettings1.bin\"/></Relationships>";
        var sheet2 = Decl + $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Hidden note for Zorvex Holdings</t></is></c></row></sheetData></worksheet>";
        var comments = Decl + $"<comments xmlns=\"{Main}\"><authors><author>Mira Quillon</author></authors><commentList><comment ref=\"B2\" authorId=\"0\"><text><r><t>Check with Mira Quillon</t></r></text></comment></commentList></comments>";
        var vml = "<xml xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:x=\"urn:schemas-microsoft-com:office:excel\"><v:shape id=\"_x0000_s1025\" type=\"#_x0000_t202\"><x:ClientData ObjectType=\"Note\"><x:Row>1</x:Row><x:Column>1</x:Column></x:ClientData></v:shape></xml>";
        var table = Decl + $"<table xmlns=\"{Main}\" id=\"1\" name=\"Orders\" displayName=\"Orders\" ref=\"A1:B3\"><autoFilter ref=\"A1:B3\"/><tableColumns count=\"2\"><tableColumn id=\"1\" name=\"Customer\"/><tableColumn id=\"2\" name=\"Amount\"/></tableColumns><tableStyleInfo name=\"TableStyleMedium2\"/></table>";
        var drawing = Decl + "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><xdr:oneCellAnchor><xdr:from><xdr:col>5</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx=\"100\" cy=\"100\"/><xdr:sp><xdr:txBody><a:p><a:r><a:t>Zorvex Holdings text box</a:t></a:r></a:p></xdr:txBody></xdr:sp><xdr:clientData/></xdr:oneCellAnchor></xdr:wsDr>";
        var drawingRels = Decl + $"<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{RelNs}/chart\" Target=\"../charts/chart1.xml\"/><Relationship Id=\"rId2\" Type=\"{RelNs}/image\" Target=\"../media/image1.png\"/></Relationships>";
        var chart = Decl + "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><c:chart><c:title><c:tx><c:rich><a:p><a:r><a:t>Zorvex Holdings Revenue</a:t></a:r></a:p></c:rich></c:tx></c:title></c:chart></c:chartSpace>";
        var pivot = Decl + $"<pivotCacheDefinition xmlns=\"{Main}\"><cacheSource type=\"worksheet\"/><cacheFields count=\"1\"><cacheField name=\"Customer\"><sharedItems count=\"1\"><s v=\"Secret Pivot Customer\"/></sharedItems></cacheField></cacheFields></pivotCacheDefinition>";
        var external = Decl + $"<externalLink xmlns=\"{Main}\"><externalBook xmlns:r=\"{RelNs}\" r:id=\"rId1\"/></externalLink>";
        var list = new List<(string, string)>
        {
            ("[Content_Types].xml", types), ("_rels/.rels", rootRels), ("docProps/core.xml", core), ("docProps/app.xml", app),
            ("xl/workbook.xml", workbook), ("xl/_rels/workbook.xml.rels", workbookRels), ("xl/sharedStrings.xml", sst), ("xl/styles.xml", styles),
            ("xl/worksheets/sheet1.xml", sheet1), ("xl/worksheets/_rels/sheet1.xml.rels", sheet1Rels), ("xl/worksheets/sheet2.xml", sheet2),
            ("xl/comments1.xml", comments), ("xl/drawings/vmlDrawing1.vml", vml), ("xl/tables/table1.xml", table),
            ("xl/drawings/drawing1.xml", drawing), ("xl/drawings/_rels/drawing1.xml.rels", drawingRels), ("xl/charts/chart1.xml", chart),
            ("xl/media/image1.png", "PNGDATA Zorvex Holdings"), ("xl/pivotCache/pivotCacheDefinition1.xml", pivot), ("xl/externalLinks/externalLink1.xml", external),
            ("xl/calcChain.xml", Decl + $"<calcChain xmlns=\"{Main}\"><c r=\"C2\"/></calcChain>"), ("xl/printerSettings/printerSettings1.bin", "PRINTER Mira Quillon"),
            ("customXml/item1.xml", "<x>Mira Quillon custom</x>"),
        };
        return list;
    }

    private static string Write(string dir, string name, IEnumerable<(string Name, string Content)> parts)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Kit.Zip(parts.ToArray()));
        return path;
    }

    public static void Run(CheckFn check, string work)
    {
        var dir = Kit.Dir(work, "xlsx");
        var source = Write(dir, "book.xlsx", Parts());
        var format = DocumentFormats.Require(source);
        check("Workbook source is a valid Excel file for the SDK", CanOpen(source));

        // ---------------------------------------------------------------- reading
        var text = DocumentProcessor.ReadText(source);
        var lines = text.Split('\n');
        check("Read text has labelled cell lines", lines.Contains("Sales!A2: Zorvex Holdings") && lines.Contains("Sales!B1: Amount") && lines.Contains("Sales!B2: 120"));
        check("Read text joins rich text runs", lines.Contains("Sales!A3: Mira Quillon"));
        check("Read text includes numeric identifiers", lines.Contains("Sales!B3: 5551234"));
        check("Read text includes inline strings", lines.Contains("Sales!D2: Write to mira.quillon@zorvex.example"));
        check("Read text includes sheet and defined names", lines.Contains("Sheet name: Sales") && lines.Contains("Hidden sheet name: Archive") && lines.Any(l => l.StartsWith("Defined name: Total_Range")));
        check("Read text includes hidden sheet content with a hidden label", lines.Contains("[hidden] Archive!A1: Hidden note for Zorvex Holdings"));
        check("Read text includes comments, headers, validation, table and chart text", lines.Contains("Sales!B2 comment: Check with Mira Quillon") && lines.Contains("Sales oddHeader: Zorvex Holdings confidential") && lines.Any(l => l.Contains("validation message") && l.Contains("Zorvex Holdings entity")) && lines.Contains("Sales table column: Customer") && lines.Contains("Sales chart text: Zorvex Holdings Revenue"));
        check("Read text includes document properties and cell style names", lines.Contains("Document property creator: Mira Quillon") && lines.Contains("Document property Company: Zorvex Holdings") && lines.Contains("Cell style name: Zorvex Holdings Style"));
        check("Read text shows formula string literals", lines.Any(l => l.StartsWith("Sales!C2 formula: ") && l.Contains("\"Zorvex Holdings\"")));
        check("Read text does not include stale unused strings or pivot data", !text.Contains("Stale") && !text.Contains("Secret Pivot"));
        check("Read text is split by rows for review (one line per cell)", lines.Length > 20 && lines.All(l => l.Length < 200));
        check("Table context and review markup are not provided for xlsx", format.ReadTableContext(source, text) == null && !format.InspectReviewMarkup(source).HasReviewMarkup);
        var removals = format.DescribeRemovals(source);
        check("Removal report names pictures, charts, pivots, links and hidden sheets", new[] { "1 picture", "1 chart", "pivot cache", "another workbook", "hidden sheet" }.All(k => removals.Any(r => r.Contains(k))) && removals.All(r => !r.Contains('—')));

        // ---------------------------------------------------------------- forward transform
        var output = Path.Combine(dir, "book-protected.xlsx");
        DocumentProcessor.Desensitize(source, output, Maps);
        var parts = Kit.Read(output);
        var everything = string.Join("\n", parts.Values);
        check("Protected workbook holds no selected value in any part", new[] { "Zorvex", "Quillon", "5551234", "mira.quillon" }.All(v => !everything.Contains(v, StringComparison.OrdinalIgnoreCase)), string.Join(";", parts.Where(p => p.Value.Contains("Zorvex") || p.Value.Contains("Quillon")).Select(p => p.Key)));
        check("Protected workbook keeps only allowed parts", parts.Keys.All(k => !Regex.IsMatch(k, @"drawing1\.xml|chart|media|pivot|externalLink|calcChain|printerSettings|customXml|docProps/app")), string.Join(",", parts.Keys));
        check("Protected workbook has no external relationship", parts.Where(p => p.Key.EndsWith(".rels")).All(p => !p.Value.Contains("TargetMode")));
        check("Protected workbook has a vml drawing for the notes", parts.ContainsKey("xl/drawings/vmlDrawing1.vml") && parts["xl/worksheets/sheet1.xml"].Contains("legacyDrawing"));
        check("Protected workbook drops images and drawings from the sheet", !parts["xl/worksheets/sheet1.xml"].Contains("<drawing") && !parts["xl/worksheets/sheet1.xml"].Contains("pageSetup orientation=\"portrait\" r:id"));
        check("Protected workbook removes external hyperlink but keeps the internal one", !parts["xl/worksheets/sheet1.xml"].Contains("Zorvex site") && parts["xl/worksheets/sheet1.xml"].Contains("Archive!A1"));
        check("Protected workbook removes workbook level external and pivot references", !parts["xl/workbook.xml"].Contains("externalReference") && !parts["xl/workbook.xml"].Contains("pivotCache"));
        var after = DocumentProcessor.ReadText(output).Split('\n');
        check("Protected text replaces cell values", after.Contains("Sales!A2: [ORGANIZATION_001]") && after.Contains("Sales!A3: [PERSON_001]") && after.Contains("Sales!B3: [IDENTIFIER_001]") && after.Contains("Sales!D2: Write to [EMAIL_001]"));
        check("Protected text keeps numbers and business logic", after.Contains("Sales!B2: 120") && after.Contains("Sales!C3: 5551354"));
        check("Hidden sheet text is replaced and the sheet stays hidden", after.Contains("[hidden] Archive!A1: Hidden note for [ORGANIZATION_001]") && parts["xl/workbook.xml"].Contains("state=\"hidden\""));
        check("Formula string literal is rewritten, structure kept", parts["xl/worksheets/sheet1.xml"].Contains("IF(A2=\"[ORGANIZATION_001]\",\"big\",\"small\")") && parts["xl/worksheets/sheet1.xml"].Contains("SUM(B2:B3)"));
        check("Numeric cell holding a selected value becomes text", Regex.IsMatch(parts["xl/worksheets/sheet1.xml"], "<c r=\"B3\"[^>]*t=\"inlineStr\"[^>]*><is><t>\\[IDENTIFIER_001\\]</t></is></c>"));
        check("Other numbers are untouched", parts["xl/worksheets/sheet1.xml"].Contains("<v>120</v>") && parts["xl/worksheets/sheet1.xml"].Contains("<v>5551354</v>"));
        check("Rich text runs are replaced across the run boundary", parts["xl/sharedStrings.xml"].Contains("[PERSON_001]") && parts["xl/sharedStrings.xml"].Contains("uniqueCount=\"6\""));
        check("Stale shared string is blanked and counts stay", !parts["xl/sharedStrings.xml"].Contains("Stale") && Regex.Matches(parts["xl/sharedStrings.xml"], "<si>").Count == 6);
        check("Comment text is replaced and the author is neutral", parts["xl/comments1.xml"].Contains("Check with [PERSON_001]") && parts["xl/comments1.xml"].Contains("<author>Author 1</author>"));
        check("Header text and validation messages are replaced", parts["xl/worksheets/sheet1.xml"].Contains("&amp;L[ORGANIZATION_001] confidential") && parts["xl/worksheets/sheet1.xml"].Contains("Choose a [ORGANIZATION_001] entity"), parts["xl/worksheets/sheet1.xml"]);
        check("Cell style name is replaced", parts["xl/styles.xml"].Contains("[ORGANIZATION_001] Style") && parts["xl/styles.xml"].Contains("name=\"Normal\""));
        check("Document properties are cleared", !parts["docProps/core.xml"].Contains("creator") && !parts["docProps/core.xml"].Contains("title"));
        using (var zip = ZipFile.OpenRead(output))
            check("Entries carry the normalized timestamp and there is no archive comment", zip.Entries.All(e => e.LastWriteTime.Year == 1980) && zip.Entries.First().FullName == "[Content_Types].xml");
        check("Protected workbook opens with the SDK and has the same sheets", CanOpen(output) && SheetNames(output).SequenceEqual(new[] { "Sales", "Archive" }));
        check("Protected workbook adds no new validator errors", NewErrors(source, output) == 0);
        check("Cell counts and formula counts are unchanged", CountMatches(source, output, "<c ") && CountMatches(source, output, "<f>"));
        check("Leaves no temporary file", Kit.Leftovers(dir).Length == 0);
        format.Audit(source, output, Maps);
        check("Audit passes on a good copy", true);

        // ---------------------------------------------------------------- rehydrate
        var restored = Path.Combine(dir, "book-restored.xlsx");
        var unknown = DocumentProcessor.Rehydrate(output, restored, Maps);
        var back = DocumentProcessor.ReadText(restored).Split('\n');
        check("Rehydrate restores cell, comment, header and formula text", unknown == 0 && back.Contains("Sales!A2: Zorvex Holdings") && back.Contains("Sales!A3: Mira Quillon") && back.Contains("Sales!B3: 5551234") && back.Contains("Sales!B2 comment: Check with Mira Quillon") && back.Contains("Sales oddHeader: Zorvex Holdings confidential") && back.Any(l => l.StartsWith("Sales!C2 formula: ") && l.Contains("\"Zorvex Holdings\"")));
        check("Rehydrated workbook opens with the SDK and adds no validator errors", CanOpen(restored) && NewErrors(source, restored) == 0);
        check("Rehydrate counts unknown tokens", UnknownCount(output, dir) == 2);

        // ---------------------------------------------------------------- negative: names that cannot be renamed
        var badSheetSource = Write(dir, "sheetname.xlsx", Parts("Zorvex Holdings", "'Zorvex Holdings'"));
        var badSheetOut = Path.Combine(dir, "sheetname-out.xlsx");
        var message = Kit.Message(() => DocumentProcessor.Desensitize(badSheetSource, badSheetOut, Maps));
        check("Sheet name containing a selected value blocks the export with a clear message", message.StartsWith("Export blocked") && message.Contains("cannot be renamed safely") && !message.Contains("Zorvex"), message);
        check("Blocked export leaves no output and no temporary file", !File.Exists(badSheetOut) && Kit.Leftovers(dir).Length == 0);
        var nameSource = Write(dir, "definedname.xlsx", PartsWithDefinedName());
        var nameOut = Path.Combine(dir, "definedname-out.xlsx");
        var nameMessage = Kit.Message(() => DocumentProcessor.Desensitize(nameSource, nameOut, Maps.Append(Kit.Map("Mira_Quillon_Total", "IDENTIFIER", 2)).ToArray()));
        check("Defined name containing a selected value blocks the export", nameMessage.StartsWith("Export blocked") && !File.Exists(nameOut) && !nameMessage.Contains("Zorvex"), nameMessage);

        // ---------------------------------------------------------------- negative: unsafe content
        var external = Write(dir, "external.xlsx", Parts(extraFormula: "+[1]Other!A1"));
        var externalOut = Path.Combine(dir, "external-out.xlsx");
        var externalMessage = Kit.Message(() => DocumentProcessor.Desensitize(external, externalOut, Maps));
        check("Formula reading another workbook blocks the export", externalMessage.Contains("another workbook") && !File.Exists(externalOut), externalMessage);
        var macros = Parts();
        macros.Add(("xl/vbaProject.bin", "VBA"));
        var macroPath = Write(dir, "macro.xlsx", macros);
        check("Workbook with macros is refused", Kit.Throws(() => DocumentProcessor.ReadText(macroPath), "macros") && Kit.Throws(() => DocumentProcessor.Desensitize(macroPath, Path.Combine(dir, "macro-out.xlsx"), Maps), "macros") && !File.Exists(Path.Combine(dir, "macro-out.xlsx")));
        var xlsm = Path.Combine(dir, "book.xlsm");
        File.Copy(source, xlsm, true);
        check(".xlsm is refused by extension with advice", Kit.Throws(() => DocumentProcessor.ReadText(xlsm), "macros"));
        var notZip = Path.Combine(dir, "fake.xlsx");
        File.WriteAllText(notZip, "this is not a zip file Zorvex Holdings");
        var notZipMessage = Kit.Message(() => DocumentProcessor.ReadText(notZip));
        check("A file that is not a package gets a friendly message without its content", notZipMessage.Contains("not a valid Excel workbook") && !notZipMessage.Contains("Zorvex"));
        var duplicate = Path.Combine(dir, "duplicate.xlsx");
        File.WriteAllBytes(duplicate, Kit.ZipBytes(new[] { ("xl/workbook.xml", new byte[] { 1 }), ("xl/workbook.xml", new byte[] { 2 }) }));
        check("Archive with duplicate part names is refused", Kit.Throws(() => DocumentProcessor.ReadText(duplicate)));
        var dropped = Parts();
        dropped.RemoveAll(p => p.Name == "xl/worksheets/sheet2.xml");
        var droppedPath = Write(dir, "missing-sheet.xlsx", dropped);
        check("Workbook with a missing sheet part is refused", Kit.Throws(() => DocumentProcessor.ReadText(droppedPath)));

        // ---------------------------------------------------------------- negative: the audit catches tampering
        var tampered = Path.Combine(dir, "tampered.xlsx");
        Tamper(output, tampered, "xl/sharedStrings.xml", s => s.Replace("[ORGANIZATION_001]", "Zorvex Holdings"));
        check("Audit rejects a copy that still holds a selected value", Kit.Throws(() => format.Audit(source, tampered, Maps), "Export blocked"));
        var external2 = Path.Combine(dir, "tampered2.xlsx");
        Tamper(output, external2, "xl/worksheets/_rels/sheet1.xml.rels", s => s.Replace("</Relationships>", "<Relationship Id=\"rIdX\" Type=\"http://x/hyperlink\" Target=\"https://example.test\" TargetMode=\"External\"/></Relationships>"));
        check("Audit rejects an external relationship", Kit.Throws(() => format.Audit(source, external2, Maps), "external"));
        var extraPart = Path.Combine(dir, "tampered3.xlsx");
        AddPart(output, extraPart, "xl/media/image9.png", "x");
        check("Audit rejects a part that is not on the allow list", Kit.Throws(() => format.Audit(source, extraPart, Maps), "allowed list"));
        var changedFormula = Path.Combine(dir, "tampered4.xlsx");
        Tamper(output, changedFormula, "xl/worksheets/sheet1.xml", s => s.Replace("SUM(B2:B3)", "SUM(B2:B2)"));
        check("Preservation audit rejects a changed formula", Kit.Throws(() => format.Audit(source, changedFormula, Maps), "formula"));
        var changedValue = Path.Combine(dir, "tampered5.xlsx");
        Tamper(output, changedValue, "xl/worksheets/sheet1.xml", s => s.Replace("<v>120</v>", "<v>121</v>"));
        check("Preservation audit rejects a changed number", Kit.Throws(() => format.Audit(source, changedValue, Maps), "outside the selected"));
        var danglingRef = Path.Combine(dir, "tampered6.xlsx");
        Tamper(output, danglingRef, "xl/worksheets/sheet1.xml", s => s.Replace("</worksheet>", "<drawing r:id=\"rId99\"/></worksheet>"));
        check("Audit rejects a reference that points nowhere", Kit.Throws(() => format.Audit(source, danglingRef, Maps)));
        check("Export never overwrites the source", Kit.Throws(() => DocumentProcessor.Desensitize(source, source, Maps)));

        // ---------------------------------------------------------------- chart sheets
        var chartSheet = Parts();
        var wb = chartSheet.First(p => p.Name == "xl/workbook.xml");
        chartSheet.Remove(wb);
        chartSheet.Add(("xl/workbook.xml", wb.Content.Replace("<sheet name=\"Archive\" sheetId=\"2\" state=\"hidden\" r:id=\"rId2\"/>", "<sheet name=\"Archive\" sheetId=\"2\" state=\"hidden\" r:id=\"rId2\"/><sheet name=\"Chart1\" sheetId=\"3\" r:id=\"rId9\"/>")));
        var wbr = chartSheet.First(p => p.Name == "xl/_rels/workbook.xml.rels");
        chartSheet.Remove(wbr);
        chartSheet.Add(("xl/_rels/workbook.xml.rels", wbr.Content.Replace("</Relationships>", $"<Relationship Id=\"rId9\" Type=\"{RelNs}/chartsheet\" Target=\"chartsheets/sheet1.xml\"/></Relationships>")));
        chartSheet.Add(("xl/chartsheets/sheet1.xml", Decl + $"<chartsheet xmlns=\"{Main}\"/>"));
        var chartSource = Write(dir, "chartsheet.xlsx", chartSheet);
        var chartOut = Path.Combine(dir, "chartsheet-out.xlsx");
        DocumentProcessor.Desensitize(chartSource, chartOut, Maps);
        check("Chart sheet is replaced by an empty sheet and the sheet list is unchanged", SheetNames(chartOut).SequenceEqual(new[] { "Sales", "Archive", "Chart1" }) && !Kit.Read(chartOut).Keys.Any(k => k.Contains("chartsheets")));

        // ---------------------------------------------------------------- no selected value present
        var clean = Path.Combine(dir, "clean-out.xlsx");
        DocumentProcessor.Desensitize(source, clean, new[] { Kit.Map("Nobody Here", "PERSON", 9) });
        check("Export with a mapping that does not occur still strips unsafe parts", Kit.Read(clean).Keys.All(k => !k.Contains("media") && !k.Contains("chart")) && Kit.Read(clean)["xl/comments1.xml"].Contains("Author 1"));
    }

    private static List<(string Name, string Content)> PartsWithDefinedName()
    {
        var parts = Parts();
        var wb = parts.First(p => p.Name == "xl/workbook.xml");
        parts.Remove(wb);
        parts.Add(("xl/workbook.xml", wb.Content.Replace("name=\"Total_Range\"", "name=\"Mira_Quillon_Total\"")));
        return parts;
    }

    private static bool CanOpen(string path)
    {
        try { using var doc = SpreadsheetDocument.Open(path, false); return doc.WorkbookPart != null; }
        catch { return false; }
    }

    private static string[] SheetNames(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        return doc.WorkbookPart!.Workbook.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Select(s => s.Name!.Value!).ToArray();
    }

    private static int NewErrors(string source, string output)
    {
        var validator = new OpenXmlValidator(FileFormatVersions.Office2021);
        using var a = SpreadsheetDocument.Open(source, false);
        using var b = SpreadsheetDocument.Open(output, false);
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

    private static bool CountMatches(string source, string output, string needle)
    {
        int Count(string path) => Kit.Read(path).Where(p => p.Key.StartsWith("xl/worksheets/sheet")  && p.Key.EndsWith(".xml") && !p.Key.Contains("_rels")).Sum(p => Regex.Matches(p.Value, Regex.Escape(needle)).Count);
        return Count(source) == Count(output);
    }

    private static int UnknownCount(string protectedPath, string dir)
    {
        // Rehydrate with only one of the mappings: the other tokens are then unknown.
        var partial = Path.Combine(dir, "partial.xlsx");
        return DocumentProcessor.Rehydrate(protectedPath, partial, new[] { Maps[0], Maps[1] });
    }

    private static void Tamper(string input, string output, string part, Func<string, string> change)
    {
        var parts = Kit.Read(input).Select(p => (p.Key, p.Key == part ? change(p.Value) : p.Value)).ToArray();
        File.WriteAllBytes(output, Kit.Zip(parts));
    }

    private static void AddPart(string input, string output, string name, string content)
    {
        var parts = Kit.Read(input).Select(p => (p.Key, p.Value)).Append((name, content)).ToArray();
        File.WriteAllBytes(output, Kit.Zip(parts));
    }
}

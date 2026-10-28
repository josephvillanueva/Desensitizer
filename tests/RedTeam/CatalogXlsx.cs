using System.Text;
using static RedTeam.Sentinel;

namespace RedTeam;

public static partial class Catalog
{
    static Attack Xx(string name, string desc, Action<Xl> mod, string[]? values = null)
    {
        var a = Add("xlsx/" + name, desc, ".xlsx", () => { var x = new Xl(); mod(x); return x.ToBytes(); }, values);
        a.BuildVisible = () =>
        {
            var x = new Xl(); mod(x);
            x.AddRaw(x.Cell("A1000", "inline", "Reference " + string.Join(" | ", a.Values)), 1000);
            return x.ToBytes();
        };
        return a;
    }

    const string SheetXmlNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    static void Xlsx()
    {
        Xx("control/shared-string", "value in a shared string", x => x.Row(2, ("A2", "s", "Owner " + Person + " of " + Company + " " + Id)));
        Xx("control/inline-string", "value in an inline string", x => x.Row(2, ("A2", "inline", Person)));
        Xx("control/rich-text-split", "shared string rich text split across runs", x => x.Shared.Add("<si><r><t>Zel</t></r><r><t>da Quim</t></r><r><t>by-Orsino</t></r></si>"), new[] { Person });
        Xx("control/formula-cached-text", "formula with a cached text result", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"str\"><f>\"x\"</f><v>" + Person + "</v></c></row>");
        Xx("control/number-cell-as-id", "an id stored as text in a number-looking cell", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"str\"><v>" + Id + "</v></c></row>");

        // Names that are not renamed
        Xx("names/sheet-exact", "sheet name is the exact value", x => x.SheetName = Person);
        Xx("names/sheet-underscore", "sheet name with underscores", x => x.SheetName = Under(Company));
        Xx("names/sheet-prefix-suffix", "sheet name embeds the value among other words", x => x.SheetName = "Q3 " + Company + " 2026");
        Xx("names/definedname-underscore", "defined name derived from the value", x => x.WorkbookAfterSheets = "<definedNames><definedName name=\"" + Under(Person) + "\">Sheet1!$A$1</definedName></definedNames>");
        Xx("names/definedname-exact-constant", "defined name constant holds the exact value", x => x.WorkbookAfterSheets = "<definedNames><definedName name=\"Owner\">\"" + Person + "\"</definedName></definedNames>");
        Xx("names/definedname-split-constant", "defined name constant built from two literals", x => x.WorkbookAfterSheets = "<definedNames><definedName name=\"Owner\">\"Zelda \"&amp;\"Quimby-Orsino\"</definedName></definedNames>", new[] { Person });
        Xx("names/definedname-comment", "defined name comment attribute", x => x.WorkbookAfterSheets = "<definedNames><definedName name=\"Owner\" comment=\"" + Person + "\">Sheet1!$A$1</definedName></definedNames>");
        Xx("names/definedname-comment-variant", "defined name comment attribute, separator variant", x => x.WorkbookAfterSheets = "<definedNames><definedName name=\"Owner\" comment=\"" + Under(Person) + "\">Sheet1!$A$1</definedName></definedNames>");
        Xx("names/workbookpr-codename", "workbookPr codeName and fileVersion appName", x => x.WorkbookBeforeSheets = "<fileVersion appName=\"" + Under(Company) + "\" lastEdited=\"7\"/><workbookPr codeName=\"" + Under(Person) + "\"/>");
        Xx("names/workbook-protection", "workbookProtection attributes", x => x.WorkbookBeforeSheets = "<workbookProtection workbookAlgorithmName=\"" + Person + "\" lockStructure=\"1\"/>");
        Xx("names/sheetpr-codename", "sheetPr codeName", x => x.SheetBeforeData = "<sheetPr codeName=\"" + Under(Person) + "\"/>");
        Xx("names/sheetpr-codename-exact", "sheetPr codeName exact", x => x.SheetBeforeData = "<sheetPr codeName=\"" + Person + "\"/>");
        Xx("names/hidden-sheet-state", "the only sheet is hidden and holds the value", x => { x.SheetState = " state=\"hidden\""; x.Row(2, ("A2", "s", Person)); });
        Xx("names/very-hidden-sheet-state", "the only sheet is very hidden and holds the value", x => { x.SheetState = " state=\"veryHidden\""; x.Row(2, ("A2", "s", Company)); });
        Xx("names/hidden-rows-cols", "hidden rows and columns hold the value", x => { x.Rows += "<row r=\"2\" hidden=\"1\"><c r=\"A2\" t=\"inlineStr\"><is><t>" + Person + "</t></is></c></row>"; x.SheetRootAttrs = ""; });

        // Table parts
        void Table(Xl x, string name, string display, string column)
        {
            x.Overrides["/xl/tables/table1.xml"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml";
            x.SetPart("xl/tables/table1.xml", OoxmlBuilder.Decl + "<table xmlns=\"" + SheetXmlNs + "\" id=\"1\" name=\"" + OoxmlBuilder.Esc(name) + "\" displayName=\"" + OoxmlBuilder.Esc(display) + "\" ref=\"A1:B2\" totalsRowShown=\"0\"><autoFilter ref=\"A1:B2\"/><tableColumns count=\"2\"><tableColumn id=\"1\" name=\"Quarterly figures\"/><tableColumn id=\"2\" name=\"" + OoxmlBuilder.Esc(column) + "\"/></tableColumns><tableStyleInfo name=\"TableStyleMedium2\" showRowStripes=\"1\"/></table>");
            x.Rel("xl/worksheets/sheet1.xml", "rIdT1", "table", "../tables/table1.xml");
            x.Row(2, ("A2", "s", "a"), ("B2", "s", "b"));
            x.Shared.Add("Column two");
            x.SheetAfterData = "<tableParts count=\"1\"><tablePart r:id=\"rIdT1\"/></tableParts>";
        }
        Xx("table/name-underscore", "table name and displayName derived from the value", x => Table(x, "Table_" + Under(Company), "Table_" + Under(Company), "Amount"));
        Xx("table/name-exact-column", "table column header is the exact value", x => Table(x, "Table1", "Table1", Person));
        Xx("table/column-variant", "table column header, separator variant", x => Table(x, "Table1", "Table1", Under(Person)));

        // Comments and notes
        Xx("comments/note-text-author", "legacy comment text and author", x =>
        {
            x.Overrides["/xl/comments1.xml"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.comments+xml";
            x.Defaults["vml"] = "application/vnd.openxmlformats-officedocument.vmlDrawing";
            x.SetPart("xl/comments1.xml", OoxmlBuilder.Decl + "<comments xmlns=\"" + SheetXmlNs + "\"><authors><author>" + Person + "</author></authors><commentList><comment ref=\"A1\" authorId=\"0\"><text><r><t>" + Company + " " + Id + "</t></r></text></comment></commentList></comments>");
            x.SetPart("xl/drawings/vmlDrawing1.vml", "<xml xmlns:v=\"urn:schemas-microsoft-com:vml\"><v:shape id=\"s1\" alt=\"" + Person + "\"><v:textbox><div>" + Person + "</div></v:textbox></v:shape></xml>");
            x.Rel("xl/worksheets/sheet1.xml", "rIdC", "comments", "../comments1.xml");
            x.Rel("xl/worksheets/sheet1.xml", "rIdV", "vmlDrawing", "../drawings/vmlDrawing1.vml");
            x.SheetAfterData = "<legacyDrawing r:id=\"rIdV\"/>";
        });
        Xx("comments/threaded", "threaded comment part and persons", x =>
        {
            x.Overrides["/xl/threadedComments/threadedComment1.xml"] = "application/vnd.ms-excel.threadedcomments+xml";
            x.SetPart("xl/threadedComments/threadedComment1.xml", OoxmlBuilder.Decl + "<ThreadedComments xmlns=\"http://schemas.microsoft.com/office/spreadsheetml/2018/threadedcomments\"><threadedComment ref=\"A1\" dT=\"2020-01-01T00:00:00.00\" personId=\"{1}\" id=\"{2}\"><text>" + Person + "</text></threadedComment></ThreadedComments>");
            x.Rel("xl/worksheets/sheet1.xml", "rIdTC", "http://schemas.microsoft.com/office/2017/10/relationships/threadedComment", "../threadedComments/threadedComment1.xml");
        });

        // Validation, conditional format, hyperlinks
        Xx("validation/prompt-error-list", "data validation prompt, error and list formula", x => x.SheetAfterData = "<dataValidations count=\"1\"><dataValidation type=\"list\" allowBlank=\"1\" showInputMessage=\"1\" showErrorMessage=\"1\" errorTitle=\"" + Person + "\" error=\"" + Company + "\" promptTitle=\"t\" prompt=\"" + Id + "\" sqref=\"A2\"><formula1>\"" + Person + ",Other\"</formula1></dataValidation></dataValidations>");
        Xx("validation/variant", "data validation messages, separator variant", x => x.SheetAfterData = "<dataValidations count=\"1\"><dataValidation type=\"list\" showErrorMessage=\"1\" error=\"" + Under(Company) + "\" sqref=\"A2\"><formula1>\"" + Under(Person) + ",Other\"</formula1></dataValidation></dataValidations>");
        Xx("cond-format/text-and-formula", "conditional format text rule and formula", x => x.SheetAfterData = "<conditionalFormatting sqref=\"A1:A5\"><cfRule type=\"containsText\" dxfId=\"0\" priority=\"1\" operator=\"containsText\" text=\"" + Person + "\"><formula>NOT(ISERROR(SEARCH(\"" + Person + "\",A1)))</formula></cfRule></conditionalFormatting>");
        Xx("cond-format/formula-split-literals", "conditional format formula built from literals", x => x.SheetAfterData = "<conditionalFormatting sqref=\"A1:A5\"><cfRule type=\"expression\" priority=\"1\"><formula>A1=\"Zelda \"&amp;\"Quimby-Orsino\"</formula></cfRule></conditionalFormatting>", new[] { Person });
        Xx("hyperlink/external-rel", "external hyperlink target and tooltip", x =>
        {
            x.Rel("xl/worksheets/sheet1.xml", "rIdH", "hyperlink", "https://example.test/" + Under(Person), true);
            x.SheetAfterData = "<hyperlinks><hyperlink ref=\"A1\" r:id=\"rIdH\" tooltip=\"" + Person + "\" display=\"" + Company + "\"/></hyperlinks>";
        });
        Xx("hyperlink/internal-location", "internal hyperlink location, display and tooltip", x => x.SheetAfterData = "<hyperlinks><hyperlink ref=\"A1\" location=\"'" + Person + "'!A1\" display=\"" + Company + "\" tooltip=\"" + Person + "\"/></hyperlinks>");
        Xx("hyperlink/location-variant", "internal hyperlink location, underscored sheet reference", x => x.SheetAfterData = "<hyperlinks><hyperlink ref=\"A1\" location=\"" + Under(Person) + "!A1\"/></hyperlinks>");
        Xx("hyperlink/hyperlink-formula", "HYPERLINK formula with url and friendly name", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"str\"><f>HYPERLINK(\"https://example.test/" + Under(Person) + "\",\"" + Person + "\")</f><v>" + Person + "</v></c></row>");

        // Formulas that build the value
        Xx("formula/concatenate", "CONCATENATE of fragments with a cached full value", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"str\"><f>CONCATENATE(\"Zelda\",\" \",\"Quimby-Orsino\")</f><v>" + Person + "</v></c></row>", new[] { Person });
        Xx("formula/ampersand", "ampersand join of fragments with a cached full value", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"str\"><f>\"Vantablack\"&amp;\" \"&amp;\"Holdings\"&amp;\" Zeta\"</f><v>" + Company + "</v></c></row>", new[] { Company });
        Xx("formula/no-cached-value", "formula fragments with no cached value", x => x.Rows += "<row r=\"2\"><c r=\"A2\"><f>\"ZQ-\"&amp;\"5150-\"&amp;\"0099\"</f></c></row>", new[] { Id });
        Xx("formula/char-codes", "formula that builds the value from CHAR codes", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"str\"><f>CHAR(90)&amp;CHAR(81)&amp;\"-5150-0099\"</f><v>" + Id + "</v></c></row>", new[] { Id });
        Xx("formula/exact-literal", "formula holds the exact value as a literal", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"str\"><f>\"" + Person + "\"</f><v>" + Person + "</v></c></row>");
        Xx("formula/shared-formula-text", "shared formula master holds the exact value", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"str\"><f t=\"shared\" ref=\"A2:A3\" si=\"0\">\"" + Person + "\"</f><v>x</v></c></row><row r=\"3\"><c r=\"A3\" t=\"str\"><f t=\"shared\" si=\"0\"/><v>x</v></c></row>");
        Xx("formula/external-reference", "formula reading another workbook (documented as blocked)", x => x.Rows += "<row r=\"2\"><c r=\"A2\"><f>[1]Sheet1!A1</f><v>1</v></c></row>");
        Xx("formula/array-and-name", "array formula and 3D reference text", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"str\"><f t=\"array\" ref=\"A2\">TEXT(1,\"\\\"" + Person + "\\\"\")</f><v>x</v></c></row>");

        // Styles
        Xx("styles/numfmt-literal", "number format with literal text", x => x.StylesXml = OoxmlBuilder.Decl + "<styleSheet xmlns=\"" + SheetXmlNs + "\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"&quot;" + Person + "&quot; 0\"/></numFmts><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
        Xx("styles/numfmt-literal-variant", "number format with literal text, separator variant", x => x.StylesXml = OoxmlBuilder.Decl + "<styleSheet xmlns=\"" + SheetXmlNs + "\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"&quot;" + Under(Company) + "&quot; 0\"/></numFmts><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
        Xx("styles/cellstyle-name", "custom cell style name", x => x.StylesXml = OoxmlBuilder.Decl + "<styleSheet xmlns=\"" + SheetXmlNs + "\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs><cellStyles count=\"2\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/><cellStyle name=\"" + Person + " style\" xfId=\"1\"/></cellStyles></styleSheet>");
        Xx("styles/font-name", "font name holds the value", x => x.StylesXml = OoxmlBuilder.Decl + "<styleSheet xmlns=\"" + SheetXmlNs + "\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"" + Company + "\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
        Xx("styles/dxf-numfmt", "differential format with a literal number format", x => x.StylesXml = OoxmlBuilder.Decl + "<styleSheet xmlns=\"" + SheetXmlNs + "\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles><dxfs count=\"1\"><dxf><numFmt numFmtId=\"165\" formatCode=\"&quot;" + Person + "&quot;\"/></dxf></dxfs></styleSheet>");

        // Header and footer
        Xx("headerfooter/exact", "header and footer text", x => x.SheetAfterData = "<headerFooter><oddHeader>&amp;L" + Person + "&amp;R" + Company + "</oddHeader><oddFooter>&amp;C" + Id + "</oddFooter></headerFooter>");
        Xx("headerfooter/font-code-split", "header text split by a font code", x => x.SheetAfterData = "<headerFooter><oddHeader>&amp;LZelda &amp;\"Arial\"Quimby-Orsino</oddHeader></headerFooter>", new[] { Person });
        Xx("headerfooter/variant", "header text, separator variant", x => x.SheetAfterData = "<headerFooter><evenHeader>&amp;L" + Under(Person) + "</evenHeader><firstFooter>&amp;C" + Under(Company) + "</firstFooter></headerFooter>");

        // Cell types and strings
        Xx("cells/error-type-text", "error-typed cell holding the value", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"e\"><v>" + Person + "</v></c></row>");
        Xx("cells/bool-type-text", "boolean-typed cell holding the value", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"b\"><v>" + Person + "</v></c></row>");
        Xx("cells/date-type-text", "date-typed cell holding the value", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"d\"><v>" + Person + "</v></c></row>");
        Xx("cells/unreferenced-shared-string", "shared string no cell uses", x => x.Shared.Add(Person), new[] { Person });
        Xx("cells/shared-string-phonetic", "phonetic guide text holds the value", x => x.Shared.Add("<si><t>kana</t><rPh sb=\"0\" eb=\"1\"><t>" + Person + "</t></rPh></si>"));
        Xx("cells/inline-rich-split", "inline rich text split across runs", x => x.Rows += "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><r><t>Vantablack </t></r><r><t>Holdings</t></r><r><t> Zeta</t></r></is></c></row>", new[] { Company });
        Xx("cells/string-cell-lowercase", "lower case value in a cell", x => x.Row(2, ("A2", "s", Person.ToLowerInvariant())));
        Xx("cells/row-uid-attribute", "row attribute xr:uid carries the exact value", x =>
        {
            x.RootExtraNs = " xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:xr=\"http://schemas.microsoft.com/office/spreadsheetml/2014/revision\" mc:Ignorable=\"xr\"";
            x.Rows += "<row r=\"2\" xr:uid=\"" + Person + "\"><c r=\"A2\" t=\"inlineStr\"><is><t>a</t></is></c></row>";
        });
        Xx("cells/across-cells", "value split across two neighbouring cells", x => x.Row(2, ("A2", "s", "Zelda"), ("B2", "s", "Quimby-Orsino")), new[] { Person });

        // Parts that must be dropped
        Xx("parts/drawing-with-textbox", "drawing part with a text box holding the value", x =>
        {
            x.Overrides["/xl/drawings/drawing1.xml"] = "application/vnd.openxmlformats-officedocument.drawing+xml";
            x.SetPart("xl/drawings/drawing1.xml", OoxmlBuilder.Decl + "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><xdr:twoCellAnchor><xdr:sp><xdr:txBody><a:bodyPr/><a:p><a:r><a:t>" + Person + "</a:t></a:r></a:p></xdr:txBody></xdr:sp></xdr:twoCellAnchor></xdr:wsDr>");
            x.Rel("xl/worksheets/sheet1.xml", "rIdD", "drawing", "../drawings/drawing1.xml");
            x.SheetAfterData = "<drawing r:id=\"rIdD\"/>";
        });
        Xx("parts/chart-title", "chart part title holds the value", x =>
        {
            x.Overrides["/xl/charts/chart1.xml"] = "application/vnd.openxmlformats-officedocument.drawingml.chart+xml";
            x.SetPart("xl/charts/chart1.xml", OoxmlBuilder.Decl + "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><c:chart><c:title><c:tx><c:rich><a:p><a:r><a:t>" + Company + "</a:t></a:r></a:p></c:rich></c:tx></c:title></c:chart></c:chartSpace>");
        });
        Xx("parts/pivot-cache", "pivot cache definition and records hold the value", x =>
        {
            x.Overrides["/xl/pivotCache/pivotCacheDefinition1.xml"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.pivotCacheDefinition+xml";
            x.SetPart("xl/pivotCache/pivotCacheDefinition1.xml", OoxmlBuilder.Decl + "<pivotCacheDefinition xmlns=\"" + SheetXmlNs + "\"><cacheSource type=\"worksheet\"><worksheetSource ref=\"A1:A2\" sheet=\"Sheet1\"/></cacheSource><cacheFields count=\"1\"><cacheField name=\"f\"><sharedItems count=\"1\"><s v=\"" + Person + "\"/></sharedItems></cacheField></cacheFields></pivotCacheDefinition>");
            x.Rel("xl/workbook.xml", "rIdPC", "pivotCacheDefinition", "pivotCache/pivotCacheDefinition1.xml");
        });
        Xx("parts/external-link", "externalLink part with sheet names and cached data", x =>
        {
            x.Overrides["/xl/externalLinks/externalLink1.xml"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.externalLink+xml";
            x.SetPart("xl/externalLinks/externalLink1.xml", OoxmlBuilder.Decl + "<externalLink xmlns=\"" + SheetXmlNs + "\"><externalBook xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rId1\"><sheetNames><sheetName val=\"" + Person + "\"/></sheetNames></externalBook></externalLink>");
            x.Rel("xl/externalLinks/externalLink1.xml", "rId1", "externalLinkPath", "file:///C:/" + Under(Company) + ".xlsx", true);
            x.Rel("xl/workbook.xml", "rIdEL", "externalLink", "externalLinks/externalLink1.xml");
        });
        Xx("parts/custom-xml-and-props", "customXml item, custom.xml and app.xml hold the value", x =>
        {
            x.SetPart("customXml/item1.xml", OoxmlBuilder.Decl + "<root>" + Person + "</root>", "application/xml");
            x.Overrides["/docProps/app.xml"] = "application/vnd.openxmlformats-officedocument.extended-properties+xml";
            x.SetPart("docProps/app.xml", OoxmlBuilder.Decl + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Company>" + Company + "</Company></Properties>");
            x.Rel("", "rIdA", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties", "docProps/app.xml");
        });
        Xx("parts/core-properties", "core properties hold the value", x => x.SetPart("docProps/core.xml", OoxmlBuilder.Decl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:creator>" + Person + "</dc:creator><dc:title>" + Company + "</dc:title></cp:coreProperties>"));
        Xx("parts/core-comment", "XML comment in core.xml", x => x.SetPart("docProps/core.xml", OoxmlBuilder.Decl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><!-- " + Person + " --></cp:coreProperties>"));
        Xx("parts/xml-comment-sheet", "XML comment in the worksheet part", x => x.Rows += "<!-- " + Person + " --><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>a</t></is></c></row>");
        Xx("parts/xml-comment-workbook", "XML comment in the workbook part", x => x.WorkbookAfterSheets = "<!-- " + Company + " -->");
        Xx("parts/macro-sheet", "macro sheet part (documented as blocked)", x => x.SetPart("xl/macrosheets/sheet1.xml", OoxmlBuilder.Decl + "<xm:macrosheet xmlns:xm=\"urn:x\">" + Person + "</xm:macrosheet>", "application/vnd.ms-excel.macrosheet+xml"));
        Xx("parts/vba", "vbaProject.bin (documented as blocked)", x => x.SetBytes("xl/vbaProject.bin", Encoding.UTF8.GetBytes(Person)));
        Xx("parts/named-part-exact", "extra part named with the exact value", x => x.SetBytes("xl/worksheets/" + Person + ".xml", Encoding.UTF8.GetBytes("<a/>")));
        Xx("parts/unreferenced-sheet-part", "worksheet part no workbook relationship points to", x => x.SetPart("xl/worksheets/sheet2.xml", OoxmlBuilder.Decl + "<worksheet xmlns=\"" + SheetXmlNs + "\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>" + Person + "</t></is></c></row></sheetData></worksheet>", Xl.Ws));
        Add("xlsx/zip/comment-direct", "xlsx zip comment holds the value", ".xlsx", () => { var x = new Xl { ZipComment = Person + " " + Company }; x.Row(2, ("A2", "s", Id)); return x.ToBytes(); });
        Add("xlsx/zip/duplicate-sheet-entry", "duplicate worksheet entry names", ".xlsx", () => { var x = new Xl(); var e = x.Entries(); e.Add(new ZEntry { Name = "xl/worksheets/sheet1.xml", Data = Encoding.UTF8.GetBytes("<worksheet xmlns=\"" + SheetXmlNs + "\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>" + Person + "</t></is></c></row></sheetData></worksheet>") }); return RawZip.Write(e); });
        Add("xlsx/zip/not-a-zip", "plain text named .xlsx", ".xlsx", () => Encoding.UTF8.GetBytes(Person));
        Add("xlsx/zip/csv-named-xlsx", "csv content named .xlsx", ".xlsx", () => Encoding.UTF8.GetBytes("a,b\n" + Person + ",2\n"));
    }
}


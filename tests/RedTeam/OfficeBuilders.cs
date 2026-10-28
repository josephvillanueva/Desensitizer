using System.Text;

namespace RedTeam;

// Shared pieces for the small .xlsx and .pptx files the attacks build.
public abstract class OoxmlBuilder
{
    public const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
    public const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    public List<ZEntry> Extra = new();
    public Dictionary<string, string> Defaults = new() { ["rels"] = "application/vnd.openxmlformats-package.relationships+xml", ["xml"] = "application/xml" };
    public Dictionary<string, string> Overrides = new();
    public Dictionary<string, List<(string Id, string Type, string Target, bool External)>> Rels = new();
    public Dictionary<string, byte[]> Raw = new();
    public string ZipComment = "";

    public static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";
    public static byte[] U8(string s) => new UTF8Encoding(false).GetBytes(s);

    public void Rel(string part, string id, string type, string target, bool external = false)
    {
        if (!Rels.TryGetValue(part, out var list)) Rels[part] = list = new();
        list.Add((id, type.Contains("://") ? type : RelNs + type, target, external));
    }

    public void SetPart(string name, string xml, string? ct = null)
    {
        Raw[name] = U8(xml);
        if (ct != null) Overrides["/" + name] = ct;
    }
    public void SetBytes(string name, byte[] data, string? ct = null)
    {
        Raw[name] = data;
        if (ct != null) Overrides["/" + name] = ct;
    }

    static string RelsXml(IEnumerable<(string Id, string Type, string Target, bool External)> rels) =>
        Decl + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        string.Concat(rels.Select(r => "<Relationship Id=\"" + Esc(r.Id) + "\" Type=\"" + r.Type + "\" Target=\"" + Esc(r.Target) + "\"" + (r.External ? " TargetMode=\"External\"" : "") + "/>")) + "</Relationships>";

    protected abstract void Fill();

    public List<ZEntry> Entries()
    {
        Fill();
        var all = new List<ZEntry>();
        var types = Decl + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            string.Concat(Defaults.Select(d => "<Default Extension=\"" + d.Key + "\" ContentType=\"" + d.Value + "\"/>")) +
            string.Concat(Overrides.Select(o => "<Override PartName=\"" + Esc(o.Key) + "\" ContentType=\"" + o.Value + "\"/>")) + "</Types>";
        all.Add(new ZEntry { Name = "[Content_Types].xml", Data = U8(types) });
        foreach (var r in Rels)
        {
            var name = r.Key.Length == 0 ? "_rels/.rels" : r.Key.Substring(0, r.Key.LastIndexOf('/') + 1) + "_rels/" + r.Key.Substring(r.Key.LastIndexOf('/') + 1) + ".rels";
            all.Add(new ZEntry { Name = name, Data = U8(RelsXml(r.Value)) });
        }
        foreach (var p in Raw) all.Add(new ZEntry { Name = p.Key, Data = p.Value });
        all.AddRange(Extra);
        return all;
    }

    public byte[] ToBytes() => RawZip.Write(Entries(), ZipComment);
}

// A one-sheet workbook. Hooks inject markup at the places Excel keeps it.
public sealed class Xl : OoxmlBuilder
{
    public const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const string Wb = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    public const string Ws = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
    public string SheetName = "Sheet1";
    public List<string> Shared = new();
    public string Rows = "";                   // <row> elements
    public string SheetAfterData = "";         // after sheetData, in schema order
    public string SheetBeforeData = "";
    public string SheetRootAttrs = "";
    public string WorkbookAfterSheets = "";    // definedNames etc.
    public string WorkbookBeforeSheets = "";   // fileVersion, workbookPr
    public string? StylesXml;
    public string SheetState = "";
    public string RootExtraNs = "";

    public Xl() { Row(1, ("A1", "s", "Quarterly figures")); }

    public int S(string text) { Shared.Add(text); return Shared.Count - 1; }
    public void Row(int n, params (string Ref, string Type, string Value)[] cells) =>
        Rows += "<row r=\"" + n + "\">" + string.Concat(cells.Select(c => Cell(c.Ref, c.Type, c.Value))) + "</row>";
    public string Cell(string r, string type, string value) => type switch
    {
        "s" => "<c r=\"" + r + "\" t=\"s\"><v>" + S(value) + "</v></c>",
        "inline" => "<c r=\"" + r + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + Esc(value) + "</t></is></c>",
        "n" => "<c r=\"" + r + "\"><v>" + Esc(value) + "</v></c>",
        _ => "<c r=\"" + r + "\" t=\"" + type + "\"><v>" + Esc(value) + "</v></c>",
    };
    public void AddRaw(string cellsXml, int row) => Rows += "<row r=\"" + row + "\">" + cellsXml + "</row>";

    protected override void Fill()
    {
        Overrides["/xl/workbook.xml"] = Wb;
        Overrides["/xl/worksheets/sheet1.xml"] = Ws;
        Overrides["/xl/sharedStrings.xml"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml";
        Overrides["/xl/styles.xml"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml";
        Overrides["/docProps/core.xml"] = "application/vnd.openxmlformats-package.core-properties+xml";
        Rel("", "rId1", "officeDocument", "xl/workbook.xml");
        Rel("", "rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
        Rel("xl/workbook.xml", "rId1", "worksheet", "worksheets/sheet1.xml");
        Rel("xl/workbook.xml", "rId2", "styles", "styles.xml");
        Rel("xl/workbook.xml", "rId3", "sharedStrings", "sharedStrings.xml");
        if (!Raw.ContainsKey("docProps/core.xml")) SetPart("docProps/core.xml", Decl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Plain</dc:title></cp:coreProperties>");
        SetPart("xl/workbook.xml", Decl + "<workbook xmlns=\"" + Ns + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"" + RootExtraNs + ">" + WorkbookBeforeSheets + "<sheets><sheet name=\"" + Esc(SheetName) + "\" sheetId=\"1\"" + SheetState + " r:id=\"rId1\"/></sheets>" + WorkbookAfterSheets + "</workbook>");
        SetPart("xl/worksheets/sheet1.xml", Decl + "<worksheet xmlns=\"" + Ns + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"" + RootExtraNs + SheetRootAttrs + ">" + SheetBeforeData + "<sheetData>" + Rows + "</sheetData>" + SheetAfterData + "</worksheet>");
        SetPart("xl/sharedStrings.xml", Decl + "<sst xmlns=\"" + Ns + "\" count=\"" + Shared.Count + "\" uniqueCount=\"" + Shared.Count + "\">" + string.Concat(Shared.Select(s => s.StartsWith("<si>") ? s : "<si><t xml:space=\"preserve\">" + Esc(s) + "</t></si>")) + "</sst>");
        SetPart("xl/styles.xml", StylesXml ?? Decl + "<styleSheet xmlns=\"" + Ns + "\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
    }
}

public sealed class Pp : OoxmlBuilder
{
    public const string PNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
    public const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public string Shapes = "";            // slide shapes
    public string SlideExtra = "";        // after cSld on the slide
    public string SlideCSldAttrs = "";
    public string MasterShapes = "";
    public string LayoutShapes = "";
    public string Theme;
    public string PresentationExtra = ""; // after notesSz
    public string RootAttrs = "";
    public string AfterMasterIdLst = "";

    public Pp()
    {
        Shapes = Text("Title", "Quarterly review", 1);
        Theme = DefaultTheme("Office");
    }

    public static string Text(string name, string text, int id) =>
        "<p:sp><p:nvSpPr><p:cNvPr id=\"" + (id + 1) + "\" name=\"" + Esc(name) + "\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\"/><a:t>" + Esc(text) + "</a:t></a:r></a:p></p:txBody></p:sp>";

    public static string Runs(string name, int id, params string[] runs) =>
        "<p:sp><p:nvSpPr><p:cNvPr id=\"" + (id + 1) + "\" name=\"" + Esc(name) + "\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p>" + string.Concat(runs.Select(r => "<a:r><a:rPr lang=\"en-US\"/><a:t>" + Esc(r) + "</a:t></a:r>")) + "</a:p></p:txBody></p:sp>";

    public static string DefaultTheme(string name) => Decl + "<a:theme xmlns:a=\"" + ANs + "\" name=\"" + Esc(name) + "\"><a:themeElements><a:clrScheme name=\"Office\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2><a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4><a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme><a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme><a:fmtScheme name=\"Office\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>";

    static string Tree(string shapes) => "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" + shapes + "</p:spTree>";

    protected override void Fill()
    {
        const string root = "xmlns:a=\"" + ANs + "\" xmlns:r=\"" + RNs + "\" xmlns:p=\"" + PNs + "\"";
        Overrides["/ppt/presentation.xml"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
        Overrides["/ppt/slideMasters/slideMaster1.xml"] = "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml";
        Overrides["/ppt/slideLayouts/slideLayout1.xml"] = "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml";
        Overrides["/ppt/slides/slide1.xml"] = "application/vnd.openxmlformats-officedocument.presentationml.slide+xml";
        Overrides["/ppt/theme/theme1.xml"] = "application/vnd.openxmlformats-officedocument.theme+xml";
        Overrides["/docProps/core.xml"] = "application/vnd.openxmlformats-package.core-properties+xml";
        Rel("", "rId1", "officeDocument", "ppt/presentation.xml");
        Rel("", "rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
        Rel("ppt/presentation.xml", "rId1", "slideMaster", "slideMasters/slideMaster1.xml");
        Rel("ppt/presentation.xml", "rId2", "slide", "slides/slide1.xml");
        Rel("ppt/presentation.xml", "rId3", "theme", "theme/theme1.xml");
        Rel("ppt/slideMasters/slideMaster1.xml", "rId1", "slideLayout", "../slideLayouts/slideLayout1.xml");
        Rel("ppt/slideMasters/slideMaster1.xml", "rId2", "theme", "../theme/theme1.xml");
        Rel("ppt/slideLayouts/slideLayout1.xml", "rId1", "slideMaster", "../slideMasters/slideMaster1.xml");
        Rel("ppt/slides/slide1.xml", "rId1", "slideLayout", "../slideLayouts/slideLayout1.xml");
        if (!Raw.ContainsKey("docProps/core.xml")) SetPart("docProps/core.xml", Decl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Plain</dc:title></cp:coreProperties>");
        SetPart("ppt/presentation.xml", Decl + "<p:presentation " + root + RootAttrs + "><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" + AfterMasterIdLst + "<p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/></p:sldIdLst><p:sldSz cx=\"9144000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>" + PresentationExtra + "</p:presentation>");
        SetPart("ppt/slideMasters/slideMaster1.xml", Decl + "<p:sldMaster " + root + "><p:cSld>" + Tree(MasterShapes) + "</p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/><p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst></p:sldMaster>");
        SetPart("ppt/slideLayouts/slideLayout1.xml", Decl + "<p:sldLayout " + root + " type=\"blank\"><p:cSld name=\"Blank\">" + Tree(LayoutShapes) + "</p:cSld></p:sldLayout>");
        SetPart("ppt/slides/slide1.xml", Decl + "<p:sld " + root + "><p:cSld" + SlideCSldAttrs + ">" + Tree(Shapes) + "</p:cSld>" + SlideExtra + "</p:sld>");
        SetPart("ppt/theme/theme1.xml", Theme);
    }
}


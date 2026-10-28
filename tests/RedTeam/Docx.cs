using System.Security;
using System.Text;

namespace RedTeam;

// Builds a small valid .docx (as raw zip) that attacks then modify.
public sealed class Docx
{
    public const string Ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" xmlns:w15=\"http://schemas.microsoft.com/office/word/2012/wordml\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" mc:Ignorable=\"w14 w15\"";
    public const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    public const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    public List<ZEntry> Entries = new();
    public Dictionary<string, string> Defaults = new() { ["rels"] = "application/vnd.openxmlformats-package.relationships+xml", ["xml"] = "application/xml" };
    public Dictionary<string, string> Overrides = new();
    public List<(string Id, string Type, string Target, bool External)> DocRels = new();
    public List<(string Id, string Type, string Target, bool External)> RootRels = new();
    public string BodyXml = "";
    public string RootNs = Ns;
    // Plain visible paragraph added at the end of the body (the app only creates mappings for values it can read).
    public string ExtraVisible = "";
    public string? DocumentOverride;
    public string ZipComment = "";
    public byte[]? DocumentRaw;
    public string? DocRelsOverride;
    public string? ContentTypesOverride;
    public string? RootRelsOverride;
    public Docx SetBytesRaw(string name, byte[] data) { if (name == "word/document.xml") DocumentRaw = data; else SetBytes(name, data); return this; }

    public const string MainCt = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    public Docx()
    {
        Overrides["/word/document.xml"] = MainCt;
        Overrides["/word/styles.xml"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml";
        Overrides["/word/settings.xml"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml";
        Overrides["/docProps/core.xml"] = "application/vnd.openxmlformats-package.core-properties+xml";
        RootRels.Add(("rId1", RelNs + "officeDocument", "word/document.xml", false));
        RootRels.Add(("rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml", false));
        DocRels.Add(("rId1", RelNs + "styles", "styles.xml", false));
        DocRels.Add(("rId2", RelNs + "settings", "settings.xml", false));
        Set("word/styles.xml", Decl + "<w:styles " + Ns + "><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style></w:styles>");
        Set("word/settings.xml", Decl + "<w:settings " + Ns + "><w:zoom w:percent=\"100\"/></w:settings>");
        Set("docProps/core.xml", Decl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\"><dc:title>Plain</dc:title></cp:coreProperties>");
        Body(Para("Quarterly operations summary. Nothing sensitive here."));
    }

    public static string Esc(string s) => SecurityElement.Escape(s) ?? "";
    public static string Run(string text, string rPr = "") => "<w:r>" + (rPr.Length > 0 ? "<w:rPr>" + rPr + "</w:rPr>" : "") + "<w:t xml:space=\"preserve\">" + Esc(text) + "</w:t></w:r>";
    public static string Para(string text, string pPr = "") => "<w:p>" + (pPr.Length > 0 ? "<w:pPr>" + pPr + "</w:pPr>" : "") + Run(text) + "</w:p>";
    public static string Paras(params string[] runs) => "<w:p>" + string.Concat(runs) + "</w:p>";

    public Docx Body(string inner) { BodyXml = inner; return this; }
    public Docx AddBody(string inner) { BodyXml += inner; return this; }

    public Docx Set(string name, string text, string? contentType = null) => SetBytes(name, new UTF8Encoding(false).GetBytes(text), contentType);
    public Docx SetBytes(string name, byte[] data, string? contentType = null)
    {
        var existing = Entries.FindIndex(e => e.Name == name);
        if (existing >= 0) Entries[existing].Data = data; else Entries.Add(new ZEntry { Name = name, Data = data });
        if (contentType != null) Overrides["/" + name] = contentType;
        return this;
    }
    public Docx Part(string name, string xml, string ct, string? relType = null, string? relId = null, string relBase = "word/")
    {
        Set(name, xml, ct);
        if (relType != null && name.StartsWith(relBase)) DocRels.Add((relId ?? "rId" + (10 + DocRels.Count), RelNs + relType, name.Substring(relBase.Length), false));
        return this;
    }
    public Docx Rel(string id, string type, string target, bool external = false) { DocRels.Add((id, type.Contains("://") ? type : RelNs + type, target, external)); return this; }
    public Docx RootRel(string id, string type, string target, bool external = false) { RootRels.Add((id, type.Contains("://") ? type : RelNs + type, target, external)); return this; }

    public string Document() => DocumentOverride ?? Decl + "<w:document " + RootNs + "><w:body>" + BodyXml + ExtraVisible + "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/></w:sectPr></w:body></w:document>";

    public static string RelsXml(IEnumerable<(string Id, string Type, string Target, bool External)> rels) =>
        Decl + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        string.Concat(rels.Select(r => "<Relationship Id=\"" + Esc(r.Id) + "\" Type=\"" + r.Type + "\" Target=\"" + Esc(r.Target) + "\"" + (r.External ? " TargetMode=\"External\"" : "") + "/>")) + "</Relationships>";

    public string ContentTypes() =>
        Decl + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        string.Concat(Defaults.Select(d => "<Default Extension=\"" + d.Key + "\" ContentType=\"" + d.Value + "\"/>")) +
        string.Concat(Overrides.Select(o => "<Override PartName=\"" + Esc(o.Key) + "\" ContentType=\"" + o.Value + "\"/>")) + "</Types>";

    public List<ZEntry> AllEntries()
    {
        var all = new List<ZEntry>
        {
            new() { Name = "[Content_Types].xml", Data = new UTF8Encoding(false).GetBytes(ContentTypesOverride ?? ContentTypes()) },
            new() { Name = "_rels/.rels", Data = new UTF8Encoding(false).GetBytes(RootRelsOverride ?? RelsXml(RootRels)) },
            new() { Name = "word/document.xml", Data = DocumentRaw ?? new UTF8Encoding(false).GetBytes(Document()) },
            new() { Name = "word/_rels/document.xml.rels", Data = new UTF8Encoding(false).GetBytes(DocRelsOverride ?? RelsXml(DocRels)) },
        };
        all.AddRange(Entries);
        return all;
    }

    public byte[] ToBytes() => RawZip.Write(AllEntries(), ZipComment);
}


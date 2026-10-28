using System.Text;
using static RedTeam.Sentinel;

namespace RedTeam;

public static partial class Catalog
{
    const string CoreNs = "xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"";
    const string CoreRel = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";

    static byte[] Jpeg(string embedded) =>
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xFE }
            .Concat(new[] { (byte)0, (byte)(embedded.Length + 2) }).Concat(Encoding.ASCII.GetBytes(embedded)).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();

    static void DocxMetadata()
    {
        Dx("meta/core-all-fields", "value in every core.xml property", d => d.Set("docProps/core.xml", Docx.Decl + "<cp:coreProperties " + CoreNs + "><dc:title>" + Person + "</dc:title><dc:subject>" + Company + "</dc:subject><dc:creator>" + Person + "</dc:creator><cp:keywords>" + Id + "</cp:keywords><dc:description>" + Person + "</dc:description><cp:lastModifiedBy>" + Person + "</cp:lastModifiedBy><cp:category>" + Company + "</cp:category><dc:identifier>" + Id + "</dc:identifier></cp:coreProperties>"));
        Dx("meta/core-extra-elements", "dc:publisher, dc:contributor, dc:source, dc:rights, dc:relation in core.xml", d => d.Set("docProps/core.xml", Docx.Decl + "<cp:coreProperties " + CoreNs + "><dc:title>t</dc:title><dc:publisher>" + Company + "</dc:publisher><dc:contributor>" + Person + "</dc:contributor><dc:source>" + Id + "</dc:source><dc:rights>" + Person + "</dc:rights><dc:relation>" + Company + "</dc:relation></cp:coreProperties>"));
        Dx("meta/core-xml-comment", "XML comment inside core.xml", d => d.Set("docProps/core.xml", Docx.Decl + "<cp:coreProperties " + CoreNs + "><!-- author: " + Person + " --><dc:title>t</dc:title></cp:coreProperties>"));
        Dx("meta/core-custom-attr", "unknown attribute on core.xml root", d => d.Set("docProps/core.xml", Docx.Decl + "<cp:coreProperties " + CoreNs + " xsi:note=\"" + Person + "\"><dc:title>t</dc:title></cp:coreProperties>"));
        Dx("meta/app-xml", "app.xml Company and Manager", d =>
        {
            d.Set("docProps/app.xml", Docx.Decl + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Company>" + Company + "</Company><Manager>" + Person + "</Manager><TitlesOfParts><vt:vector xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\" size=\"1\" baseType=\"lpstr\"><vt:lpstr>" + Id + "</vt:lpstr></vt:vector></TitlesOfParts></Properties>", "application/vnd.openxmlformats-officedocument.extended-properties+xml");
            d.RootRel("rId3", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties", "docProps/app.xml");
        });
        Dx("meta/custom-xml-props", "custom.xml property values", d =>
        {
            d.Set("docProps/custom.xml", Docx.Decl + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/custom-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\"><property fmtid=\"{D5CDD505-2E9C-101B-9397-08002B2CF9AE}\" pid=\"2\" name=\"" + Person + "\"><vt:lpwstr>" + Company + "</vt:lpwstr></property></Properties>", "application/vnd.openxmlformats-officedocument.custom-properties+xml");
            d.RootRel("rId3", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties", "docProps/custom.xml");
        });
        Dx("meta/thumbnail-exif", "thumbnail.jpeg whose comment segment holds the value", d =>
        {
            d.Defaults["jpeg"] = "image/jpeg";
            d.SetBytes("docProps/thumbnail.jpeg", Jpeg(Person));
            d.RootRel("rId3", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail", "docProps/thumbnail.jpeg");
        });
        Dx("meta/core-custom-namespace-uri", "xmlns declaration in core.xml with value as URI", d => d.Set("docProps/core.xml", Docx.Decl + "<cp:coreProperties " + CoreNs + " xmlns:z=\"urn:owner:" + Person.Replace(' ', '_') + "\"><dc:title>t</dc:title></cp:coreProperties>"));
    }

    static void DocxFields()
    {
        Dx("rels/hyperlink-url", "external hyperlink target holds the value", d => { d.Rel("rIdH1", "hyperlink", "https://example.test/people/" + Person.Replace(' ', '-'), true); d.Body(Docx.Para("see") + "<w:p><w:hyperlink r:id=\"rIdH1\"><w:r><w:t>link</w:t></w:r></w:hyperlink></w:p>"); });
        Dx("rels/hyperlink-mailto", "mailto target built from the value", d => { d.Rel("rIdH1", "hyperlink", "mailto:zelda.quimby-orsino@example.test?subject=" + Uri.EscapeDataString(Company), true); d.Body("<w:p><w:hyperlink r:id=\"rIdH1\"><w:r><w:t>mail</w:t></w:r></w:hyperlink></w:p>"); });
        Dx("rels/hyperlink-text-split", "hyperlink display text split across runs", d => { d.Rel("rIdH1", "hyperlink", "https://example.test/", true); d.Body("<w:p><w:hyperlink r:id=\"rIdH1\">" + Docx.Run("Zelda ") + Docx.Run("Quimby") + Docx.Run("-Orsino") + "</w:hyperlink></w:p>"); }, new[] { Person });
        Dx("rels/hyperlink-tooltip", "w:tooltip attribute on hyperlink", d => d.Body("<w:p><w:hyperlink w:anchor=\"bm1\" w:tooltip=\"" + Person + "\"><w:r><w:t>jump</w:t></w:r></w:hyperlink><w:bookmarkStart w:id=\"1\" w:name=\"bm1\"/><w:bookmarkEnd w:id=\"1\"/></w:p>"));
        Dx("rels/hyperlink-anchor-name", "internal anchor named with the id", d => d.Body("<w:p><w:bookmarkStart w:id=\"1\" w:name=\"" + Id + "\"/><w:r><w:t>t</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p><w:p><w:hyperlink w:anchor=\"" + Id + "\"><w:r><w:t>go</w:t></w:r></w:hyperlink></w:p>"));
        Dx("rels/internal-target-name", "relationship Target to a theme part whose file name holds the value (underscored)", d =>
        {
            d.Set("word/theme/Zelda_Quimby-Orsino.xml", Docx.Decl + "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Office\"><a:themeElements/></a:theme>", "application/vnd.openxmlformats-officedocument.theme+xml");
            d.Rel("rIdT", "theme", "theme/Zelda_Quimby-Orsino.xml");
        });
        Dx("rels/part-name-exact-unreferenced", "allowed-looking theme part whose zip entry name is the exact value, no override, no relationship", d =>
            d.SetBytes("word/theme/" + Person + ".xml", Encoding.UTF8.GetBytes(Docx.Decl + "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"x\"/>")));
        Dx("rels/rel-id-and-target", "relationship Id and Type attributes hold the value (to a dropped part)", d =>
        {
            d.SetBytes("word/media/p.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            d.Defaults["png"] = "image/png";
            d.Rel(Person.Replace(' ', '_'), "image", "media/p.png");
        });
        Dx("rels/content-types-override-name", "Content_Types override PartName with the value for a part that is dropped", d =>
        {
            d.SetBytes("word/printerSettings/" + Person.Replace(' ', '_') + ".bin", new byte[] { 1, 2, 3 }, "application/vnd.openxmlformats-officedocument.wordprocessingml.printerSettings");
        });

        // Field codes
        foreach (var (code, instr) in new[]
        {
            ("hyperlink", "HYPERLINK \"https://example.test/" + Id + "\""),
            ("includetext", "INCLUDETEXT \"C:\\Users\\" + Person + "\\notes.docx\""),
            ("docproperty", "DOCPROPERTY \"" + Person + "\""),
            ("mergefield", "MERGEFIELD \"" + Company + "\""),
            ("if", "IF \"" + Person + "\" = \"x\" \"yes\" \"no\""),
            ("ref", "REF \"" + Person.Replace(' ', '_') + "\""),
            ("seq", "SEQ " + Person.Replace(' ', '_') + " \\* ARABIC"),
            ("pageref", "PAGEREF " + Company.Replace(' ', '_')),
            ("toc", "TOC \\o \"1-3\" \\t \"" + Person + ",1\""),
            ("set", "SET " + Company.Replace(' ', '_') + " \"" + Person + "\""),
            ("askfield", "ASK name \"" + Person + "\""),
            ("author", "AUTHOR \"" + Person + "\""),
            ("info", "INFO Title \"" + Company + "\""),
            ("includepicture", "INCLUDEPICTURE \"https://example.test/" + Person + ".png\""),
            ("link", "LINK Excel.Sheet.12 \"C:\\" + Company + ".xlsx\" \"Sheet1!R1C1\""),
            ("autotext", "AUTOTEXT \"" + Person + "\""),
            ("styleref", "STYLEREF \"" + Person + "\""),
        })
            Dx("fields/complex-" + code, "complex field instruction " + code, d => d.Body("<w:p>" + Fld(instr) + "</w:p>"));
        Dx("fields/simple-docproperty", "fldSimple DOCPROPERTY", d => d.Body("<w:p><w:fldSimple w:instr=\" DOCPROPERTY &quot;" + Person + "&quot; \"><w:r><w:t>x</w:t></w:r></w:fldSimple></w:p>"));
        Dx("fields/simple-ref", "fldSimple REF with underscored name", d => d.Body("<w:p><w:fldSimple w:instr=\" REF " + Person.Replace(' ', '_') + " \"><w:r><w:t>x</w:t></w:r></w:fldSimple></w:p>"));
        Dx("fields/simple-seq", "fldSimple SEQ identifier (a kept structural field)", d => d.Body("<w:p><w:fldSimple w:instr=\" SEQ " + Company.Replace(' ', '_') + " \"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p>"));
        Dx("fields/nested-if-ref", "nested fields: IF containing REF", d => d.Body("<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> IF </w:instrText></w:r>" + Fld("REF \"" + Person + "\"", "a") + "<w:r><w:instrText xml:space=\"preserve\"> = \"1\" \"" + Company + "\" \"no\" </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>r</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>"));
        Dx("fields/split-instr", "instruction split over three instrText runs", d => d.Body("<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>MERGE</w:instrText></w:r><w:r><w:instrText>FIELD Zelda Quim</w:instrText></w:r><w:r><w:instrText>by-Orsino</w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>v</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>"));
        Dx("fields/delinstrtext", "w:delInstrText inside a tracked deletion", d => d.Body("<w:p><w:del w:id=\"1\" w:author=\"a\" w:date=\"2020-01-01T00:00:00Z\"><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:delInstrText>MERGEFIELD " + Person + "</w:delInstrText></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:del></w:p>"));
        Dx("fields/result-only-unmapped-case", "field result text in lower case caps", d => d.Body("<w:p>" + Fld("PAGE", Person.ToUpperInvariant()) + "</w:p>"));
        Dx("fields/instr-in-header", "MERGEFIELD in a header part", d =>
        {
            d.Part("word/header1.xml", Docx.Decl + "<w:hdr " + Docx.Ns + "><w:p>" + Fld("MERGEFIELD " + Person) + "</w:p></w:hdr>", "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml", "header", "rIdH");
            d.Body(Docx.Para("b") + "<w:p><w:pPr><w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdH\"/></w:sectPr></w:pPr></w:p>");
        });
        Dx("fields/formfield-default", "legacy form field default text and help", d => d.Body("<w:p><w:r><w:fldChar w:fldCharType=\"begin\"><w:ffData><w:name w:val=\"" + Person + "\"/><w:enabled/><w:textInput><w:default w:val=\"" + Company + "\"/></w:textInput><w:helpText w:type=\"text\" w:val=\"" + Id + "\"/><w:statusText w:type=\"text\" w:val=\"" + Person + "\"/></w:ffData></w:fldChar></w:r><w:r><w:instrText> FORMTEXT </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>v</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>"));
        Dx("fields/bookmark-derived-names", "bookmark names derived from the values (underscored)", d => d.Body("<w:p><w:bookmarkStart w:id=\"1\" w:name=\"Zelda_Quimby_Orsino\"/><w:r><w:t>a</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p><w:p><w:bookmarkStart w:id=\"2\" w:name=\"Vantablack_Holdings_Zeta_contract\"/><w:r><w:t>b</w:t></w:r><w:bookmarkEnd w:id=\"2\"/></w:p>"));
        Dx("fields/bookmark-exact-id", "bookmark whose name is exactly the id value", d => d.Body("<w:p><w:bookmarkStart w:id=\"1\" w:name=\"" + Id + "\"/><w:r><w:t>a</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p>"));
        Dx("fields/move-range-name", "moveFromRangeStart / moveToRangeStart names", d => d.Body("<w:p><w:moveFromRangeStart w:id=\"1\" w:name=\"" + Person.Replace(' ', '_') + "\" w:author=\"" + Person + "\" w:date=\"2020-01-01T00:00:00Z\"/><w:r><w:t>a</w:t></w:r><w:moveFromRangeEnd w:id=\"1\"/><w:moveToRangeStart w:id=\"2\" w:name=\"" + Person.Replace(' ', '_') + "\" w:author=\"" + Person + "\"/><w:r><w:t>b</w:t></w:r><w:moveToRangeEnd w:id=\"2\"/></w:p>"));
    }
}


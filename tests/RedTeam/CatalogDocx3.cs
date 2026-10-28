using System.Text;
using static RedTeam.Sentinel;

namespace RedTeam;

public static partial class Catalog
{
    // Root namespaces plus an extension namespace "x" that Word is told to ignore (mc:Ignorable), so the validator tolerates it.
    static string NsX => Docx.Ns.Replace("mc:Ignorable=\"w14 w15\"", "xmlns:x=\"urn:redteam:x\" mc:Ignorable=\"w14 w15 x\"");
    static string DocWith(string body, string? ns = null, string prolog = "") => Docx.Decl + prolog + "<w:document " + (ns ?? NsX) + "><w:body>" + body + "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/></w:sectPr></w:body></w:document>";

    static Attack Dxn(string name, string desc, Action<Docx> mod, string[]? values = null) => Dx(name, desc, d => { d.RootNs = NsX; mod(d); }, values);

    static void DocxXmlTricks()
    {
        Dx("xml/comment-in-document", "XML comment in document.xml", d => d.Body(Docx.Para("a") + "<!-- " + Person + " -->" + Docx.Para("b")));
        Dx("xml/comment-in-styles", "XML comment in styles.xml", d => d.Set("word/styles.xml", Docx.Decl + "<!-- owner " + Company + " --><w:styles " + Docx.Ns + "><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style></w:styles>"));
        Dx("xml/comment-in-settings", "XML comment in settings.xml", d => d.Set("word/settings.xml", Docx.Decl + "<w:settings " + Docx.Ns + "><!-- " + Id + " --><w:zoom w:percent=\"100\"/></w:settings>"));
        Dx("xml/comment-in-theme", "XML comment in theme part", d => d.Part("word/theme/theme1.xml", Docx.Decl + "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Office\"><!-- " + Person + " --><a:themeElements/></a:theme>", "application/vnd.openxmlformats-officedocument.theme+xml", "theme"));
        Dx("xml/comment-in-header", "XML comment in a header part", d =>
        {
            d.Part("word/header1.xml", Docx.Decl + "<w:hdr " + Docx.Ns + "><!-- " + Person + " --><w:p><w:r><w:t>h</w:t></w:r></w:p></w:hdr>", HdrCt, "header", "rIdH");
            d.Body(Docx.Para("b") + "<w:p><w:pPr><w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdH\"/></w:sectPr></w:pPr></w:p>");
        });
        Dx("xml/comment-in-rels", "XML comment in document.xml.rels", d => d.DocRelsOverride = Docx.RelsXml(d.DocRels).Replace("<Relationship ", "<!-- " + Person + " --><Relationship "));
        Dx("xml/comment-in-content-types", "XML comment inside [Content_Types].xml", d => d.ContentTypesOverride = d.ContentTypes().Replace("<Default ", "<!-- " + Company + " --><Default "));
        Dx("xml/comment-in-root-rels", "XML comment inside _rels/.rels", d => d.RootRelsOverride = Docx.RelsXml(d.RootRels).Replace("<Relationship ", "<!-- " + Id + " --><Relationship "));
        Dx("xml/comment-inside-text", "comment splits the text inside w:t", d => d.Body("<w:p><w:r><w:t>Zel<!-- x -->da Quimby-Or<!-- y -->sino</w:t></w:r></w:p>"), new[] { Person });
        Dx("xml/processing-instruction", "processing instruction in document.xml", d => d.Body(Docx.Para("a") + "<?note " + Person + "?>" + Docx.Para("b")));
        Dx("xml/pi-before-root", "processing instruction before the root element", d => d.DocumentOverride = Docx.Decl + "<?redteam " + Company + "?><w:document " + Docx.Ns + "><w:body>" + Docx.Para("a") + "<w:sectPr/></w:body></w:document>");
        Dx("xml/doctype-internal-entity", "DOCTYPE with an internal entity holding the value", d => d.DocumentOverride = DocWith(Docx.Para("a &z; b"), Docx.Ns, "<!DOCTYPE w:document [<!ENTITY z \"" + Person + "\">]>"));
        Dx("xml/doctype-entity-unused", "DOCTYPE with an unused internal entity holding the value", d => d.DocumentOverride = DocWith(Docx.Para("a"), Docx.Ns, "<!DOCTYPE w:document [<!ENTITY z \"" + Person + "\">]>"));
        Dx("xml/doctype-system-id", "DOCTYPE with a SYSTEM identifier holding the value", d => d.DocumentOverride = DocWith(Docx.Para("a"), Docx.Ns, "<!DOCTYPE w:document SYSTEM \"" + Under(Person) + ".dtd\">"));
        Dx("xml/doctype-external-entity", "external entity pointing at a local file (XXE style)", d => d.DocumentOverride = DocWith(Docx.Para("a &z; b"), Docx.Ns, "<!DOCTYPE w:document [<!ENTITY z SYSTEM \"file:///C:/Windows/win.ini\">]>"));
        Dxn("xml/cdata-split", "value split between text, CDATA and text in an unknown element", d => d.Body(Docx.Para("a") + "<x:note>Zel<![CDATA[da Quimby]]>-Orsino</x:note>"), new[] { Person });
        Dxn("xml/unknown-element-split-comment", "unknown element text split by a comment", d => d.Body(Docx.Para("a") + "<x:note>Zel<!---->da Quimby-Orsino</x:note>"), new[] { Person });
        Dxn("xml/unknown-element-text", "unknown extension element holding the exact value", d => d.Body(Docx.Para("a") + "<x:note>" + Person + "</x:note>"));
        Dxn("xml/unknown-element-entity", "unknown element text written with character references", d => d.Body(Docx.Para("a") + "<x:note>&#x5A;elda Quimby&#45;Orsino</x:note>"), new[] { Person });
        Dx("xml/namespace-uri", "namespace URI holds the value", d => d.DocumentOverride = DocWith(Docx.Para("a"), NsX.Replace("xmlns:x=\"urn:redteam:x\"", "xmlns:x=\"urn:redteam:" + Person + "\"")));
        Dx("xml/namespace-prefix", "namespace prefix derived from the value", d => d.DocumentOverride = DocWith(Docx.Para("a"), NsX + " xmlns:Zelda_Quimby=\"urn:redteam:y\""));
        Dxn("xml/element-name", "extension element named after the value", d => d.Body(Docx.Para("a") + "<x:Zelda_Quimby-Orsino/>"));
        Dxn("xml/attribute-name", "extension attribute named after the value", d => d.Body("<w:p x:Zelda_Quimby-Orsino=\"1\"><w:r><w:t>a</w:t></w:r></w:p>"));
        Dxn("xml/extension-attribute-value", "extension attribute holding the exact value", d => d.Body("<w:p x:note=\"" + Person + "\"><w:r><w:t>a</w:t></w:r></w:p>"));
        Dxn("xml/extension-attribute-split", "extension attribute holding entity encoded value", d => d.Body("<w:p x:note=\"&#90;elda&#32;Quimby-Orsino\"><w:r><w:t>a</w:t></w:r></w:p>"), new[] { Person });
        Dx("xml/xml-lang-attr", "xml:lang attribute with the value", d => d.Body("<w:p><w:r><w:t xml:lang=\"" + Person + "\">a</w:t></w:r></w:p>"));
        Dx("xml/rsid-attributes", "rsid style attributes with id-like value", d => d.Body("<w:p w:rsidR=\"00ABCDEF\" w:rsidRDefault=\"" + Id + "\"><w:r><w:t>a</w:t></w:r></w:p>"));
        Dx("xml/char-ref-visible", "visible text written with character references", d => d.Body("<w:p><w:r><w:t>&#x5A;elda Quimby&#x2D;Orsino</w:t></w:r></w:p>"), new[] { Person });
        Dx("xml/utf16-document", "document.xml encoded as UTF-16 with BOM", d => d.SetBytesRaw("word/document.xml", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-16\" standalone=\"yes\"?>" + DocWith(Docx.Para("see " + Person)).Substring(Docx.Decl.Length))).ToArray()));
        Dx("xml/utf8-double-bom", "document.xml with two UTF-8 byte order marks", d => d.SetBytesRaw("word/document.xml", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(new byte[] { 0xEF, 0xBB, 0xBF }).Concat(Encoding.UTF8.GetBytes(DocWith(Docx.Para("see " + Person)))).ToArray()));
        Dx("xml/alternatecontent-choice-fallback", "AlternateContent with the value in Choice and Fallback runs", d => d.Body("<w:p><mc:AlternateContent><mc:Choice Requires=\"w14\"><w:r><w:t>" + Person + "</w:t></w:r></mc:Choice><mc:Fallback><w:r><w:t>" + Company + "</w:t></w:r></mc:Fallback></mc:AlternateContent></w:p>"));
        Dx("xml/alternatecontent-fallback-only-variant", "AlternateContent where only the Fallback holds a case variant", d => d.Body("<w:p><mc:AlternateContent><mc:Choice Requires=\"w14\"><w:r><w:t>ok</w:t></w:r></mc:Choice><mc:Fallback><w:r><w:t>" + Person.ToUpperInvariant() + "</w:t></w:r></mc:Fallback></mc:AlternateContent></w:p>"));
        Dx("xml/smarttag", "smartTag with attributes", d => d.Body("<w:p><w:smartTag w:uri=\"urn:schemas-microsoft-com:office:smarttags\" w:element=\"" + Person + "\"><w:smartTagPr><w:attr w:name=\"Name\" w:val=\"" + Company + "\"/></w:smartTagPr><w:r><w:t>x</w:t></w:r></w:smartTag></w:p>"));
        Dx("xml/custom-xml-inline", "inline customXml with element and attributes", d => d.Body("<w:p><w:customXml w:uri=\"urn:x\" w:element=\"" + Under(Person) + "\"><w:customXmlPr><w:attr w:name=\"n\" w:val=\"" + Person + "\"/></w:customXmlPr><w:r><w:t>x</w:t></w:r></w:customXml></w:p>"));
        Dx("xml/perm-start-user", "permStart editing user", d => d.Body("<w:permStart w:id=\"1\" w:ed=\"" + Person + "\"/><w:p><w:r><w:t>x</w:t></w:r></w:p><w:permEnd w:id=\"1\"/>"));
        Dx("xml/hidden-vanish-run", "w:vanish hidden run", d => d.Body("<w:p><w:r><w:rPr><w:vanish/></w:rPr><w:t>" + Person + "</w:t></w:r></w:p>"));
        Dx("xml/specvanish-and-webhidden", "w:specVanish / w:webHidden runs", d => d.Body("<w:p><w:r><w:rPr><w:webHidden/></w:rPr><w:t>" + Company + "</w:t></w:r><w:r><w:rPr><w:specVanish/></w:rPr><w:t>" + Id + "</w:t></w:r></w:p>"));
        Dx("xml/caps-smallcaps", "w:caps and w:smallCaps formatted lower case text", d => d.Body("<w:p><w:r><w:rPr><w:caps/></w:rPr><w:t>zelda quimby-orsino</w:t></w:r><w:r><w:rPr><w:smallCaps/></w:rPr><w:t> vantablack holdings zeta</w:t></w:r></w:p>"));
        Dx("xml/rtl-bdo", "w:bdo with reversed text", d => d.Body("<w:p><w:bdo w:val=\"rtl\"><w:r><w:t>" + new string(Person.Reverse().ToArray()) + "</w:t></w:r></w:bdo></w:p>"), new[] { Person });
        Dx("xml/rtl-run-real-text", "w:rtl run holding the exact value", d => d.Body("<w:p><w:r><w:rPr><w:rtl/></w:rPr><w:t>" + Person + "</w:t></w:r></w:p>"));
        Dx("xml/text-in-sdt-run", "value inside sdt content in a table cell", d => d.Body("<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"3000\"/></w:tblGrid><w:tr><w:tc><w:tcPr><w:tcW w:w=\"3000\" w:type=\"dxa\"/></w:tcPr>" + Sdt("<w:alias w:val=\"a\"/>", Docx.Para(Person)) + "<w:p/></w:tc></w:tr></w:tbl><w:p/>"));
        Dx("xml/table-caption-description", "table caption and description", d => d.Body("<w:tbl><w:tblPr><w:tblCaption w:val=\"" + Person + "\"/><w:tblDescription w:val=\"" + Company + "\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"3000\"/></w:tblGrid><w:tr><w:tc><w:p><w:r><w:t>c</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p/>"));
        Dx("xml/table-caption-variant", "table caption, separator variant", d => d.Body("<w:tbl><w:tblPr><w:tblCaption w:val=\"" + Under(Person) + "\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"3000\"/></w:tblGrid><w:tr><w:tc><w:p><w:r><w:t>c</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p/>"));
        Dx("xml/tab-and-break-between", "tabs and line breaks between value parts", d => d.Body("<w:p><w:r><w:t>Zelda</w:t><w:tab/><w:t>Quimby-Orsino</w:t></w:r></w:p><w:p><w:r><w:t>Vantablack</w:t><w:br/><w:t>Holdings Zeta</w:t></w:r></w:p>"));
        Dx("xml/across-paragraphs", "value split across two paragraphs (detection limit)", d => d.Body(Docx.Para("Zelda") + Docx.Para("Quimby-Orsino")), new[] { Person });
    }
}


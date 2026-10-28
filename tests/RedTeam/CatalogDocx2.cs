using System.Text;
using static RedTeam.Sentinel;

namespace RedTeam;

public static partial class Catalog
{
    const string HdrCt = "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml";
    static string Under(string s) => s.Replace(' ', '_');

    static string Sdt(string pr, string content = "<w:p><w:r><w:t>content</w:t></w:r></w:p>") => "<w:sdt><w:sdtPr>" + pr + "<w:id w:val=\"12345\"/></w:sdtPr><w:sdtContent>" + content + "</w:sdtContent></w:sdt>";

    static void DocxControlsAndMarkup()
    {
        // Content controls
        Dx("sdt/alias-tag", "sdt alias and tag", d => d.Body(Sdt("<w:alias w:val=\"" + Person + "\"/><w:tag w:val=\"" + Company + "\"/>")));
        Dx("sdt/alias-tag-underscored", "sdt alias and tag, separator variant", d => d.Body(Sdt("<w:alias w:val=\"" + Under(Person) + "\"/><w:tag w:val=\"" + Under(Company) + "\"/>")));
        Dx("sdt/placeholder", "sdt placeholder docPart pointer and showingPlcHdr", d => d.Body(Sdt("<w:placeholder><w:docPart w:val=\"" + Person + "\"/></w:placeholder><w:showingPlcHdr/>")));
        Dx("sdt/databinding", "sdt dataBinding xpath and prefix mappings", d => d.Body(Sdt("<w:dataBinding w:prefixMappings=\"xmlns:ns0='urn:" + Person + "'\" w:xpath=\"/ns0:root[1]/ns0:name[1]\" w:storeItemID=\"{11111111-2222-3333-4444-555555555555}\"/>")));
        Dx("sdt/dropdown-items", "dropDownList listItem displayText and value", d => d.Body(Sdt("<w:dropDownList><w:listItem w:displayText=\"" + Person + "\" w:value=\"" + Company + "\"/></w:dropDownList>")));
        Dx("sdt/dropdown-items-variant", "dropDownList items, separator variant", d => d.Body(Sdt("<w:dropDownList><w:listItem w:displayText=\"" + Under(Person) + "\" w:value=\"" + Under(Company) + "\"/></w:dropDownList>")));
        Dx("sdt/combobox-items", "comboBox listItem", d => d.Body(Sdt("<w:comboBox><w:listItem w:displayText=\"" + Person + "\" w:value=\"" + Id + "\"/></w:comboBox>")));
        Dx("sdt/date-format", "date control format string with literal text", d => d.Body(Sdt("<w:date><w:dateFormat w:val=\"'" + Person + "' d MMM yyyy\"/><w:lid w:val=\"en-US\"/><w:storeMappedDataAs w:val=\"dateTime\"/><w:calendar w:val=\"gregorian\"/></w:date>")));
        Dx("sdt/date-format-variant", "date control format string, separator variant", d => d.Body(Sdt("<w:date><w:dateFormat w:val=\"'" + Under(Person) + "' d MMM yyyy\"/><w:lid w:val=\"en-US\"/><w:storeMappedDataAs w:val=\"dateTime\"/><w:calendar w:val=\"gregorian\"/></w:date>")));
        Dx("sdt/docpartobj", "docPartObj gallery and category", d => d.Body(Sdt("<w:docPartObj><w:docPartGallery w:val=\"" + Person + "\"/><w:docPartCategory w:val=\"" + Company + "\"/><w:docPartUnique/></w:docPartObj>")));
        Dx("sdt/checkbox-w14", "w14:checkbox symbols", d => d.Body(Sdt("<w14:checkbox><w14:checked w14:val=\"0\"/><w14:checkedState w14:val=\"2612\" w14:font=\"" + Person + "\"/><w14:uncheckedState w14:val=\"2610\" w14:font=\"" + Company + "\"/></w14:checkbox>")));
        Dx("sdt/inline-text-variants", "inline sdt with visible text and alias variants", d => d.Body("<w:p><w:r><w:t>Owner: </w:t></w:r><w:sdt><w:sdtPr><w:alias w:val=\"" + Person + "\"/><w:text/></w:sdtPr><w:sdtContent><w:r><w:t>" + Person + "</w:t></w:r></w:sdtContent></w:sdt></w:p>"));
        Dx("sdt/appearance-w15", "w15 sdt extensions", d => d.Body(Sdt("<w15:appearance w15:val=\"" + Person + "\"/><w15:color w:val=\"FF0000\"/>")));

        // Settings
        Dx("settings/docvars", "document variables", d => d.Set("word/settings.xml", Docx.Decl + "<w:settings " + Docx.Ns + "><w:docVars><w:docVar w:name=\"" + Person + "\" w:val=\"" + Company + "\"/></w:docVars></w:settings>"));
        Dx("settings/docvars-variant", "document variables, separator variant", d => d.Set("word/settings.xml", Docx.Decl + "<w:settings " + Docx.Ns + "><w:docVars><w:docVar w:name=\"" + Under(Person) + "\" w:val=\"" + Under(Company) + "\"/></w:docVars></w:settings>"));
        Dx("settings/attached-template", "attachedTemplate path with the value", d =>
        {
            d.Set("word/_rels/settings.xml.rels", Docx.RelsXml(new[] { ("rIdAT", Docx.RelNs + "attachedTemplate", "file:///C:/Users/" + Under(Person) + "/Templates/Normal.dotm", true) }));
            d.Set("word/settings.xml", Docx.Decl + "<w:settings " + Docx.Ns + "><w:attachedTemplate r:id=\"rIdAT\"/></w:settings>");
        });
        Dx("settings/mail-merge", "mailMerge data source and query", d => { d.Set("word/_rels/settings.xml.rels", Docx.RelsXml(new[] { ("rIdDS", Docx.RelNs + "mailMergeSource", "file:///C:/data/" + Under(Person) + ".xlsx", true) })); d.Set("word/settings.xml", Docx.Decl + "<w:settings " + Docx.Ns + "><w:mailMerge><w:mainDocumentType w:val=\"formLetters\"/><w:query w:val=\"SELECT * FROM " + Person + "\"/><w:dataSource r:id=\"rIdDS\"/></w:mailMerge></w:settings>"); });
        Dx("settings/captions-and-lang", "captions, listSeparator and themeFontLang with the value", d => d.Set("word/settings.xml", Docx.Decl + "<w:settings " + Docx.Ns + "><w:captions><w:caption w:name=\"" + Person + "\" w:pos=\"above\"/></w:captions><w:themeFontLang w:val=\"" + Company + "\"/></w:settings>"));
        Dx("settings/doc-protection-hash", "documentProtection attributes", d => d.Set("word/settings.xml", Docx.Decl + "<w:settings " + Docx.Ns + "><w:documentProtection w:edit=\"readOnly\" w:enforcement=\"1\" w:algorithmName=\"" + Person + "\" w:hashValue=\"" + Id + "\"/></w:settings>"));
        Dx("settings/schema-library", "schemaLibrary uri and manifest", d => d.Set("word/settings.xml", Docx.Decl + "<w:settings " + Docx.Ns + "><w:schemaLibrary><w:schema w:uri=\"urn:" + Person + "\" w:manifestLocation=\"" + Company + "\"/></w:schemaLibrary></w:settings>"));
        Dx("settings/rsid-variant", "rsids attribute abuse", d => d.Set("word/settings.xml", Docx.Decl + "<w:settings " + Docx.Ns + "><w:rsids><w:rsidRoot w:val=\"" + Id + "\"/></w:rsids></w:settings>"));
        Dx("settings/hdr-shape-defaults", "hdrShapeDefaults with alt text", d => d.Set("word/settings.xml", Docx.Decl + "<w:settings " + Docx.Ns + "><w:hdrShapeDefaults><o:shapedefaults v:ext=\"edit\" spidmax=\"1026\" alt=\"" + Person + "\"/></w:hdrShapeDefaults></w:settings>"));
        Dx("settings/web-settings", "webSettings with the value", d =>
            d.Part("word/webSettings.xml", Docx.Decl + "<w:webSettings " + Docx.Ns + "><w:encoding w:val=\"" + Person + "\"/><w:optimizeForBrowser/></w:webSettings>", "application/vnd.openxmlformats-officedocument.wordprocessingml.webSettings+xml", "webSettings"));
        Dx("settings/embedded-font-obfuscated", "fontTable embedded font with .odttf holding the value", d =>
        {
            d.Defaults["odttf"] = "application/vnd.openxmlformats-officedocument.obfuscatedFont";
            d.SetBytes("word/fonts/font1.odttf", Encoding.UTF8.GetBytes(Person + " font data"));
            d.Part("word/fontTable.xml", Docx.Decl + "<w:fonts " + Docx.Ns + "><w:font w:name=\"Calibri\"><w:embedRegular r:id=\"rIdF1\" w:fontKey=\"{00000000-0000-0000-0000-000000000001}\"/></w:font></w:fonts>", "application/vnd.openxmlformats-officedocument.wordprocessingml.fontTable+xml", "fontTable");
            d.Entries.Add(new ZEntry { Name = "word/_rels/fontTable.xml.rels", Data = Encoding.UTF8.GetBytes(Docx.Decl + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdF1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/font\" Target=\"fonts/font1.odttf\"/></Relationships>") });
        });
        Dx("settings/font-name", "fontTable font name and altName with the value", d =>
            d.Part("word/fontTable.xml", Docx.Decl + "<w:fonts " + Docx.Ns + "><w:font w:name=\"" + Under(Person) + "\"><w:altName w:val=\"" + Under(Company) + "\"/></w:font></w:fonts>", "application/vnd.openxmlformats-officedocument.wordprocessingml.fontTable+xml", "fontTable"));
        Dx("styles/name-exact", "style name exactly the value", d => d.Set("word/styles.xml", Docx.Decl + "<w:styles " + Docx.Ns + "><w:style w:type=\"paragraph\" w:styleId=\"Custom1\"><w:name w:val=\"" + Person + "\"/></w:style></w:styles>"));
        Dx("styles/style-id-variant", "custom style id and name derived from the value", d => d.Set("word/styles.xml", Docx.Decl + "<w:styles " + Docx.Ns + "><w:style w:type=\"paragraph\" w:styleId=\"" + Under(Person) + "\"><w:name w:val=\"" + Under(Company) + "_Heading\"/><w:basedOn w:val=\"Normal\"/></w:style></w:styles>"));
        Dx("styles/alias-element", "style w:aliases and w:link", d => d.Set("word/styles.xml", Docx.Decl + "<w:styles " + Docx.Ns + "><w:style w:type=\"paragraph\" w:styleId=\"S1\"><w:name w:val=\"S1\"/><w:aliases w:val=\"" + Person + "," + Company + "\"/></w:style></w:styles>"));
        Dx("styles/latent-name", "latentStyles lsdException name", d => d.Set("word/styles.xml", Docx.Decl + "<w:styles " + Docx.Ns + "><w:latentStyles w:defLockedState=\"0\"><w:lsdException w:name=\"" + Person + "\"/></w:latentStyles></w:styles>"));
        Dx("theme/name-and-fonts", "theme name and font typeface hold the value (exact)", d => d.Part("word/theme/theme1.xml", Docx.Decl + "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"" + Person + "\"><a:themeElements><a:fontScheme name=\"" + Company + "\"><a:majorFont><a:latin typeface=\"" + Id + "\"/></a:majorFont></a:fontScheme></a:themeElements></a:theme>", "application/vnd.openxmlformats-officedocument.theme+xml", "theme"));
        Dx("theme/name-variant", "theme name and font typeface, separator variant", d => d.Part("word/theme/theme1.xml", Docx.Decl + "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"" + Under(Person) + "\"><a:themeElements><a:fontScheme name=\"" + Under(Company) + "\"><a:majorFont><a:latin typeface=\"" + Under(Person) + "\"/></a:majorFont></a:fontScheme></a:themeElements></a:theme>", "application/vnd.openxmlformats-officedocument.theme+xml", "theme"));
        Dx("numbering/lvltext", "list number text holds the value", d => d.Part("word/numbering.xml", Docx.Decl + "<w:numbering " + Docx.Ns + "><w:abstractNum w:abstractNumId=\"0\"><w:name w:val=\"" + Person + "\"/><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"" + Person + " %1\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml", "numbering"));
        Dx("numbering/style-link", "numbering numStyleLink / styleLink / lvl pStyle with the value", d => d.Part("word/numbering.xml", Docx.Decl + "<w:numbering " + Docx.Ns + "><w:abstractNum w:abstractNumId=\"0\"><w:styleLink w:val=\"" + Under(Person) + "\"/><w:lvl w:ilvl=\"0\"><w:pStyle w:val=\"" + Under(Company) + "\"/></w:lvl></w:abstractNum></w:numbering>", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml", "numbering"));

        // Comments and people
        const string CommentsCt = "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml";
        Dx("comments/text-author-initials", "comment text, author and initials", d =>
        {
            d.Part("word/comments.xml", Docx.Decl + "<w:comments " + Docx.Ns + "><w:comment w:id=\"0\" w:author=\"" + Person + "\" w:initials=\"ZQO\" w:date=\"2020-01-01T00:00:00Z\"><w:p><w:r><w:t>" + Company + " " + Id + "</w:t></w:r></w:p></w:comment></w:comments>", CommentsCt, "comments");
            d.Body("<w:p><w:commentRangeStart w:id=\"0\"/><w:r><w:t>anchored</w:t></w:r><w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r></w:p>");
        });
        Dx("comments/extended-ids-people", "commentsExtended, commentsIds, people.xml", d =>
        {
            d.Part("word/comments.xml", Docx.Decl + "<w:comments " + Docx.Ns + "><w:comment w:id=\"0\" w:author=\"" + Person + "\"><w:p><w:r><w:t>c</w:t></w:r></w:p></w:comment></w:comments>", CommentsCt, "comments");
            d.Part("word/people.xml", Docx.Decl + "<w15:people " + Docx.Ns + "><w15:person w15:author=\"" + Person + "\"><w15:presenceInfo w15:providerId=\"AD\" w15:userId=\"S::" + Under(Person) + "@example.test::1\"/></w15:person></w15:people>", "application/vnd.openxmlformats-officedocument.wordprocessingml.people+xml", "people");
            d.Part("word/commentsExtended.xml", Docx.Decl + "<w15:commentsEx " + Docx.Ns + "><w15:commentEx w15:paraId=\"00000001\" w15:done=\"0\"/></w15:commentsEx>", "application/vnd.openxmlformats-officedocument.wordprocessingml.commentsExtended+xml", "commentsExtended");
            d.Part("word/commentsIds.xml", Docx.Decl + "<w16cid:commentsIds xmlns:w16cid=\"http://schemas.microsoft.com/office/word/2016/wordml/cid\"><w16cid:commentId w16cid:paraId=\"00000001\" w16cid:durableId=\"" + Id + "\"/></w16cid:commentsIds>", "application/vnd.openxmlformats-officedocument.wordprocessingml.commentsIds+xml", "commentsIds");
            d.Body("<w:p><w:commentRangeStart w:id=\"0\"/><w:r><w:t>anchored</w:t></w:r><w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r></w:p>");
        });
        Dx("comments/comment-in-body-text", "visible text inside the comment range", d => d.Body("<w:p><w:commentRangeStart w:id=\"0\"/>" + Docx.Run("Contact " + Person) + "<w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r></w:p>"));
        Dx("comments/orphan-comments-part", "comments.xml present but nothing references it", d => d.Part("word/comments.xml", Docx.Decl + "<w:comments " + Docx.Ns + "><w:comment w:id=\"9\" w:author=\"" + Person + "\"><w:p><w:r><w:t>" + Company + "</w:t></w:r></w:p></w:comment></w:comments>", CommentsCt, "comments"));
        Dx("comments/renamed-part-path", "comments content stored under a non-standard part name", d => d.Part("word/Comments_Zelda.xml", Docx.Decl + "<w:comments " + Docx.Ns + "><w:comment w:id=\"9\" w:author=\"x\"><w:p><w:r><w:t>" + Person + "</w:t></w:r></w:p></w:comment></w:comments>", CommentsCt, "comments"));

        // Tracked changes
        string Tc(string inner) => "<w:p>" + inner + "</w:p>";
        Dx("tracked/ins-author-text", "w:ins with author and visible inserted text", d => d.Body(Tc("<w:ins w:id=\"1\" w:author=\"" + Person + "\" w:date=\"2020-01-01T00:00:00Z\"><w:r><w:t>" + Company + "</w:t></w:r></w:ins>")));
        Dx("tracked/del-text-and-author", "w:del with delText and author", d => d.Body(Tc("<w:del w:id=\"1\" w:author=\"" + Person + "\" w:date=\"2020-01-01T00:00:00Z\"><w:r><w:delText>" + Person + "</w:delText></w:r></w:del>" + Docx.Run("kept"))));
        Dx("tracked/move-from-to", "moveFrom and moveTo", d => d.Body(Tc("<w:moveFrom w:id=\"1\" w:author=\"" + Person + "\"><w:r><w:t>" + Person + "</w:t></w:r></w:moveFrom><w:moveTo w:id=\"2\" w:author=\"" + Person + "\"><w:r><w:t>" + Company + "</w:t></w:r></w:moveTo>")));
        Dx("tracked/rpr-change", "rPrChange with author and old run properties (rFonts name)", d => d.Body(Tc("<w:r><w:rPr><w:b/><w:rPrChange w:id=\"1\" w:author=\"" + Person + "\" w:date=\"2020-01-01T00:00:00Z\"><w:rPr><w:rFonts w:ascii=\"" + Under(Company) + "\"/></w:rPr></w:rPrChange></w:rPr><w:t>x</w:t></w:r>")));
        Dx("tracked/ppr-change", "pPrChange with author", d => d.Body("<w:p><w:pPr><w:pStyle w:val=\"Normal\"/><w:pPrChange w:id=\"1\" w:author=\"" + Person + "\" w:date=\"2020-01-01T00:00:00Z\"><w:pPr><w:pStyle w:val=\"" + Under(Person) + "\"/></w:pPr></w:pPrChange></w:pPr><w:r><w:t>x</w:t></w:r></w:p>"));
        Dx("tracked/tblpr-change", "tblPrChange and tblGridChange", d => d.Body("<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblPrChange w:id=\"1\" w:author=\"" + Person + "\"><w:tblPr><w:tblCaption w:val=\"" + Company + "\"/></w:tblPr></w:tblPrChange></w:tblPr><w:tblGrid><w:gridCol w:w=\"1000\"/></w:tblGrid><w:tr><w:tc><w:tcPr><w:tcW w:w=\"1000\" w:type=\"dxa\"/></w:tcPr><w:p><w:r><w:t>c</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p/>"));
        Dx("tracked/paragraph-mark-ins", "inserted paragraph mark with author", d => d.Body("<w:p><w:pPr><w:rPr><w:ins w:id=\"1\" w:author=\"" + Person + "\" w:date=\"2020-01-01T00:00:00Z\"/></w:rPr></w:pPr><w:r><w:t>x</w:t></w:r></w:p>"));
        Dx("tracked/section-change", "sectPrChange", d => d.Body("<w:p><w:r><w:t>x</w:t></w:r></w:p><w:p><w:pPr><w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:sectPrChange w:id=\"1\" w:author=\"" + Person + "\"><w:sectPr/></w:sectPrChange></w:sectPr></w:pPr></w:p>"));
        Dx("tracked/numbering-change", "numberingChange original string", d => d.Body("<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/><w:numberingChange w:id=\"1\" w:author=\"" + Person + "\" w:original=\"" + Company + "\"/></w:numPr></w:pPr><w:r><w:t>x</w:t></w:r></w:p>"));
        Dx("tracked/ins-in-del", "inserted text nested in a deletion", d => d.Body(Tc("<w:del w:id=\"1\" w:author=\"a\"><w:ins w:id=\"2\" w:author=\"" + Person + "\"><w:r><w:delText>" + Person + "</w:delText></w:r></w:ins></w:del>")));
        Dx("tracked/cell-changes", "tracked table cell insertion (documented as blocked)", d => d.Body("<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"1000\"/></w:tblGrid><w:tr><w:tc><w:tcPr><w:cellIns w:id=\"1\" w:author=\"" + Person + "\"/></w:tcPr><w:p><w:r><w:t>c</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p/>"));

        // Footnotes, endnotes
        Dx("footnotes/text-and-author", "footnote text, separator footnote and a tracked author", d =>
        {
            d.Part("word/footnotes.xml", Docx.Decl + "<w:footnotes " + Docx.Ns + "><w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote><w:footnote w:id=\"1\"><w:p><w:r><w:t>" + Person + "</w:t></w:r><w:ins w:id=\"5\" w:author=\"" + Person + "\"><w:r><w:t> " + Company + "</w:t></w:r></w:ins></w:p></w:footnote></w:footnotes>", "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml", "footnotes");
            d.Body("<w:p><w:r><w:t>text</w:t></w:r><w:r><w:footnoteReference w:id=\"1\"/></w:r></w:p>");
        });
        Dx("footnotes/endnotes", "endnote text", d =>
        {
            d.Part("word/endnotes.xml", Docx.Decl + "<w:endnotes " + Docx.Ns + "><w:endnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:endnote><w:endnote w:id=\"1\"><w:p><w:r><w:t>" + Id + "</w:t></w:r></w:p></w:endnote></w:endnotes>", "application/vnd.openxmlformats-officedocument.wordprocessingml.endnotes+xml", "endnotes");
            d.Body("<w:p><w:r><w:t>text</w:t></w:r><w:r><w:endnoteReference w:id=\"1\"/></w:r></w:p>");
        });
        foreach (var kind in new[] { "first", "even" })
            Dx("headers/" + kind + "-page", kind + " page header and footer", d =>
            {
                d.Part("word/header2.xml", Docx.Decl + "<w:hdr " + Docx.Ns + ">" + Docx.Para(Person) + "</w:hdr>", HdrCt, "header", "rIdH2");
                d.Part("word/footer2.xml", Docx.Decl + "<w:ftr " + Docx.Ns + ">" + Docx.Para(Company) + "</w:ftr>", "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml", "footer", "rIdF2");
                d.Body(Docx.Para("b") + "<w:p><w:pPr><w:sectPr><w:headerReference w:type=\"" + kind + "\" r:id=\"rIdH2\"/><w:footerReference w:type=\"" + kind + "\" r:id=\"rIdF2\"/><w:titlePg/></w:sectPr></w:pPr></w:p>");
            });
        Dx("headers/unreferenced-header", "header part not referenced by any section", d => d.Part("word/header7.xml", Docx.Decl + "<w:hdr " + Docx.Ns + ">" + Docx.Para(Person) + "</w:hdr>", HdrCt));
        Dx("headers/unreferenced-header-no-override", "header part without content type override", d => d.SetBytes("word/header8.xml", Encoding.UTF8.GetBytes(Docx.Decl + "<w:hdr " + Docx.Ns + ">" + Docx.Para(Person) + "</w:hdr>")));
    }
}


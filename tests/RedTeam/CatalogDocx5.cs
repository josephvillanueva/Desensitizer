using System.Text;
using static RedTeam.Sentinel;

namespace RedTeam;

public static partial class Catalog
{
    // Disguises of the person value in visible body text. The mapping holds the canonical spelling only.
    static IEnumerable<(string Key, string Text, string Why)> PersonDisguises()
    {
        yield return ("zero-width-space", "Zel​da Quimby-Orsino", "U+200B inside the first name");
        yield return ("zero-width-nonjoiner", "Zelda Quimby‌-Orsino", "U+200C before the hyphen");
        yield return ("zero-width-joiner", "Zelda Qui‍mby-Orsino", "U+200D inside the surname");
        yield return ("word-joiner", "Zelda⁠ Quimby-Orsino", "U+2060 after the first name");
        yield return ("bom-inside", "Zel﻿da Quimby-Orsino", "U+FEFF inside the first name");
        yield return ("soft-hyphen", "Zel­da Quimby-Or­sino", "U+00AD soft hyphens");
        yield return ("lrm-rlm", "Zelda‎ Quimby-‏Orsino", "left and right marks");
        yield return ("bidi-override", "‮onisrO-ybmiuQ adleZ‬", "U+202E right to left override with reversed letters (renders as the value)");
        yield return ("variation-selector", "Zelda️ Quimby-Orsino", "U+FE0F variation selector");
        yield return ("cyrillic-e", "Zеlda Quimby-Orsino", "Cyrillic small ie in Zelda");
        yield return ("cyrillic-multi", "Zеldа Quimby-Пrsino".Replace("П", "О"), "Cyrillic a, e and O");
        yield return ("greek-zeta-omicron", "Ζelda Quimby-Orsinο", "Greek capital Zeta and small omicron");
        yield return ("fullwidth", "Ｚｅｌｄａ Ｑｕｉｍｂｙ－Ｏｒｓｉｎｏ", "fullwidth letters");
        yield return ("combining-acute", "Zeĺda Quimby-Orsino", "combining acute on e");
        yield return ("precomposed-accent", "Zélda Quimby-Orsino", "accented e");
        yield return ("nbsp", "Zelda Quimby-Orsino", "no-break space");
        yield return ("em-space", "Zelda Quimby-Orsino", "em space");
        yield return ("ideographic-space", "Zelda　Quimby-Orsino", "ideographic space");
        yield return ("thin-space", "Zelda Quimby-Orsino", "thin space");
        yield return ("hyphen-U2010", "Zelda Quimby‐Orsino", "U+2010 hyphen");
        yield return ("nb-hyphen-U2011", "Zelda Quimby‑Orsino", "U+2011 non-breaking hyphen");
        yield return ("en-dash", "Zelda Quimby–Orsino", "en dash");
        yield return ("em-dash", "Zelda Quimby—Orsino", "em dash");
        yield return ("minus-sign", "Zelda Quimby−Orsino", "minus sign");
        yield return ("fullwidth-hyphen", "Zelda Quimby－Orsino", "fullwidth hyphen minus");
        yield return ("space-for-hyphen", "Zelda Quimby Orsino", "space instead of hyphen");
        yield return ("no-hyphen", "Zelda QuimbyOrsino", "hyphen removed");
        yield return ("underscore", "Zelda_Quimby-Orsino", "underscore instead of space");
        yield return ("spaced-letters", "Z e l d a  Q u i m b y - O r s i n o", "letters spaced out");
        yield return ("dotted", "Z.e.l.d.a Quimby-Orsino", "dots between letters");
        yield return ("surname-first", "Quimby-Orsino, Zelda", "surname first");
        yield return ("reversed-letters", "onisrO-ybmiuQ adleZ", "reversed letters (no bidi mark)");
        yield return ("kelvin-long-s", "Zelda Quimby-Orſino", "long s in Orsino");
        yield return ("double-space", "Zelda  Quimby-Orsino", "two spaces (should still match)");
        yield return ("lowercase", "zelda quimby-orsino", "lower case (should still match)");
        yield return ("uppercase", "ZELDA QUIMBY-ORSINO", "upper case (should still match)");
        yield return ("mixed-case", "zELDA qUIMBY-oRSINO", "mixed case (should still match)");
        yield return ("trailing-possessive", "Zelda Quimby-Orsino's", "possessive (should still match)");
        yield return ("embedded-in-word", "XZelda Quimby-OrsinoX", "glued to letters (value boundary)");
        yield return ("leading-digits", "1Zelda Quimby-Orsino2", "glued to digits (value boundary)");
        yield return ("first-name-only", "Zelda", "first name alone (alias, not the mapped value): expected Blocked by the fragment guard");
        yield return ("surname-only", "Quimby-Orsino", "surname alone: expected Blocked by the fragment guard");
        yield return ("tag-chars", "Zelda " + string.Concat("Quimby-Orsino".Select(c => char.ConvertFromUtf32(0xE0000 + c))) + "Quimby-Orsino", "Unicode tag characters (invisible) beside surname");
    }

    static void DocxUnicode()
    {
        foreach (var (key, text, why) in PersonDisguises())
            Dx("unicode/person-" + key, "person disguised: " + why, d => d.Body(Docx.Para("Contact " + text + " today.")), new[] { Person });
        Dx("unicode/word-nobreakhyphen-element", "w:noBreakHyphen element between Quimby and Orsino", d => d.Body("<w:p><w:r><w:t>Zelda Quimby</w:t><w:noBreakHyphen/><w:t>Orsino</w:t></w:r></w:p>"), new[] { Person });
        Dx("unicode/word-softhyphen-element", "w:softHyphen element inside Quimby", d => d.Body("<w:p><w:r><w:t>Zelda Quim</w:t><w:softHyphen/><w:t>by-Orsino</w:t></w:r></w:p>"), new[] { Person });
        Dx("unicode/word-sym-hyphen", "w:sym character used as the hyphen", d => d.Body("<w:p><w:r><w:t>Zelda Quimby</w:t><w:sym w:font=\"Symbol\" w:char=\"F02D\"/><w:t>Orsino</w:t></w:r></w:p>"), new[] { Person });
        Dx("unicode/word-ptab", "w:ptab between first and last name", d => d.Body("<w:p><w:r><w:t>Zelda</w:t><w:ptab w:relativeTo=\"margin\" w:alignment=\"left\" w:leader=\"none\"/><w:t>Quimby-Orsino</w:t></w:r></w:p>"), new[] { Person });
        Dx("unicode/word-lastrenderedpagebreak", "lastRenderedPageBreak inside the name", d => d.Body("<w:p><w:r><w:t>Zelda Quimby-</w:t><w:lastRenderedPageBreak/><w:t>Orsino</w:t></w:r></w:p>"), new[] { Person });
        Dx("unicode/word-run-per-character", "one run per character", d => d.Body("<w:p>" + string.Concat(Person.Select(c => Docx.Run(c.ToString()))) + "</w:p>"), new[] { Person });
        Dx("unicode/word-run-with-field-between", "a PAGE field result sits inside the name", d => d.Body("<w:p>" + Docx.Run("Zelda Quimby-") + Fld("PAGE", "") + Docx.Run("Orsino") + "</w:p>"), new[] { Person });
        Dx("unicode/word-bookmark-between-runs", "bookmark boundaries and proofErr between runs", d => d.Body("<w:p><w:r><w:t>Zelda </w:t></w:r><w:bookmarkStart w:id=\"1\" w:name=\"_GoBack\"/><w:proofErr w:type=\"spellStart\"/><w:r><w:t>Quimby-</w:t></w:r><w:bookmarkEnd w:id=\"1\"/><w:r><w:t>Orsino</w:t></w:r></w:p>"), new[] { Person });
        Dx("unicode/word-ins-del-split", "tracked deletion and insertion split the name", d => d.Body("<w:p><w:r><w:t>Zelda </w:t></w:r><w:del w:id=\"1\" w:author=\"a\"><w:r><w:delText>Smith</w:delText></w:r></w:del><w:ins w:id=\"2\" w:author=\"a\"><w:r><w:t>Quimby-</w:t></w:r></w:ins><w:r><w:t>Orsino</w:t></w:r></w:p>"), new[] { Person });
        Dx("unicode/word-run-in-hyperlink-and-sdt", "name split across a hyperlink, a smartTag and an sdt run", d => d.Body("<w:p><w:r><w:t>Zelda </w:t></w:r><w:smartTag w:uri=\"u\" w:element=\"e\"><w:r><w:t>Quimby-</w:t></w:r></w:smartTag><w:sdt><w:sdtPr><w:alias w:val=\"a\"/></w:sdtPr><w:sdtContent><w:r><w:t>Orsino</w:t></w:r></w:sdtContent></w:sdt></w:p>"), new[] { Person });
        Dx("unicode/word-textbox-halves", "first name in the paragraph, surname inside a text box in the same paragraph", d => d.Body("<w:p><w:r><w:t>Zelda </w:t></w:r><w:r><w:pict><v:shape><v:textbox><w:txbxContent><w:p><w:r><w:t>Quimby-Orsino</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>"), new[] { Person });

        // Company and id disguises
        foreach (var (key, text, why) in new[]
        {
            ("company-hyphens", "Vantablack-Holdings-Zeta", "hyphens instead of spaces"),
            ("company-underscores", "Vantablack_Holdings_Zeta", "underscores instead of spaces"),
            ("company-no-spaces", "VantablackHoldingsZeta", "no spaces"),
            ("company-comma", "Vantablack Holdings, Zeta", "comma inside"),
            ("company-paren", "Vantablack Holdings (Zeta)", "parentheses around the last word"),
            ("company-zero-width", "Vantablack​ Holdings Zeta", "zero-width space"),
            ("company-kelvin", "VantablacK Holdings Zeta", "Kelvin sign for k"),
            ("company-cyrillic", "Vаntаblack Holdings Zeta", "Cyrillic a twice"),
            ("company-acronym", "VHZ", "acronym (alias, not the mapped value)"),
            ("company-short", "Vantablack", "short name alone: expected Blocked by the fragment guard"),
            ("company-possessive", "Vantablack Holdings Zeta's", "possessive (should still match)"),
            ("id-nb-hyphens", "ZQ‑5150‑0099", "non-breaking hyphens"),
            ("id-en-dashes", "ZQ–5150–0099", "en dashes"),
            ("id-spaces", "ZQ 5150 0099", "spaces instead of hyphens"),
            ("id-no-separators", "ZQ51500099", "no separators"),
            ("id-letter-o", "ZQ-5150-OO99", "letter O for zero (different value)"),
            ("id-lowercase", "zq-5150-0099", "lower case (should still match)"),
            ("id-zero-width", "ZQ-5150​-0099", "zero-width space"),
            ("id-fullwidth-digits", "ZQ-５１５０-００９９", "fullwidth digits"),
            ("id-in-url", "https://example.test/acct/ZQ-5150-0099/view", "inside a URL (should be replaced)"),
            ("id-percent-encoded-url", "https://example.test/acct/%5A%51-5150-0099", "percent encoded letters inside a URL"),
            ("id-base64", "token=" + Convert.ToBase64String(Encoding.UTF8.GetBytes("ZQ-5150-0099")), "base64 of the value"),
            ("id-hex", "id=" + Convert.ToHexString(Encoding.UTF8.GetBytes("ZQ-5150-0099")), "hex of the value"),
            ("person-base64", "ref=" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Zelda Quimby-Orsino")), "base64 of the person"),
        })
        {
            var value = key.StartsWith("company") ? Company : key.StartsWith("person") ? Person : Id;
            Dx("unicode/" + key, "disguise: " + why, d => d.Body(Docx.Para("Ref " + text + " end.")), new[] { value });
        }
    }

    static void DocxZip()
    {
        byte[] Base(Action<Docx>? mod = null) { var d = new Docx(); mod?.Invoke(d); return d.ToBytes(); }
        Add("zip/comment", "zip archive comment holds the value", ".docx", () => { var d = new Docx(); d.ZipComment = Person + " " + Company; return d.ToBytes(); });
        Add("zip/comment-binary", "zip comment holds the value in UTF-16", ".docx", () => RawZip.Write(new Docx().AllEntries(), "", Encoding.Unicode.GetBytes(Person)));
        Add("zip/entry-comment", "per-entry comment holds the value", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); e[2].EntryComment = Person; return RawZip.Write(e); });
        Add("zip/extra-field-central", "central directory extra field holds the value", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); e[2].ExtraCentral = new byte[] { 0x99, 0x99, (byte)Person.Length, 0 }.Concat(Encoding.UTF8.GetBytes(Person)).ToArray(); return RawZip.Write(e); });
        Add("zip/extra-field-local", "local header extra field holds the value", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); e[2].ExtraLocal = new byte[] { 0x99, 0x99, (byte)Company.Length, 0 }.Concat(Encoding.UTF8.GetBytes(Company)).ToArray(); return RawZip.Write(e); });
        Add("zip/duplicate-entry", "two word/document.xml entries, one benign, one with the value", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); var evil = new ZEntry { Name = "word/document.xml", Data = Encoding.UTF8.GetBytes(DocWith(Docx.Para("see " + Person))) }; e.Add(evil); return RawZip.Write(e); });
        Add("zip/duplicate-entry-first-evil", "duplicate word/document.xml, the value in the first copy", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); e.Insert(0, new ZEntry { Name = "word/document.xml", Data = Encoding.UTF8.GetBytes(DocWith(Docx.Para("see " + Person))) }); return RawZip.Write(e); });
        Add("zip/case-variant-name", "Word/Document.xml beside word/document.xml", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); e.Add(new ZEntry { Name = "Word/Document.xml", Data = Encoding.UTF8.GetBytes(DocWith(Docx.Para("see " + Person))) }); return RawZip.Write(e); });
        Add("zip/backslash-name", "word\\document.xml style names", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); e.Add(new ZEntry { Name = "word\\media\\" + Under(Person) + ".xml", Data = Encoding.UTF8.GetBytes("<a>" + Person + "</a>") }); return RawZip.Write(e); });
        Add("zip/dotdot-name", "path traversal entry name", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); e.Add(new ZEntry { Name = "word/../../" + Under(Person) + ".xml", Data = Encoding.UTF8.GetBytes("<a/>") }); return RawZip.Write(e); });
        Add("zip/entry-name-exact-value", "an extra entry whose name is the exact value, not allow-listed", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); e.Add(new ZEntry { Name = "word/" + Person + ".txt", Data = new byte[] { 1 } }); return RawZip.Write(e); });
        Add("zip/entry-name-in-folder", "a folder entry whose name is the value", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); e.Add(new ZEntry { Name = "word/" + Person + "/", Data = Array.Empty<byte>(), Deflate = false }); return RawZip.Write(e); });
        Add("zip/entry-name-utf16-odd", "entry name bytes are not valid UTF-8 (value in Latin-1 without the UTF-8 flag)", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); e.Add(new ZEntry { Name = "x", RawName = Encoding.Latin1.GetBytes("word/" + Under(Person) + ".bin"), Utf8Name = false, Data = new byte[] { 1 } }); return RawZip.Write(e); });
        Add("zip/data-descriptor", "entries written with data descriptors (valid, value in body)", ".docx", () => { var d = new Docx(); d.Body(Docx.Para("see " + Person)); var e = d.AllEntries(); foreach (var x in e) x.DataDescriptor = true; return RawZip.Write(e); });
        Add("zip/zip64-entries", "entries written with ZIP64 extra fields (valid, value in body)", ".docx", () => { var d = new Docx(); d.Body(Docx.Para("see " + Person)); var e = d.AllEntries(); foreach (var x in e) x.Zip64 = true; return RawZip.Write(e); });
        Add("zip/stored-uncompressed", "entries stored without compression", ".docx", () => { var d = new Docx(); d.Body(Docx.Para("see " + Person)); var e = d.AllEntries(); foreach (var x in e) x.Deflate = false; return RawZip.Write(e); });
        Add("zip/prefix-junk", "bytes before the archive start hold the value", ".docx", () => Encoding.UTF8.GetBytes(Person + "\n").Concat(new Docx().ToBytes()).ToArray());
        Add("zip/trailing-junk", "bytes after the end of central directory hold the value", ".docx", () => new Docx().ToBytes().Concat(Encoding.UTF8.GetBytes(Person)).ToArray());
        Add("zip/content-type-macro-enabled", "macroEnabled content type for the main part, no vba part", ".docx", () => { var d = new Docx(); d.Overrides["/word/document.xml"] = "application/vnd.ms-word.document.macroEnabled.main+xml"; d.Body(Docx.Para("see " + Person)); return d.ToBytes(); });
        Add("zip/rtf-named-docx", "an RTF file named .docx holding the value", ".docx", () => Encoding.UTF8.GetBytes("{\\rtf1\\ansi " + Person + "}"));
        Add("zip/empty-file", "zero-byte file named .docx", ".docx", () => Array.Empty<byte>());
        Add("zip/ole-compound-doc", "legacy .doc (OLE header) named .docx holding the value", ".docx", () => new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.Concat(Encoding.Unicode.GetBytes(Person)).ToArray());
        Add("zip/zip-bomb-ratio", "5 MB of zeros in one entry (ratio check)", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); e.Add(new ZEntry { Name = "word/media/bomb.bin", Data = new byte[5 * 1024 * 1024] }); return RawZip.Write(e); });
        Add("zip/many-entries", "4100 tiny entries (entry count limit)", ".docx", () => { var d = new Docx(); var e = d.AllEntries(); for (var i = 0; i < 4100; i++) e.Add(new ZEntry { Name = "word/media/e" + i + ".bin", Data = new byte[] { 1 }, Deflate = false }); return RawZip.Write(e); });
        Add("zip/lying-size", "declared uncompressed size smaller than the real data", ".docx", () =>
        {
            var d = new Docx(); d.Body(Docx.Para("see " + Person));
            var bytes = d.ToBytes();
            // Corrupt the central directory sizes of the first entry (offset search for the signature).
            var sig = new byte[] { 0x50, 0x4B, 0x01, 0x02 };
            var idx = bytes.AsSpan().IndexOf(sig);
            if (idx >= 0) { bytes[idx + 24] = 4; bytes[idx + 25] = 0; bytes[idx + 26] = 0; bytes[idx + 27] = 0; }
            return bytes;
        });
    }
}


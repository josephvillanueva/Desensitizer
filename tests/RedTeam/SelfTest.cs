using System.Text;
using static RedTeam.Sentinel;

namespace RedTeam;

// The scanner must catch what it claims to catch. Without this, every PASS above could be a blind spot.
public static class SelfTest
{
    public static (bool Passed, List<string> Lines) Run()
    {
        var lines = new List<string>();
        var ok = true;
        void Check(string name, bool passed) { lines.Add((passed ? "PASS " : "FAIL ") + "selftest " + name); if (!passed) ok = false; }

        bool Hits(string text) => Scanner.TextHas(text);
        Check("plain value", Hits("x " + Person + " y"));
        Check("lower case", Hits(Person.ToLowerInvariant()));
        Check("zero width split", Hits("Zel​da Quimby-Orsino"));
        Check("soft hyphen", Hits("Zel­da Quimby-Orsino"));
        Check("cyrillic homoglyph", Hits("Zеlda Quimby-Orsino"));
        Check("fullwidth", Hits("Ｚｅｌｄａ Quimby"));
        Check("xml entity", Hits("&#x5A;elda &amp; Quimby&#45;Orsino"));
        Check("split across tags", Hits("<w:t>Zel</w:t><w:t>da Quim</w:t><w:t>by-Orsino</w:t>"));
        Check("percent encoded", Hits("a=Zelda%20Quimby-Orsino"));
        Check("base64", Hits(Convert.ToBase64String(Encoding.UTF8.GetBytes(Id))));
        Check("hex", Hits(Convert.ToHexString(Encoding.UTF8.GetBytes(Company))));
        Check("fragment", Hits("only Quimby here"));
        Check("id fragment", Hits("5150-0099"));
        Check("reversed", Hits(new string(Person.Reverse().ToArray())));
        Check("binary-decoded invalid Unicode retains sentinel", Hits("\uD800\uFFFF\uFFFE"+Person));
        Check("clean text is not flagged", !Hits("Quarterly operations summary. [PERSON_001] approved [ORGANIZATION_002]."));

        // Container checks: comment, entry name and extra field of a real zip.
        var dir = Path.Combine(Harness.WorkRoot, "selftest");
        Directory.CreateDirectory(dir);
        string Write(string name, byte[] data) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, data); return p; }
        var plain = new Docx(); plain.Body(Docx.Para("x"));
        Check("clean docx is not flagged", Scanner.ScanFile(Write("clean.docx", plain.ToBytes())).Count == 0);
        var withComment = new Docx { ZipComment = Person };
        Check("zip comment found", Scanner.ScanFile(Write("c.docx", withComment.ToBytes())).Count > 0);
        var named = new Docx(); named.SetBytes("word/media/" + Id + ".bin", new byte[] { 1 });
        Check("entry name found", Scanner.ScanFile(Write("n.docx", named.ToBytes())).Count > 0);
        var body = new Docx(); body.Body(Docx.Para("see " + Person));
        Check("compressed body found", Scanner.ScanFile(Write("b.docx", body.ToBytes())).Count > 0);
        var utf16 = new Docx(); utf16.SetBytes("word/x.bin", Encoding.Unicode.GetBytes(Company));
        Check("utf-16 entry found", Scanner.ScanFile(Write("u.docx", utf16.ToBytes())).Count > 0);
        var nested = new Docx(); nested.SetBytes("word/embeddings/p.docx", body.ToBytes());
        Check("nested zip found", Scanner.ScanFile(Write("z.docx", nested.ToBytes())).Count > 0);
        var extra = new Docx(); var e = extra.AllEntries(); e[0].ExtraCentral = new byte[] { 0x99, 0x99, (byte)Person.Length, 0 }.Concat(Encoding.UTF8.GetBytes(Person)).ToArray();
        Check("central extra field found", Scanner.ScanFile(Write("x.docx", RawZip.Write(e))).Count > 0);
        try { Directory.Delete(dir, true); } catch { }
        return (ok, lines);
    }
}


using static RedTeam.Sentinel;

namespace RedTeam;

// The attack catalog. Each attack builds one hostile file from fictional values only.
// A Known entry marks a leak that is real and not fixed yet: the test stays, prints "LEAK RT-xxx", and does not fail the suite.
public static partial class Catalog
{
    static readonly List<Attack> list = new();
    static bool built;

    public static List<Attack> All()
    {
        if (built) return list;
        built = true;
        Controls();
        DocxMetadata();
        DocxFields();
        DocxControlsAndMarkup();
        DocxXmlTricks();
        DocxParts();
        DocxUnicode();
        DocxZip();
        Xlsx();
        Pptx();
        TextFormats();
        Flows();
        ApplyKnown();
        return list;
    }

    static Attack Add(string name, string desc, string ext, Func<byte[]> build, string[]? values = null, Known? known = null)
    {
        var a = new Attack { Name = name, Group = name.Split('/')[0], Description = desc, Ext = ext, Build = build, Values = values ?? Full };
        list.Add(a);
        return a;
    }

    static Attack Dx(string name, string desc, Action<Docx> mod, string[]? values = null)
    {
        var a = Add("docx/" + name, desc, ".docx", () => { var d = new Docx(); mod(d); return d.ToBytes(); }, values);
        a.BuildVisible = () =>
        {
            var d = new Docx(); mod(d);
            d.ExtraVisible = Docx.Para("Reference: " + string.Join(" | ", a.Values));
            return d.ToBytes();
        };
        return a;
    }

    // ---- helpers for Word markup ----
    public static string Fld(string instr, string result = "x") =>
        "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> " + Docx.Esc(instr) + " </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>" + Docx.Esc(result) + "</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";

    static void Controls()
    {
        // Positive controls: the sentinel in plain visible text must be replaced (Clean), in every format.
        Dx("control/visible-person", "visible sentinel person in body", d => d.Body(Docx.Para("Please contact " + Person + " about the contract.")));
        Dx("control/visible-all-three", "all three sentinels in one paragraph", d => d.Body(Docx.Para(Person + " of " + Company + " holds account " + Id + ".")));
        Dx("control/visible-split-runs", "sentinel split over four runs", d => d.Body(Docx.Paras(Docx.Run("Zel"), Docx.Run("da Quim"), Docx.Run("by-Or"), Docx.Run("sino"))), new[] { Person });
        Dx("control/header-footer", "sentinel in header and footer (visible parts)", d =>
        {
            d.Part("word/header1.xml", Docx.Decl + "<w:hdr " + Docx.Ns + ">" + Docx.Para(Person) + "</w:hdr>", "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml", "header", "rIdH");
            d.Part("word/footer1.xml", Docx.Decl + "<w:ftr " + Docx.Ns + ">" + Docx.Para(Company) + "</w:ftr>", "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml", "footer", "rIdF");
            d.Body(Docx.Para("body") + "<w:p><w:pPr><w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdH\"/><w:footerReference w:type=\"default\" r:id=\"rIdF\"/></w:sectPr></w:pPr></w:p>");
        });
    }
}


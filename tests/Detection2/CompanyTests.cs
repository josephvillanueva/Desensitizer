using Desensitizer;

public delegate void Chk(string name, bool ok, string? detail = null);

// Company alias consolidation. Every positive case has a negative case: a wrong merge (one token for two
// different companies) is worse than a missed alias.
public sealed class Doc
{
    public Project P = Project.New("Detection2", "test only");

    public Mapping Add(string value, string category = "ORGANIZATION", string file = "d1", string explanation = "")
        => P.EnsureMapping(value, category, true, 1, "Rules", explanation, file);

    public Mapping Get(string value, string category = "ORGANIZATION") => P.Mappings.First(m => m.Category == category && m.OriginalValue == value);
    public string Tok(string value, string category = "ORGANIZATION") => Get(value, category).Token;
    public void Run() => P.ConsolidatePersonAliases();
    public bool Same(string a, string b) => Tok(a) == Tok(b);
    public bool AllDistinct(params string[] values) => values.Select(v => Tok(v)).Distinct().Count() == values.Length;
    public bool NoTokenSharedByDifferentValues()
        => P.Mappings.GroupBy(m => m.Category + "|" + m.Token).All(g => g.Count() == 1);
}

public static class CompanyTests
{
    private static string Intro(string name, bool quoted = false) => "Acronym printed beside a detected organization. " + CompanyIdentity.NoteFor(name, quoted);

    public static void Run(Chk check)
    {
        LegalSuffixes(check);
        Acronyms(check);
        Stems(check);
        NeverMerge(check);
        Stability(check);
        EndToEnd(check);
    }

    private static void LegalSuffixes(Chk c)
    {
        var d = new Doc();
        d.Add("Acme Holdings Inc."); d.Add("Acme Holdings Inc"); d.Add("Acme Holdings Corporation"); d.Add("Acme Holdings Corp.");
        d.Run();
        c("(b) Punctuation-only difference reuses the token", d.Same("Acme Holdings Inc.", "Acme Holdings Inc"));
        c("(b) Corp and Corporation are one kind of suffix", d.Same("Acme Holdings Corporation", "Acme Holdings Corp."));
        c("(b) Inc and Corp on one name stay separate entities", !d.Same("Acme Holdings Inc.", "Acme Holdings Corp."));

        d = new Doc();
        d.Add("Kestrelbrook Energy LLC"); d.Add("Kestrelbrook Energy Ltd.");
        d.Run();
        c("(b) Different legal suffixes with full suffixes stay separate", d.AllDistinct("Kestrelbrook Energy LLC", "Kestrelbrook Energy Ltd."));

        d = new Doc();
        d.Add("Velmora Foods Inc."); d.Add("Velmora Foods");
        d.Run();
        c("(b) A bare form joins the only full form in the document", d.Same("Velmora Foods", "Velmora Foods Inc."));

        d = new Doc();
        d.Add("Velmora Foods Inc."); d.Add("Velmora Foods LLC"); d.Add("Velmora Foods");
        d.Run();
        c("(b) A bare form with two full forms joins neither", d.AllDistinct("Velmora Foods Inc.", "Velmora Foods LLC", "Velmora Foods"));

        d = new Doc();
        d.Add("Velmora Foods Inc.", file: "a"); d.Add("Velmora Foods", file: "b");
        d.Run();
        c("(b) A bare form in another document is not linked", !d.Same("Velmora Foods", "Velmora Foods Inc."));

        d = new Doc();
        d.Add("Velmora Foods Inc.", file: "a"); d.Add("Velmora Foods LLC", file: "b");
        d.Add("Velmora Foods", file: "a"); d.Add("Velmora Foods", file: "b");
        d.Run();
        c("(b) A bare form found in two documents with different full forms joins neither", d.AllDistinct("Velmora Foods Inc.", "Velmora Foods LLC", "Velmora Foods"));

        d = new Doc();
        d.Add("Global Inc."); d.Add("Global");
        d.Run();
        c("(b) A common single word is never linked to a full form", !d.Same("Global", "Global Inc."));

        d = new Doc();
        d.Add("Brightmoor Inc."); d.Add("Brightmoor"); d.Add("Brightmoor Holdings LLC");
        d.Run();
        c("(b) A bare stem that fits two longer names is not linked", d.AllDistinct("Brightmoor Inc.", "Brightmoor", "Brightmoor Holdings LLC"));

        d = new Doc();
        d.Add("Brightmoor Inc."); d.Add("Brightmoor");
        d.Run();
        c("(b) Single word bare form joins its only full form", d.Same("Brightmoor", "Brightmoor Inc."));

        d = new Doc();
        d.Add("Acme Holdings Inc."); d.Add("Acme Holdings Inc.", "VENDOR");
        d.Run();
        c("(d) The same name in another category keeps its own token", d.Tok("Acme Holdings Inc.") != d.Tok("Acme Holdings Inc.", "VENDOR")
            && d.Tok("Acme Holdings Inc.", "VENDOR").StartsWith("[VENDOR_"));

        c("Consolidation never creates mappings", d.P.Mappings.Count == 2);
    }

    private static void Acronyms(Chk c)
    {
        var d = new Doc();
        d.Add("Acme Holdings Corporation"); d.Add("AHC", explanation: Intro("Acme Holdings Corporation"));
        d.Run();
        c("(a) Acronym matching the initials joins the introduced name", d.Same("AHC", "Acme Holdings Corporation"));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("BRG", explanation: Intro("Brightmoor Logistics Inc."));
        d.Run();
        c("(a) An explicit parenthetical definition joins even without matching initials", d.Same("BRG", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("BRG");
        d.Run();
        c("(a) Without introduction evidence the acronym is not linked", !d.Same("BRG", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Acme Holdings Corporation"); d.Add("AHC");
        d.Run();
        c("(a) Matching initials alone, without an introduction, are not linked", !d.Same("AHC", "Acme Holdings Corporation"));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("USA", explanation: Intro("Brightmoor Logistics Inc."));
        d.Run();
        c("(a) A country code in parentheses is not an acronym", !d.Same("USA", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("UAE", explanation: Intro("Brightmoor Logistics Inc."));
        d.Run();
        c("(a) A region code in parentheses is not an acronym", !d.Same("UAE", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("XY", explanation: Intro("Brightmoor Logistics Inc."));
        d.Run();
        c("(a) A two letter unquoted term that is not the initials is not linked", !d.Same("XY", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("BLX", explanation: Intro("Brightmoor Logistics Inc.", true));
        d.Run();
        c("(a) A quoted defined short name joins the introduced name", d.Same("BLX", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("Tallowgate Foods Ltd.");
        d.Add("XYZ", explanation: Intro("Brightmoor Logistics Inc.") + " " + CompanyIdentity.NoteFor("Tallowgate Foods Ltd.", false));
        d.Run();
        c("(a) An acronym introduced for two different names joins neither", d.AllDistinct("Brightmoor Logistics Inc.", "Tallowgate Foods Ltd.", "XYZ"));

        d = new Doc();
        d.Add("AHC", explanation: Intro("Acme Holdings Corporation"));
        d.Run();
        c("(a) An introduction naming an undetected company links nothing and creates nothing", d.P.Mappings.Count == 1);

        d = new Doc();
        d.Add("Acme Holdings Corporation", file: "a"); d.Add("AHC", file: "b", explanation: Intro("Acme Holdings Corporation"));
        d.Run();
        c("(a) The introduced name must be in the same document", !d.Same("AHC", "Acme Holdings Corporation"));

        d = new Doc();
        d.Add("Acme Holdings Corporation"); d.Add("Acme Holdings Corp."); d.Add("AHC", explanation: Intro("Acme Holdings Corporation"));
        d.Run();
        c("(a) The acronym reaches every spelling of the introduced name", d.Same("AHC", "Acme Holdings Corp."));

        d = new Doc();
        d.Add("Acme Holdings Corporation"); d.Add("AHC", explanation: Intro("Acme Holdings Corporation"));
        d.Get("AHC").RequiresContextReview = true;
        d.Run();
        c("(d) A RequiresContextReview acronym is not merged", !d.Same("AHC", "Acme Holdings Corporation"));

        d = new Doc();
        d.Add("Acme Holdings Corporation"); d.Add("AHC", "VENDOR", explanation: Intro("Acme Holdings Corporation"));
        d.Run();
        c("(d) An acronym in another category is not merged", d.Tok("AHC", "VENDOR").StartsWith("[VENDOR_") && d.Tok("AHC", "VENDOR") != d.Tok("Acme Holdings Corporation"));
    }

    private static void Stems(Chk c)
    {
        var d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("Brightmoor");
        d.Run();
        c("(c) A unique stem short form joins the longer name", d.Same("Brightmoor", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("Brightmoor Holdings Ltd."); d.Add("Brightmoor");
        d.Run();
        c("(c) A stem shared by two companies joins neither", d.AllDistinct("Brightmoor Logistics Inc.", "Brightmoor Holdings Ltd.", "Brightmoor"));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("Brightmoor Holdings Ltd.");
        d.Run();
        c("(d) Two full names that share a first word stay separate", d.AllDistinct("Brightmoor Logistics Inc.", "Brightmoor Holdings Ltd."));

        d = new Doc();
        d.Add("Brightmoor Logistics"); d.Add("Brightmoor Holdings"); d.Add("Brightmoor");
        d.Run();
        c("(d) Suffix-free names that share a first word stay separate", d.AllDistinct("Brightmoor Logistics", "Brightmoor Holdings", "Brightmoor"));

        d = new Doc();
        d.Add("Acme Treasury Services Inc."); d.Add("Acme");
        d.Run();
        c("(c) A stem under five letters is not linked", !d.Same("Acme", "Acme Treasury Services Inc."));

        d = new Doc();
        d.Add("Global Logistics Inc."); d.Add("Global");
        d.Run();
        c("(c) A common word stem is not linked", !d.Same("Global", "Global Logistics Inc."));

        d = new Doc();
        d.Add("Golden Harbor Inc."); d.Add("Golden");
        d.Run();
        c("(c) A common first word on its own is not linked", !d.Same("Golden", "Golden Harbor Inc."));

        d = new Doc();
        d.Add("Python Works Inc."); d.Add("Python");
        d.Run();
        c("(c) A public technology stem is not linked", !d.Same("Python", "Python Works Inc."));

        d = new Doc();
        d.Add("Blue Harbor Logistics Inc."); d.Add("Blue Harbor");
        d.Run();
        c("(c) A two word stem joins when it is unique", d.Same("Blue Harbor", "Blue Harbor Logistics Inc."));

        d = new Doc();
        d.Add("Blue Harbor Logistics Inc."); d.Add("Blue Harbor Energy Corp."); d.Add("Blue Harbor");
        d.Run();
        c("(c) A two word stem shared by two companies joins neither", d.AllDistinct("Blue Harbor Logistics Inc.", "Blue Harbor Energy Corp.", "Blue Harbor"));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc.", file: "a"); d.Add("Brightmoor", file: "b");
        d.Run();
        c("(c) A short form in another document is not linked", !d.Same("Brightmoor", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc.", file: "a"); d.Add("Brightmoor Holdings Ltd.", file: "b"); d.Add("Brightmoor", file: "a"); d.Add("Brightmoor", file: "b");
        d.Run();
        c("(c) One short form used in two documents for different companies joins neither", d.AllDistinct("Brightmoor Logistics Inc.", "Brightmoor Holdings Ltd.", "Brightmoor"));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("Brightmoor", "VENDOR");
        d.Run();
        c("(d) A short form in another category is not linked", d.Tok("Brightmoor", "VENDOR") != d.Tok("Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("Brightmoor");
        d.Get("Brightmoor").RequiresContextReview = true;
        d.Run();
        c("(d) A RequiresContextReview short form is not merged", !d.Same("Brightmoor", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("Brightmoor");
        d.Get("Brightmoor Logistics Inc.").RequiresContextReview = true;
        d.Run();
        c("(d) A short form is not merged into a RequiresContextReview name", !d.Same("Brightmoor", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("Brightmoor Holdings Ltd."); d.Add("Brightmoor");
        d.Get("Brightmoor Holdings Ltd.").RequiresContextReview = true;
        d.Run();
        c("(d) A blocked candidate still counts as a second candidate", !d.Same("Brightmoor", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("Brightmoor");
        d.Get("Brightmoor").IsManual = true;
        d.Run();
        c("(d) A manual mapping is never merged", !d.Same("Brightmoor", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("Brightmoor");
        d.Get("Brightmoor").Ignored = true;
        d.Run();
        c("(d) A kept-as-is mapping is never merged", !d.Same("Brightmoor", "Brightmoor Logistics Inc."));
    }

    private static void NeverMerge(Chk c)
    {
        var d = new Doc();
        d.Add("Maria Santos", "PERSON"); d.Add("Santos", "PERSON"); d.Add("Santos Logistics Inc."); d.Add("Santos");
        d.Run();
        c("A person and a company are never merged",
            d.Tok("Maria Santos", "PERSON").StartsWith("[PERSON_") && d.Tok("Santos", "PERSON").StartsWith("[PERSON_")
            && d.Tok("Santos", "PERSON") != d.Tok("Maria Santos", "PERSON") && d.Tok("Santos").StartsWith("[ORGANIZATION_"));

        d = new Doc();
        d.Add("Tallowgate Foods Inc."); d.Add("Harborlight Foods Inc."); d.Add("Foods");
        d.Run();
        c("Companies sharing only a later word stay separate", d.AllDistinct("Tallowgate Foods Inc.", "Harborlight Foods Inc."));

        d = new Doc();
        d.Add("Treasury Services Inc."); d.Add("Treasury Services");
        d.Run();
        c("A generic descriptive name is not linked to a suffix form", !d.Same("Treasury Services", "Treasury Services Inc."));

        d = new Doc();
        d.Add("Quillfeather Capital Inc."); d.Add("Quillfeather Capital LLC"); d.Add("Quillfeather Capital Inc");
        d.Run();
        c("Punctuation-only variants share one token while the other suffix stays apart",
            d.Same("Quillfeather Capital Inc.", "Quillfeather Capital Inc") && !d.Same("Quillfeather Capital Inc.", "Quillfeather Capital LLC"));
    }

    private static void Stability(Chk c)
    {
        var d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("Brightmoor");
        d.Run();
        var first = d.Tok("Brightmoor Logistics Inc.");
        d.Run();
        c("Running the consolidation twice changes nothing", d.Tok("Brightmoor Logistics Inc.") == first && d.Same("Brightmoor", "Brightmoor Logistics Inc."));

        // A second company with the same stem shows up later: the earlier merge must be undone.
        d.Add("Brightmoor Holdings Ltd.");
        d.Run();
        c("A later second candidate splits an earlier short-form merge", d.AllDistinct("Brightmoor Logistics Inc.", "Brightmoor Holdings Ltd.", "Brightmoor"));
        c("No token is shared by two different values after the split", d.NoTokenSharedByDifferentValues());

        // Rescan path: the acronym already exists in the project and the introduction is read from the document text.
        d = new Doc();
        d.Add("Acme Holdings Corporation", file: "a"); d.Add("AHC", file: "a");
        d.P.RefreshOccurrenceEvidence("a", "Acme Holdings Corporation (AHC) is a vendor. AHC pays on time.");
        d.Run();
        c("Rescan: an introduction found in the text links the acronym", d.Same("AHC", "Acme Holdings Corporation"));

        d = new Doc();
        d.Add("Acme Holdings Corporation", file: "a"); d.Add("AHC", file: "a");
        d.P.RefreshOccurrenceEvidence("a", "Acme Holdings Corporation pays AHC on time. (AHC) is mentioned alone.");
        d.Run();
        c("Rescan: an acronym that is not printed right after the name is not linked", !d.Same("AHC", "Acme Holdings Corporation"));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc.", file: "a"); d.Add("Tallowgate Foods Ltd.", file: "a"); d.Add("XYZ", file: "a");
        d.P.RefreshOccurrenceEvidence("a", "Brightmoor Logistics Inc. (XYZ) and later Tallowgate Foods Ltd. (XYZ).");
        d.Run();
        c("Rescan: the same acronym introduced after two different names links to neither", d.AllDistinct("Brightmoor Logistics Inc.", "Tallowgate Foods Ltd.", "XYZ"));

        var persons = new Doc();
        persons.Add("Maria T. Santos", "PERSON"); persons.Add("Santos, Maria T.", "PERSON"); persons.Add("Brightmoor Logistics Inc."); persons.Add("Brightmoor");
        persons.Run();
        c("Person consolidation still runs and is unaffected", persons.Tok("Maria T. Santos", "PERSON") == persons.Tok("Santos, Maria T.", "PERSON") && persons.Same("Brightmoor", "Brightmoor Logistics Inc."));
    }

    private static void EndToEnd(Chk C)
    {
        const string text = "This agreement is made with Acme Holdings Corporation (AHC) for the refund service. AHC shall pay within 30 days. Brightmoor Logistics Inc. (\"BLI\") and Brightmoor Logistics Inc. are the same party. See also Velmora Foods Inc. (USA).";
        var found = new List<Candidate>
        {
            new() { Value = "Acme Holdings Corporation", Category = "ORGANIZATION", Confidence = 0.9, Source = "Local AI" },
            new() { Value = "Brightmoor Logistics Inc.", Category = "ORGANIZATION", Confidence = 0.9, Source = "Local AI" },
            new() { Value = "Velmora Foods Inc.", Category = "ORGANIZATION", Confidence = 0.9, Source = "Local AI" },
        };
        var result = FindingPostProcessor.Complete(text, found);
        var ahc = result.FirstOrDefault(r => r.Value == "AHC");
        var bli = result.FirstOrDefault(r => r.Value == "BLI");
        var usa = result.FirstOrDefault(r => r.Value == "USA");
        C("The post-processor emits the introduced acronym with an introduction note", ahc != null && ahc.Explanation.Contains("Introduced as the acronym of \"Acme Holdings Corporation\""));
        C("A quoted acronym introduction is recognised", bli != null && bli.Explanation.Contains("defined short name"));

        var project = Project.New("Detection2", "test only");
        foreach (var candidate in result) project.EnsureMapping(candidate, "doc", true);
        project.ConsolidatePersonAliases();
        string T(string v) => project.Mappings.First(m => m.OriginalValue == v).Token;
        C("End to end: AHC shares the token of its full name", ahc != null && T("AHC") == T("Acme Holdings Corporation"));
        C("End to end: a quoted BLI shares the token of its full name", bli != null && T("BLI") == T("Brightmoor Logistics Inc."));
        C("End to end: different companies keep different tokens", new[] { "Acme Holdings Corporation", "Brightmoor Logistics Inc.", "Velmora Foods Inc." }.Select(T).Distinct().Count() == 3);
        C("End to end: a region code in parentheses is not merged into the company", usa == null || T("USA") != T("Velmora Foods Inc."));
        C("End to end: consolidation invents nothing", project.Mappings.Count == result.Count);
    }
}

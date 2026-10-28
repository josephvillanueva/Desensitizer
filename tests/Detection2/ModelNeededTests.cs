using Desensitizer;

// D10 groundwork. These tests document cases that deterministic code cannot solve and that need a model
// (or the Full benchmark) before any change is made. They pass today by asserting the CURRENT, safe
// behavior: the deterministic layers neither invent entities nor merge on weak evidence. When a model
// path is added later, update the matching case and its label deliberately.
public static class ModelNeededTests
{
    private static bool Finds(string text, string category, string value)
        => Detector.Find(text).Any(c => c.Category == category && c.Value.Contains(value, StringComparison.OrdinalIgnoreCase));

    public static void Run(Chk check)
    {
        // Detection: names that appear only inside a sentence have no label to anchor a rule.
        check("D10 needs model: a legal company name inside a sentence is not found by the deterministic layer",
            !Finds("Prepared for Brightmoor Logistics Inc. for the Q3 refund review.", "ORGANIZATION", "Brightmoor"));
        check("D10 needs model: a person named inside a sentence is not found by the deterministic layer",
            !Finds("The request is reviewed by Maria Santos before release.", "PERSON", "Maria Santos"));
        check("D10 needs model: a company without a suffix in a sentence is not found by the deterministic layer",
            !Finds("Payments are routed to Velmora Foods for approval.", "ORGANIZATION", "Velmora"));

        // Identity: weak evidence is never enough for the deterministic consolidation.
        var d = new Doc();
        d.Add("Acme Holdings Corporation"); d.Add("AHC");
        d.Run();
        check("D10 needs model: an acronym used without any introduction stays a separate entity", !d.Same("AHC", "Acme Holdings Corporation"));

        d = new Doc();
        d.Add("Acme Holdings Corporation"); d.Add("Acme Group");
        d.Run();
        check("D10 needs model: a related but different name stays a separate entity", !d.Same("Acme Group", "Acme Holdings Corporation"));

        d = new Doc();
        d.Add("Brightmoor Logistics Inc."); d.Add("the Company");
        d.Run();
        check("D10 needs model: a defined term such as the Company is never linked deterministically", !d.Same("the Company", "Brightmoor Logistics Inc."));

        d = new Doc();
        d.Add("Velmora Foods Inc."); d.Add("Velmora Food Services");
        d.Run();
        check("D10 needs model: a reworded company stays separate (no fuzzy matching)", !d.Same("Velmora Food Services", "Velmora Foods Inc."));

        d = new Doc();
        d.Add("Maria T. Santos", "PERSON"); d.Add("Maria Santos", "PERSON");
        d.Run();
        check("D10 needs model: a person with a missing middle initial is not merged (identity evidence is missing)", d.Tok("Maria T. Santos", "PERSON") != d.Tok("Maria Santos", "PERSON"));
    }
}

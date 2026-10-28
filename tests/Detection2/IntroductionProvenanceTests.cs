using System.Text.Json;
using Desensitizer;

// The same acronym in two documents is not automatically the same company.
// Every value and document id is fictional. No model or user file is read.
public static class IntroductionProvenanceTests
{
    private const string FullName = "Briarstone Harbor Corporation";
    private const string OtherName = "Tallowgate Transit Corporation";
    private const string Acronym = "BHC";
    private static string IntroText(string name = FullName) => name + " (" + Acronym + ") pays within 30 days.";

    public static void Run(Chk check)
    {
        foreach (var category in new[] { "ORGANIZATION", "VENDOR" })
        {
            var d = new Doc();
            var full = d.Add(FullName, category, "a");
            var shortName = d.Add(Acronym, category, "a", CompanyIdentity.NoteFor(FullName, false));
            d.P.RefreshOccurrenceEvidence("a", IntroText());
            d.Run();
            check(category + " current source introduction links its full name", full.Token == shortName.Token);
            check(category + " introduction evidence is explicitly scoped to its file", shortName.CompanyIntroductions != null && shortName.CompanyIntroductions.TryGetValue("a", out var evidence) && evidence.Count == 1 && evidence[0].Name == FullName);

            // The old explanatory sentence is deliberately left in place. Typed
            // negative evidence from the current source must override it.
            d.P.RefreshOccurrenceEvidence("a", FullName + " pays " + Acronym + ", another supplier.");
            d.Run();
            check(category + " a removed declaration splits a previous alias despite its legacy note", full.Token != shortName.Token);
            check(category + " a source check with no declaration is persisted as negative evidence", shortName.CompanyIntroductions!["a"].Count == 0);
            var splitToken = shortName.Token;
            d.Run();
            check(category + " negative introduction evidence remains stable across consolidation", shortName.Token == splitToken && shortName.Token != full.Token);

            var reloaded = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(d.P))!;
            reloaded.ConsolidatePersonAliases();
            check(category + " negative scoped evidence survives project serialization", reloaded.Mappings.Single(m => m.OriginalValue == FullName).Token != reloaded.Mappings.Single(m => m.OriginalValue == Acronym).Token && reloaded.Mappings.Single(m => m.OriginalValue == Acronym).CompanyIntroductions!["a"].Count == 0);

            d.P.RefreshOccurrenceEvidence("a", IntroText());
            d.Run();
            check(category + " restoring the declaration restores the supported link", full.Token == shortName.Token);
            d.Add(FullName, category, "b");
            d.Add(Acronym, category, "b");
            d.Run();
            check(category + " an unreviewed second source cannot borrow the first source introduction", full.Token != shortName.Token);
            d.P.RefreshOccurrenceEvidence("b", FullName + " pays " + Acronym + ", a separate company.");
            d.Run();
            check(category + " a second source without a declaration prevents global token merging", full.Token != shortName.Token);
            d.P.RefreshOccurrenceEvidence("b", IntroText());
            d.Run();
            check(category + " matching current introductions in every source support one token", full.Token == shortName.Token);

            var other = d.Add(OtherName, category, "b");
            d.P.RefreshOccurrenceEvidence("b", IntroText(OtherName));
            d.Run();
            check(category + " conflicting per-source introductions link neither company", shortName.Token != full.Token && shortName.Token != other.Token);
            check(category + " refreshing one source retains the other source's evidence", shortName.CompanyIntroductions!["a"].Single().Name == FullName && shortName.CompanyIntroductions["b"].Single().Name == OtherName);

            d.P.Files.Add(new FileRecord { Id = "a" });
            d.P.Files.Add(new FileRecord { Id = "b" });
            d.P.RemoveFile(d.P.Files.Single(f => f.Id == "b"));
            d.Run();
            check(category + " removing a source removes only that source's introduction evidence", !shortName.CompanyIntroductions!.ContainsKey("b") && shortName.CompanyIntroductions.ContainsKey("a"));
            check(category + " removing a conflicting source leaves the surviving declared alias coherent", shortName.Token == full.Token && d.P.Mappings.All(m => m != other));

            // A name known only in another file cannot validate a local declaration.
            var foreign = new Doc();
            var foreignFull = foreign.Add(FullName, category, "a");
            var localAcronym = foreign.Add(Acronym, category, "b");
            foreign.P.RefreshOccurrenceEvidence("b", IntroText());
            foreign.Run();
            check(category + " source evidence requires the full-name mapping in the same file", foreignFull.Token != localAcronym.Token && localAcronym.CompanyIntroductions!["b"].Count == 0);

            var legacy = new Doc();
            var legacyFull = legacy.Add(FullName, category, "a");
            var legacyShort = legacy.Add(Acronym, category, "a", CompanyIdentity.NoteFor(FullName, false));
            legacy.Run();
            check(category + " single-file provisional introduction notes remain compatible", legacyFull.Token == legacyShort.Token && legacyShort.CompanyIntroductions == null);
            legacy.P.RefreshOccurrenceEvidence("b", "An unrelated document.");
            legacy.Run();
            check(category + " unrelated source refresh does not erase another file's provisional evidence", legacyFull.Token == legacyShort.Token && legacyShort.CompanyIntroductions == null);
            legacy.Add(Acronym, category, "b");
            legacy.Add(FullName, category, "b");
            legacy.Run();
            check(category + " unscoped legacy notes never support cross-file merging", legacyFull.Token != legacyShort.Token);

            var invalid = new Doc();
            var invalidFull = invalid.Add(FullName, category, "a");
            var invalidShort = invalid.Add(Acronym, category, "a", CompanyIdentity.NoteFor(FullName, false));
            invalidShort.CompanyIntroductions = new() { ["a"] = null! };
            invalid.Run();
            check(category + " damaged typed evidence cannot revive a legacy alias", invalidFull.Token != invalidShort.Token);
            invalidShort.CompanyIntroductions["a"] = new() { null! };
            invalid.Run();
            check(category + " null introduction records fail closed", invalidFull.Token != invalidShort.Token);
        }
        TokenAllocation(check);
    }

    private static void TokenAllocation(Chk check)
    {
        var project = Project.New("Fictional bounded allocation", "not saved");
        project.Mappings.Add(new Mapping { Category = "PERSON", OriginalValue = "Elena Vale", Token = "[PERSON_2147483647]", SourceFileIds = new() { "a" } });
        project.Mappings.Add(new Mapping { Category = "PERSON", OriginalValue = "Marcus Rowan", Token = "[PERSON_999999999999999999999]", SourceFileIds = new() { "a" } });
        project.Mappings.Add(new Mapping { Category = "ORGANIZATION", OriginalValue = "Reserved analyst choice", Token = "[PERSON_001]", IsManual = true });
        var allocated = project.EnsureMapping("Daria Moss", "PERSON", sourceFileId: "a");
        check("Token allocation: maximum and oversized historical numbers do not overflow", allocated.Token == "[PERSON_002]");
        var next = project.EnsureMapping("Galen Frost", "PERSON", sourceFileId: "a");
        check("Token allocation: fallback preserves all existing token reservations", next.Token == "[PERSON_003]" && project.Mappings.Select(m => m.Token).Distinct().Count() == project.Mappings.Count);

        foreach (var protection in new[] { "manual", "keep", "context", "custom" })
        {
            var p = Project.New("Fictional protected alias", "not saved");
            var existing = p.EnsureMapping("Vale, Elena", "PERSON", sourceFileId: "a");
            existing.IsManual = protection == "manual";
            existing.Ignored = protection == "keep";
            existing.RequiresContextReview = protection == "context";
            if (protection == "custom") existing.Token = "[ANALYST_PERSON]";
            var expected = existing.Token;
            var automatic = p.EnsureMapping("Elena Vale", "PERSON", sourceFileId: "a");
            check("Token allocation: automatic person does not inherit " + protection + " alias token", automatic.Token != expected && existing.Token == expected);

            p = Project.New("Fictional protected company", "not saved");
            existing = p.EnsureMapping("Briarstone Harbor Inc.", "ORGANIZATION", sourceFileId: "a");
            existing.IsManual = protection == "manual";
            existing.Ignored = protection == "keep";
            existing.RequiresContextReview = protection == "context";
            if (protection == "custom") existing.Token = "[ANALYST_COMPANY]";
            expected = existing.Token;
            automatic = p.EnsureMapping("Briarstone Harbor Inc", "ORGANIZATION", sourceFileId: "a");
            check("Token allocation: automatic company does not inherit " + protection + " alias token", automatic.Token != expected && existing.Token == expected);
        }
    }
}

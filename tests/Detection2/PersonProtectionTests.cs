using Desensitizer;

public static class PersonProtectionTests
{
    private static Mapping Person(string value, string token, int day = 1, string file = "fictional-document")
        => new() { OriginalValue = value, Category = "PERSON", Token = token,
            SourceFileIds = new() { file }, CreatedAt = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero) };

    public static void Run(Chk check)
    {
        foreach (var protectedKind in new[] { "manual", "kept", "context", "custom" })
        {
            var automatic = Person("Elena Vale", "[PERSON_001]");
            var protectedMapping = Person("Vale, Elena", protectedKind == "custom" ? "[ANALYST_CHOSEN]" : "[PERSON_002]", 2);
            protectedMapping.IsManual = protectedKind == "manual";
            protectedMapping.Ignored = protectedKind == "kept";
            protectedMapping.RequiresContextReview = protectedKind == "context";
            var expected = protectedMapping.Token;
            var mappings = new List<Mapping> { automatic, protectedMapping };
            PersonIdentity.Consolidate(mappings);
            check("Person protection: " + protectedKind + " token is unchanged", protectedMapping.Token == expected);
            check("Person protection: " + protectedKind + " is not merged into an automatic alias", automatic.Token != protectedMapping.Token);
            var stable = mappings.Select(m => m.Token).ToArray();
            PersonIdentity.Consolidate(mappings);
            check("Person protection: " + protectedKind + " consolidation is stable", stable.SequenceEqual(mappings.Select(m => m.Token)));
        }

        var collision = Person("Elena Vale", "[PERSON_001]");
        var analyst = Person("Marcus Rowan", "[PERSON_001]", 2);
        analyst.IsManual = true;
        var collisionMappings = new List<Mapping> { collision, analyst };
        PersonIdentity.Consolidate(collisionMappings);
        check("Person protection: a later analyst token is reserved before automatic assignment", analyst.Token == "[PERSON_001]" && collision.Token == "[PERSON_002]");
        PersonIdentity.Consolidate(collisionMappings);
        check("Person protection: repairing a legacy automatic collision is stable", analyst.Token == "[PERSON_001]" && collision.Token == "[PERSON_002]");

        var hugeCustom = Person("Elena Vale", "[PERSON_999999999999999999999]");
        PersonIdentity.Consolidate(new() { hugeCustom });
        check("Person protection: an oversized custom numeric token is preserved without crashing", hugeCustom.Token == "[PERSON_999999999999999999999]");

        var maximum = Person("Elena Vale", "[PERSON_2147483647]");
        var maximumAnalyst = Person("Marcus Rowan", "[PERSON_2147483647]", 2);
        maximumAnalyst.IsManual = true;
        var otherToken = Person("Daniel Moss", "[PERSON_001]", 3);
        var maximumMappings = new List<Mapping> { maximum, maximumAnalyst, otherToken };
        PersonIdentity.Consolidate(maximumMappings);
        check("Person protection: maximum numeric collision allocates an in-range unused token", maximum.Token == "[PERSON_002]" && maximumAnalyst.Token == "[PERSON_2147483647]" && otherToken.Token == "[PERSON_001]");
        // Reuse the repaired mapping in a fresh project. The historical maximum
        // remains a separate legacy caller concern, not repaired by this resolver.
        var repairedProject = Project.New("Fictional repaired token", "not saved");
        maximum.NormalizedValue = Project.Normalize(maximum.OriginalValue);
        repairedProject.Mappings.Add(maximum);
        var freshPerson = repairedProject.EnsureMapping("Daniel Moss", "PERSON", true, sourceFileId: "fictional-document");
        check("Person protection: the newly allocated token remains valid for later Project finding allocation", freshPerson.Token == "[PERSON_003]");

        var foreignCategory = Person("Fictional Corp", "[PERSON_001]", 2);
        foreignCategory.Category = "ORGANIZATION";
        foreignCategory.IsManual = true;
        var automaticCollision = Person("Elena Vale", "[PERSON_001]");
        PersonIdentity.Consolidate(new() { automaticCollision, foreignCategory });
        check("Person protection: an analyst token in another category is reserved", foreignCategory.Token == "[PERSON_001]" && automaticCollision.Token == "[PERSON_002]");

        var manualProject = Project.New("Fictional analyst identity", "not saved");
        var manualPerson = manualProject.EnsureMapping("Vale, Elena", "PERSON", true, sourceFileId: "manual-doc", isManual: true);
        manualPerson.Token = "[PERSON_021]";
        var autoPerson = manualProject.EnsureMapping("Elena Vale", "PERSON", true, sourceFileId: "manual-doc");
        manualProject.ConsolidatePersonAliases();
        check("Person protection: Project finding allocation cannot overwrite an analyst-chosen person token", manualPerson.Token == "[PERSON_021]" && autoPerson.Token != manualPerson.Token);
        var projectStable = manualProject.Mappings.Select(m => m.Token).ToArray();
        manualProject.ConsolidatePersonAliases();
        check("Person protection: Project manual and automatic tokens remain stable", projectStable.SequenceEqual(manualProject.Mappings.Select(m => m.Token)));

        var project = Project.New("Fictional person identity", "not saved");
        var canonical = project.EnsureMapping("Elena A. Vale", "PERSON", true, sourceFileId: "d1");
        var reversed = project.EnsureMapping("Vale, Elena A.", "PERSON", true, sourceFileId: "d1");
        var unrelated = project.EnsureMapping("Elena A. Vale", "PERSON", true, sourceFileId: "d2");
        var shortName = project.EnsureMapping("Elena", "PERSON", true, sourceFileId: "d1");
        project.ConsolidatePersonAliases();
        check("Person protection: ordinary automatic aliases still consolidate", canonical.Token == reversed.Token);
        check("Person protection: automatic names in unrelated documents remain separate", canonical.Token != unrelated.Token);
        check("Person protection: a short name is not guessed to be the full person", canonical.Token != shortName.Token);
    }
}

using Desensitizer;

static class RehydratePreviewTests
{
    private static Mapping Map(string original, string token, string category = "PERSON") => new()
    {
        OriginalValue = original, NormalizedValue = Project.Normalize(original), Token = token, Category = category,
        SourceFileIds = new() { "fictional-document" }, Approved = true
    };

    public static void Run(CheckFn check, string work)
    {
        var person = Map("Elena Vale", "[PERSON_001]");
        var before = System.Text.Json.JsonSerializer.Serialize(person);
        var preview = UiRehydratePreview.Analyze("[PERSON_001] approved [PERSON_001].", new[] { person });
        check("rehydrate preview: repeated tokens are counted separately", preview.KnownOccurrences == 2 && preview.KnownTokenCount == 1 && preview.CanRestore);
        check("rehydrate preview: known mapping object remains unchanged", before == System.Text.Json.JsonSerializer.Serialize(person));
        check("rehydrate preview: count-only text contains no identifying values", !(preview.Summary + preview.Warning).Contains(person.OriginalValue) && !(preview.Summary + preview.Warning).Contains(person.Token));
        preview = UiRehydratePreview.Analyze("[PERSON_001] [PERSON_999] [PERSON_999] [EMAIL_111]", new[] { person });
        check("rehydrate preview: unknown occurrences and unique tokens are separate", preview.KnownOccurrences == 1 && preview.UnknownOccurrences == 3 && preview.UnknownTokenCount == 2);
        check("rehydrate preview: unknown tokens are warned about without blocking recognized restoration", preview.CanRestore && preview.Warning.Contains("Unknown tokens"));
        preview = UiRehydratePreview.Analyze("[PERSON_999]", new[] { person });
        check("rehydrate preview: wrong project has no restorable occurrences", !preview.CanRestore && preview.KnownOccurrences == 0 && preview.UnknownOccurrences == 1);
        preview = UiRehydratePreview.Analyze("No replacement tokens occur here.", new[] { person });
        check("rehydrate preview: no tokens does not propose a pointless restoration", !preview.CanRestore && preview.KnownOccurrences == 0 && preview.UnknownOccurrences == 0);

        var custom = Map("Elena Vale", "[ANALYST_CHOICE]");
        preview = UiRehydratePreview.Analyze("[ANALYST_CHOICE] [analyst_choice]", new[] { custom });
        check("rehydrate preview: custom analyst tokens use exact case-sensitive matching", preview.KnownOccurrences == 1 && preview.CanRestore);
        preview = UiRehydratePreview.Analyze("[PERSON_001]-EXTRA", new[] { person, Map("Fictional Company", "[PERSON_001]-EXTRA", "ORGANIZATION") });
        check("rehydrate preview: longer custom token prevents counting its nested shorter token", preview.KnownOccurrences == 1 && preview.KnownTokenCount == 1 && preview.UnknownOccurrences == 0);

        var reversed = Map("Vale, Elena", person.Token);
        preview = UiRehydratePreview.Analyze(person.Token, new[] { person, reversed });
        check("rehydrate preview: legitimate reversed person alias remains restorable", preview.CanRestore && preview.AliasTokenCount == 1 && preview.ConflictingTokenCount == 0);
        check("rehydrate preview: alias warning explains canonical spelling", preview.Warning.Contains("canonical spelling"));
        preview = UiRehydratePreview.Analyze(person.Token, new[] { person, Map("Marcus Rowan", person.Token) });
        check("rehydrate preview: different people sharing a token block restoration", !preview.CanRestore && preview.ConflictingTokenCount == 1);
        preview = UiRehydratePreview.Analyze(person.Token, new[] { person, Map("Elena", person.Token) });
        check("rehydrate preview: a first name is not guessed to be the same person", !preview.CanRestore);
        preview = UiRehydratePreview.Analyze(person.Token, new[] { person, Map(person.OriginalValue, person.Token, "ORGANIZATION") });
        check("rehydrate preview: cross-category token conflict blocks even with equal spelling", !preview.CanRestore && preview.ConflictingTokenCount == 1);
        preview = UiRehydratePreview.Analyze("[PERSON_777]", new[] { person, Map("Marcus Rowan", person.Token) });
        check("rehydrate preview: absent conflicting token is not reported as present", preview.ConflictingTokenCount == 0);

        var company = Map("Brightmoor Logistics Corporation", "[ORGANIZATION_001]", "ORGANIZATION");
        var companyVariant = Map("Brightmoor Logistics Corp.", company.Token, "ORGANIZATION");
        var companyBefore = System.Text.Json.JsonSerializer.Serialize(new[] { company, companyVariant });
        preview = UiRehydratePreview.Analyze(company.Token, new[] { company, companyVariant });
        check("rehydrate preview: supported company legal aliases remain restorable", preview.CanRestore && preview.AliasTokenCount == 1);
        check("rehydrate preview: company verification never retokens original mappings", companyBefore == System.Text.Json.JsonSerializer.Serialize(new[] { company, companyVariant }));
        preview = UiRehydratePreview.Analyze(company.Token, new[] { company, Map("Tallowgate Foods Inc.", company.Token, "ORGANIZATION") });
        check("rehydrate preview: incompatible companies sharing a token block restoration", !preview.CanRestore && preview.ConflictingTokenCount == 1);
        var protectedOther = Map("Tallowgate Foods Inc.", company.Token, "ORGANIZATION");
        protectedOther.IsManual = true;
        preview = UiRehydratePreview.Analyze(company.Token, new[] { company, protectedOther });
        check("rehydrate preview: preserved manual collision is not mistaken for identity evidence", !preview.CanRestore);

        preview = UiRehydratePreview.Analyze(person.Token, new[] { person, Map("", "[PERSON_008]") });
        check("rehydrate preview: empty restoration value blocks invalid mapping list", !preview.CanRestore && preview.InvalidMappingCount == 1);
        preview = UiRehydratePreview.Analyze(person.Token, new[] { person, Map("Marcus Rowan", "") });
        check("rehydrate preview: empty token cannot reach global empty-string replacement", !preview.CanRestore && preview.InvalidMappingCount == 1);
        check("rehydrate preview: button copy provides a clear action and cancellation", RehydratePreviewDialog.RestoreButton == "Restore copy" && RehydratePreviewDialog.CancelButton == "Cancel");

        var folder = Path.Combine(work, "rehydrate-preview"); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "fictional-returned.txt"); File.WriteAllText(source, person.Token);
        var originalBytes = File.ReadAllBytes(source);
        var filesBefore = Directory.GetFiles(folder).Length;
        UiRehydratePreview.Analyze(File.ReadAllText(source), new[] { person });
        check("rehydrate preview: preflight leaves source bytes unchanged and creates no output", originalBytes.SequenceEqual(File.ReadAllBytes(source)) && Directory.GetFiles(folder).Length == filesBefore);
    }
}

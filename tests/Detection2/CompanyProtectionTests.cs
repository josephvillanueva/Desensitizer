using Desensitizer;

// Fictional names only. Identity protection must not overwrite analyst decisions
// or collapse distinct names after losing script-specific or legally meaningful words.
public static class CompanyProtectionTests
{
    public static void Run(Chk check)
    {
        foreach (var category in new[] { "ORGANIZATION", "VENDOR" })
        {
            foreach (var names in new[]
            {
                new[] { "新光 Holdings Inc.", "星海 Holdings Inc." },
                new[] { "Север Holdings Inc.", "Юг Holdings Inc." },
                new[] { "The Haven Inc.", "Haven Inc." },
                new[] { "Haven and Grove Inc.", "Haven Grove Inc." },
                new[] { "Haven of Grove Inc.", "Haven Grove Inc." }
            })
            {
                var d = new Doc();
                var left = d.Add(names[0], category); var right = d.Add(names[1], category);
                d.Run();
                check(category + " distinct company words survive consolidation: " + names[0], left.Token != right.Token);
                var first = left.Token; var second = right.Token;
                d.Run();
                check(category + " distinct company tokens are stable: " + names[0], left.Token == first && right.Token == second);
            }

            foreach (var protection in new[] { "manual", "keep", "context", "custom" })
            {
                var d = new Doc();
                var automatic = d.Add("Brightmoor Logistics Inc.", category);
                var protectedMapping = d.Add("Tallowgate Foods Inc.", category);
                protectedMapping.Token = automatic.Token;
                switch (protection)
                {
                    case "manual": protectedMapping.IsManual = true; break;
                    case "keep": protectedMapping.Ignored = true; break;
                    case "context": protectedMapping.RequiresContextReview = true; break;
                    case "custom": protectedMapping.Token = "[ANALYST_COMPANY]"; break;
                }
                var expected = protectedMapping.Token;
                d.Run();
                check(category + " " + protection + " token is preserved", protectedMapping.Token == expected);
                check(category + " automatic mapping cannot steal " + protection + " token", automatic.Token != expected);
                var assigned = automatic.Token;
                d.Run();
                check(category + " " + protection + " token protection is stable", protectedMapping.Token == expected && automatic.Token == assigned);
            }

            foreach (var protection in new[] { "manual", "keep", "context" })
            {
                var d = new Doc();
                var full = d.Add("Brightmoor Logistics Inc.", category);
                var blocked = d.Add("Brightmoor Logistics Inc", category);
                var bare = d.Add("Brightmoor Logistics", category);
                switch (protection)
                {
                    case "manual": blocked.IsManual = true; break;
                    case "keep": blocked.Ignored = true; break;
                    case "context": blocked.RequiresContextReview = true; break;
                }
                d.Run();
                check(category + " blocked same-suffix " + protection + " prevents bare merge", bare.Token != full.Token && bare.Token != blocked.Token);
            }

            var positive = new Doc();
            var a = positive.Add("新光 Holdings Corporation", category);
            var b = positive.Add("新光 Holdings Corp.", category);
            var bareName = positive.Add("新光 Holdings", category);
            positive.Run();
            check(category + " Unicode company legal variants retain positive aliases", a.Token == b.Token && b.Token == bareName.Token);
            positive = new Doc();
            a = positive.Add("The Brightmoor Logistics Inc.", category);
            b = positive.Add("BL", category, explanation: CompanyIdentity.NoteFor(a.OriginalValue, false));
            positive.Run();
            check(category + " initials still omit small words for introduced acronym", a.Token == b.Token);

            var crossCategory = new Doc();
            var auto = crossCategory.Add("Brightmoor Logistics Inc.", category);
            var manual = crossCategory.Add("Fictional identity", "PERSON");
            manual.IsManual = true; manual.Token = auto.Token;
            var reserved = manual.Token;
            CompanyIdentity.Consolidate(crossCategory.P.Mappings);
            check(category + " automatic company does not steal other-category custom token", manual.Token == reserved && auto.Token != reserved);

            var overflow = new Doc();
            var max = overflow.Add("Brightmoor Logistics Inc.", category);
            var otherMax = overflow.Add("Tallowgate Foods Inc.", category);
            max.Token = "[" + category + "_2147483647]";
            otherMax.Token = max.Token;
            CompanyIdentity.Consolidate(overflow.P.Mappings);
            var allocated = System.Text.RegularExpressions.Regex.Match(otherMax.Token, @"_(\d+)\]$");
            check(category + " duplicate max-number token allocates a parseable positive slot", max.Token != otherMax.Token
                && allocated.Success && int.TryParse(allocated.Groups[1].Value, out var n) && n > 0);

            overflow = new Doc();
            max = overflow.Add("Brightmoor Logistics Inc.", category);
            otherMax = overflow.Add("Tallowgate Foods Inc.", category);
            var later = overflow.Add("Quillfeather Energy Inc.", category);
            max.Token = "[" + category + "_2147483647]"; otherMax.Token = max.Token;
            later.Token = "[" + category + "_001]";
            var laterToken = later.Token;
            CompanyIdentity.Consolidate(overflow.P.Mappings);
            check(category + " wrapped allocation does not steal a later group's existing token", later.Token == laterToken
                && otherMax.Token != laterToken && max.Token != otherMax.Token);
        }
        UnicodeExport(check);
    }

    private static void UnicodeExport(Chk check)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CompanyProtection-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var d = new Doc();
            var left = d.Add("新光 Holdings Inc."); var right = d.Add("星海 Holdings Inc.");
            d.Run();
            const string rule = " shall pay 50000 within 30 days if the refund is approved.";
            var original = left.OriginalValue + rule + "\n" + right.OriginalValue + " shall verify the refund before payment.";
            var source = System.IO.Path.Combine(directory, "source.txt");
            var output = System.IO.Path.Combine(directory, "protected.txt");
            var restored = System.IO.Path.Combine(directory, "restored.txt");
            System.IO.File.WriteAllText(source, original, new System.Text.UTF8Encoding(false));
            DocumentProcessor.Desensitize(source, output, d.P.Mappings);
            var protectedText = DocumentProcessor.ReadText(output);
            check("Unicode company export has two distinct identity tokens", left.Token != right.Token
                && protectedText.Contains(left.Token) && protectedText.Contains(right.Token)
                && !protectedText.Contains(left.OriginalValue) && !protectedText.Contains(right.OriginalValue));
            check("Unicode company export preserves payment business rule and quantity", protectedText.Contains(rule)
                && protectedText.Contains(" shall verify the refund before payment."));
            var unknown = DocumentProcessor.Rehydrate(output, restored, d.P.Mappings);
            check("Unicode companies rehydrate without swapping legal identities", unknown == 0 && DocumentProcessor.ReadText(restored) == original);
            check("Unicode company export leaves the source unchanged", System.IO.File.ReadAllText(source) == original);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }
}

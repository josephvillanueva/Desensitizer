using System.Diagnostics;
using System.Text.Json;
using Desensitizer;

// Stage S13: fragment guard. Fictional values only. A shortened form of an approved full name (a lone first name,
// surname or company stem) must become a visible, blocking Review finding. It is never linked to the full name by itself.
public static class FragmentGuardTests
{
    private const string File1 = "fictional-document";

    private static Mapping Approved(string value, string category, string token)
        => new() { OriginalValue = value, NormalizedValue = Project.Normalize(value), Category = category, Token = token, Approved = true, SourceFileIds = new() { File1 } };

    private static List<Mapping> Project1() => new()
    {
        Approved("Zelda Quimby-Orsino", "PERSON", "[PERSON_001]"),
        Approved("Vantablack Holdings Zeta", "ORGANIZATION", "[ORGANIZATION_001]"),
    };

    private static string[] Values(IEnumerable<Candidate> found) => found.Select(c => c.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray();

    // The analyst's Keep as-is, the way the mapping grid does it: acknowledge the evidence, then set the decision.
    private static void Keep(Project project, Candidate candidate)
    {
        var mapping = project.EnsureMapping(candidate, File1);
        mapping.AcknowledgeContextReview();
        mapping.Decision = "Keep as-is";
    }

    private static void Replace(Project project, Candidate candidate)
    {
        var mapping = project.EnsureMapping(candidate, File1);
        mapping.AcknowledgeContextReview();
        mapping.Decision = "Replace";
    }

    public static void Run(Chk check)
    {
        // ---- unique fragments are found, as review items, without quoting values
        var mappings = Project1();
        var text = "Zelda joined the call. Quimby signed it. Orsino approved it. Vantablack paid the invoice.";
        var found = FragmentGuard.Find(text, mappings, File1);
        check("Fragment: first name, both surname parts and the company stem are found", Values(found).SequenceEqual(new[] { "Orsino", "Quimby", "Vantablack", "Zelda" }), string.Join(",", Values(found)));
        check("Fragment: every finding needs context review and is labelled", found.All(c => c.RequiresContextReview && c.Source == FragmentGuard.SourceName && c.DetectionSources.Contains(FragmentGuard.SourceName)));
        check("Fragment: person parts keep the PERSON category and the stem keeps ORGANIZATION", found.Where(c => c.Value != "Vantablack").All(c => c.Category == "PERSON") && found.Single(c => c.Value == "Vantablack").Category == "ORGANIZATION");
        check("Fragment: explanations never quote any value", found.All(c => !new[] { "Zelda", "Quimby", "Orsino", "Vantablack", "Holdings", "Zeta" }.Any(v => c.Explanation.Contains(v, StringComparison.OrdinalIgnoreCase))));
        var gate = FragmentGuard.Gate(text, mappings, File1);
        check("Fragment: the gate message lists counts and categories but no value", gate.Blocked && gate.Message.Contains("3 PERSON") && gate.Message.Contains("1 ORGANIZATION") && !new[] { "Zelda", "Quimby", "Orsino", "Vantablack" }.Any(v => gate.Message.Contains(v, StringComparison.OrdinalIgnoreCase)), gate.Message);

        // ---- no automatic link to the full name
        var identity = new List<Mapping>(Project1());
        var personFragment = new Mapping { OriginalValue = "Zelda", NormalizedValue = "ZELDA", Category = "PERSON", Token = "[PERSON_002]", Approved = true, SourceFileIds = new() { File1 }, DetectionSources = new() { FragmentGuard.SourceName } };
        var companyFragment = new Mapping { OriginalValue = "Vantablack", NormalizedValue = "VANTABLACK", Category = "ORGANIZATION", Token = "[ORGANIZATION_002]", Approved = true, SourceFileIds = new() { File1 }, DetectionSources = new() { FragmentGuard.SourceName } };
        identity.Add(personFragment); identity.Add(companyFragment);
        PersonIdentity.Consolidate(identity);
        check("Fragment: a decided person fragment keeps its own token", personFragment.Token != identity[0].Token);
        check("Fragment: a decided company stem keeps its own token (no unique-stem merge)", companyFragment.Token != identity[1].Token);

        // ---- Keep as-is suppresses the finding and persists
        var project = Project.New("Fictional fragment project", "not saved");
        project.Files.Add(new FileRecord { Id = File1 });
        foreach (var m in Project1()) { m.SourceFileIds = new() { File1 }; project.Mappings.Add(m); }
        var first = FragmentGuard.Find("Zelda attended.", project.Mappings, File1).Single();
        var undecided = project.EnsureMapping(first, File1);
        check("Fragment: registering the finding gives a Review item that is not approved", undecided.RequiresContextReview && !undecided.Approved && !undecided.Ignored);
        check("Fragment: an undecided fragment still blocks", FragmentGuard.Gate("Zelda attended.", project.Mappings, File1).Blocked);
        Keep(project, first);
        check("Fragment: Keep as-is suppresses the finding", !FragmentGuard.Gate("Zelda attended.", project.Mappings, File1).Blocked);
        var json = JsonSerializer.Serialize(project.Mappings);
        var reloaded = JsonSerializer.Deserialize<List<Mapping>>(json)!;
        check("Fragment: the Keep decision survives saving and loading", !FragmentGuard.Gate("Zelda attended.", reloaded, File1).Blocked);
        check("Fragment: the Keep decision is for this word only", FragmentGuard.Gate("Quimby attended.", reloaded, File1).Blocked);

        // The kept fragment does not cover a different category or changed evidence.
        var shared = new List<Mapping> { Approved("Ana Reyes", "PERSON", "[PERSON_001]"), Approved("Reyes Trading Inc", "ORGANIZATION", "[ORGANIZATION_001]") };
        var sharedProject = Project.New("Fictional shared word", "not saved");
        sharedProject.Files.Add(new FileRecord { Id = File1 });
        foreach (var m in shared) sharedProject.Mappings.Add(m);
        var both = FragmentGuard.Find("Reyes signed.", sharedProject.Mappings, File1);
        check("Fragment: one word shared by a person and a company gives one finding per category", both.Select(c => c.Category).OrderBy(c => c).SequenceEqual(new[] { "ORGANIZATION", "PERSON" }));
        Keep(sharedProject, both.Single(c => c.Category == "PERSON"));
        var afterKeep = FragmentGuard.Find("Reyes signed.", sharedProject.Mappings, File1);
        check("Fragment: a Keep for the PERSON word does not suppress the ORGANIZATION word", afterKeep.Count == 1 && afterKeep[0].Category == "ORGANIZATION");

        var evidence = Project.New("Fictional evidence", "not saved");
        evidence.Files.Add(new FileRecord { Id = File1 });
        evidence.Mappings.Add(Approved("Ana Reyes", "PERSON", "[PERSON_001]"));
        var reyes = FragmentGuard.Find("Reyes called.", evidence.Mappings, File1).Single();
        Keep(evidence, reyes);
        check("Fragment: the kept word is quiet while the context is unchanged", !FragmentGuard.Gate("Reyes called.", evidence.Mappings, File1).Blocked);
        evidence.Mappings.Add(Approved("Ben Reyes", "PERSON", "[PERSON_002]"));
        check("Fragment: a Keep is re-opened when the context changes (a second person now shares the word)", FragmentGuard.Gate("Reyes called.", evidence.Mappings, File1).Blocked);

        // ---- Replace gives the word its own token and clears the gate
        var replaceProject = Project.New("Fictional replace", "not saved");
        replaceProject.Files.Add(new FileRecord { Id = File1 });
        foreach (var m in Project1()) { m.SourceFileIds = new() { File1 }; replaceProject.Mappings.Add(m); }
        var toReplace = FragmentGuard.Find("Quimby arrived.", replaceProject.Mappings, File1).Single();
        Replace(replaceProject, toReplace);
        var replaced = replaceProject.Mappings.Single(m => m.NormalizedValue == "QUIMBY");
        check("Fragment: Replace creates its own mapping with its own token", replaced.Approved && replaced.Token != "[PERSON_001]" && replaced.Token.StartsWith("[PERSON_"));
        check("Fragment: after Replace the gate passes for that word", !FragmentGuard.Gate("Quimby arrived.", replaceProject.Mappings, File1).Blocked);
        replaceProject.ConsolidatePersonAliases();
        check("Fragment: consolidation keeps the replaced word on its own token", replaceProject.Mappings.Single(m => m.NormalizedValue == "QUIMBY").Token == replaced.Token && replaced.Token != "[PERSON_001]");

        // ---- two approved people sharing a surname: ambiguity, never an automatic link
        var people = new List<Mapping> { Approved("Ana Reyes", "PERSON", "[PERSON_001]"), Approved("Ben Reyes", "PERSON", "[PERSON_002]") };
        var hits = FragmentGuard.Analyze("Reyes confirmed the request.", people, File1).Where(h => h.Value == "Reyes").ToList();
        check("Fragment: a surname shared by two approved people gives two findings, one per person", hits.Count == 2 && hits.Select(h => h.Owner.Token).Distinct().Count() == 2);
        check("Fragment: each of the two findings names the ambiguity", hits.All(h => h.Entities == 2 && h.Candidate.Explanation.Contains("ambiguous")));
        var merged = FragmentGuard.Find("Reyes confirmed the request.", people, File1);
        check("Fragment: the shared surname becomes one Review item that is never linked to a person", merged.Count == 1 && merged[0].RequiresContextReview);
        PersonIdentity.Consolidate(people);
        check("Fragment: the two people keep different tokens", people[0].Token != people[1].Token);

        // ---- a fragment inside a longer mapped name is not flagged
        check("Fragment: the full name itself does not flag its parts", FragmentGuard.Find("Zelda Quimby-Orsino signed. Vantablack Holdings Zeta paid.", Project1(), File1).Count == 0);
        check("Fragment: case, spacing and a line break inside the full name do not flag its parts", FragmentGuard.Find("ZELDA   quimby-orsino\r\nsigned.", Project1(), File1).Count == 0);
        var longer = Project1(); longer.Add(Approved("Quimby Orsino Foundation", "ORGANIZATION", "[ORGANIZATION_002]"));
        check("Fragment: a part inside another approved longer name is covered by that name", FragmentGuard.Find("The Quimby Orsino Foundation signed.", longer, File1).All(c => c.Value != "Quimby" && c.Value != "Orsino"));
        check("Fragment: a lone part next to a mapped name is still flagged", FragmentGuard.Find("Zelda Quimby-Orsino and Zelda met.", Project1(), File1).Select(c => c.Value).SequenceEqual(new[] { "Zelda" }));

        // ---- short and generic words are not flagged
        var small = new List<Mapping> { Approved("Al Wu", "PERSON", "[PERSON_001]"), Approved("Joe Kim", "PERSON", "[PERSON_002]"), Approved("Mark Approval", "PERSON", "[PERSON_003]"), Approved("Global Systems Inc", "ORGANIZATION", "[ORGANIZATION_001]") };
        check("Fragment: words shorter than four letters are not guarded", FragmentGuard.Find("Al and Joe and Wu and Kim met.", small, File1).Count == 0);
        check("Fragment: generic and ordinary words are not guarded", FragmentGuard.Find("Mark the Approval step. Global Systems are listed. Systems and Global.", small, File1).Count == 0);
        check("Fragment: a mapping with a single plain word has no shorter form to guard", FragmentGuard.Components(Approved("Zelda", "PERSON", "[PERSON_009]")).Count == 0);
        var business = "Approval Workflow: the request is routed when the amount exceeds 10,000 within 5 days. Revision History is kept.";
        check("Fragment: business content such as Approval Workflow and thresholds is untouched", FragmentGuard.Find(business, Project1(), File1).Count == 0 && FragmentGuard.Find(business, small, File1).Count == 0);

        // ---- Unicode and diacritics
        var accents = new List<Mapping> { Approved("José Peña", "PERSON", "[PERSON_001]") };
        check("Fragment: diacritics are folded (Jose and Pena are the same words as José and Peña)", Values(FragmentGuard.Find("Jose met Pena today.", accents, File1)).SequenceEqual(new[] { "Jose", "Pena" }));
        check("Fragment: the accented spelling is found too", Values(FragmentGuard.Find("José met Peña today.", accents, File1)).SequenceEqual(new[] { "José", "Peña" }));
        check("Fragment: the exact full name is covered", FragmentGuard.Find("José Peña signed.", accents, File1).Count == 0);
        check("Fragment: the full name with the diacritics dropped is not covered by the replacement, so its parts are flagged", Values(FragmentGuard.Find("Jose Pena signed.", accents, File1)).SequenceEqual(new[] { "Jose", "Pena" }));
        check("Fragment: decomposed accents (combining marks) are folded", FragmentGuard.Find("Peña joined.", new List<Mapping> { Approved("José Peña", "PERSON", "[PERSON_001]") }, File1).Any(c => c.Value.StartsWith("Pen", StringComparison.Ordinal)));
        check("Fragment: a lower-case person word is not flagged in a mixed-case document", FragmentGuard.Find("The peña and the jose are ordinary words here.", accents, File1).Count == 0);

        // ---- hyphenated surnames
        var hyphen = new List<Mapping> { Approved("Zelda Quimby-Orsino", "PERSON", "[PERSON_001]") };
        check("Fragment: the whole hyphenated surname on its own is one finding", Values(FragmentGuard.Find("Ms Quimby-Orsino replied.", hyphen, File1)).SequenceEqual(new[] { "Quimby-Orsino" }));
        check("Fragment: a hyphenated word that contains a part is one finding, never split", Values(FragmentGuard.Find("Mr Orsino-Smith replied.", hyphen, File1)).SequenceEqual(new[] { "Orsino-Smith" }));
        check("Fragment: a non-breaking hyphen is treated as a hyphen", Values(FragmentGuard.Find("Quimby‑Orsino replied.", hyphen, File1)).SequenceEqual(new[] { "Quimby‑Orsino" }));
        check("Fragment: a single hyphenated surname still guards its parts", Values(FragmentGuard.Find("Orsino replied.", new List<Mapping> { Approved("Quimby-Orsino", "PERSON", "[PERSON_001]") }, File1)).SequenceEqual(new[] { "Orsino" }));
        check("Fragment: a possessive is a separate word", Values(FragmentGuard.Find("Zelda's laptop and Quimby’s desk.", hyphen, File1)).SequenceEqual(new[] { "Quimby", "Zelda" }));

        // ---- company stem against the full name
        var company = new List<Mapping> { Approved("Vantablack Holdings Zeta", "ORGANIZATION", "[ORGANIZATION_001]") };
        check("Fragment: the company stem alone is flagged", Values(FragmentGuard.Find("The Vantablack contract.", company, File1)).SequenceEqual(new[] { "Vantablack" }));
        check("Fragment: the full company name does not flag the stem", FragmentGuard.Find("The Vantablack Holdings Zeta contract.", company, File1).Count == 0);
        check("Fragment: descriptor words of the company are not guarded", FragmentGuard.Find("Holdings and Zeta are separate words.", company, File1).Count == 0);
        check("Fragment: the stem is guarded in lower case too (a company stem is distinctive)", FragmentGuard.Find("see vantablack for details", company, File1).Count == 1);
        check("Fragment: a legal-suffix name guards its first word", Values(FragmentGuard.Find("Kestrelbrook paid.", new List<Mapping> { Approved("Kestrelbrook Energy LLC", "ORGANIZATION", "[ORGANIZATION_001]") }, File1)).SequenceEqual(new[] { "Kestrelbrook" }));
        check("Fragment: a vendor name is guarded the same way", FragmentGuard.Find("Vantablack paid.", new List<Mapping> { Approved("Vantablack Holdings Zeta", "VENDOR", "[VENDOR_001]") }, File1).Single().Category == "VENDOR");

        // ---- ALL-CAPS documents
        var caps = "ZELDA QUIMBY-ORSINO HAS SIGNED THE AGREEMENT AND ZELDA WILL RETURN. THE VANTABLACK TEAM WAS INFORMED OF THE OUTCOME.";
        check("Fragment: an all-caps document is still guarded", Values(FragmentGuard.Find(caps, Project1(), File1)).SequenceEqual(new[] { "VANTABLACK", "ZELDA" }), string.Join(",", Values(FragmentGuard.Find(caps, Project1(), File1))));
        check("Fragment: a lower-case person word in a mixed document is quiet but upper case is flagged", FragmentGuard.Find("The new zelda theme and the old Zelda rule, plus a longer sentence to look like ordinary prose.", Project1(), File1).Count == 1);

        // ---- decisions on the document's own findings and ignored kinds
        var kept = Project1(); kept.Add(new Mapping { OriginalValue = "Zelda", NormalizedValue = "ZELDA", Category = "PERSON", Token = "[PERSON_005]", Ignored = true, SourceFileIds = new() { File1 } });
        check("Fragment: a value the analyst already kept for this category is not raised again", FragmentGuard.Find("Zelda called.", kept, File1).Count == 0);
        var unapproved = new List<Mapping> { new() { OriginalValue = "Zelda Quimby-Orsino", NormalizedValue = "ZELDA QUIMBY-ORSINO", Category = "PERSON", Token = "[PERSON_001]", Approved = false } };
        check("Fragment: only approved mappings are guarded", FragmentGuard.Find("Zelda called.", unapproved, File1).Count == 0);
        var contextual = Project1(); contextual[0].RequiresContextReview = true;
        check("Fragment: a mapping that still needs a context decision is not guarded (it is not replaced either)", FragmentGuard.Find("Zelda called.", contextual, File1).Count == 0);
        check("Fragment: other categories are never guarded", FragmentGuard.Find("Vantablack called.", new List<Mapping> { Approved("Vantablack Holdings Zeta", "SYSTEM", "[SYSTEM_001]") }, File1).Count == 0);

        // ---- hidden-channel text uses the same scan text
        var hidden = UiHiddenText.Append("Visible text only.", new[] { "comment: Quimby was here" });
        check("Fragment: a fragment in the appended hidden-channel text is found", FragmentGuard.Find(hidden, Project1(), File1).Any(c => c.Value == "Quimby"));

        // ---- the guard result is stable and does not mutate the mappings
        var stable = Project1();
        var before = JsonSerializer.Serialize(stable);
        FragmentGuard.Find(text, stable, File1); FragmentGuard.Find(text, stable, File1);
        check("Fragment: scanning does not change the mappings", JsonSerializer.Serialize(stable) == before);

        // ---- performance on a long document (about 100 pages)
        var paragraph = "The approval workflow routes each request to the validator when the amount exceeds the limit, then the requester receives a confirmation and the record is archived for the retention period defined in the policy. ";
        var sb = new System.Text.StringBuilder();
        var rng = new Random(13);
        for (var page = 0; page < 100; page++)
        {
            for (var i = 0; i < 14; i++) sb.Append(paragraph);
            if (page % 7 == 0) sb.Append("Quimby reviewed it. ");
            if (page % 11 == 0) sb.Append("Vantablack paid. ");
            sb.Append('\n');
        }
        var big = sb.ToString();
        var many = new List<Mapping>();
        for (var i = 0; i < 60; i++) many.Add(Approved("Firstname" + (char)('a' + i % 26) + i + " Surname" + (char)('a' + (i * 7) % 26) + i, "PERSON", "[PERSON_" + (100 + i) + "]"));
        many.AddRange(Project1());
        FragmentGuard.Find(big, many, File1);
        var timer = Stopwatch.StartNew();
        var bigFound = FragmentGuard.Find(big, many, File1);
        timer.Stop();
        Console.WriteLine("INFO fragment guard on " + big.Length + " characters with " + many.Count + " approved mappings: " + timer.ElapsedMilliseconds + " ms, " + bigFound.Count + " findings");
        check("Fragment: a 100-page text with 62 approved names is scanned in under two seconds", timer.ElapsedMilliseconds < 2000, timer.ElapsedMilliseconds + " ms");
        check("Fragment: the long text yields exactly the planted fragments", Values(bigFound).SequenceEqual(new[] { "Quimby", "Vantablack" }), string.Join(",", Values(bigFound)));
    }
}

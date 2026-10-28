using Desensitizer;

// Stage S14: review-noise rules (category conflict resolution, usernames, decoys, boundaries). Every rule has a positive case
// (the noise is handled) and a negative case (a real identifier with a similar shape is still found). Fictional values only.
public static class ReviewNoiseTests
{
    private static Candidate C(string value, string category, string source, double confidence = 1, params string[] detection)
        => new() { Value = value, Category = category, Source = source, Confidence = confidence, DetectionSources = detection.ToList() };

    private static List<Candidate> Run(string text, params Candidate[] input) => FindingPostProcessor.Complete(text, input);
    private static Candidate? Find(List<Candidate> result, string value) => result.FirstOrDefault(c => Project.Normalize(c.Value) == Project.Normalize(value));
    private static bool Cat(List<Candidate> result, string value, string category) => Find(result, value)?.Category == category;

    public static void Run(Chk check)
    {
        CategoryConflicts(check);
        Usernames(check);
        Decoys(check);
        Boundaries(check);
    }

    private static void Boundaries(Chk c)
    {
        // Company suffix.
        var text = "Velmora Ltd. signs with Mirabend Foods Co., Ltd. Velmora Ltd. delivers.";
        var r = Run(text, C("Velmora", "ORGANIZATION", "Local AI", 0), C("Mirabend Foods Co", "ORGANIZATION", "AI Qwen", 0), C("Velmora Ltd", "ORGANIZATION", "AI entity graph", 0));
        c("S14 boundary: a company span followed by Ltd. is extended to the suffix", Find(r, "Velmora Ltd.") != null && Find(r, "Velmora") == null);
        c("S14 boundary: 'Co' before ', Ltd.' is extended to the full legal form", Find(r, "Mirabend Foods Co., Ltd.") != null);
        r = Run("Velmora Ltd. signs. Velmora will deliver.", C("Velmora", "ORGANIZATION", "Local AI", 0));
        c("S14 boundary negative: a short form that also stands alone is kept next to the extended form", Find(r, "Velmora") != null && Find(r, "Velmora Ltd.") != null);
        r = Run("Acme ships goods. Acme will answer.", C("Acme", "ORGANIZATION", "Local AI", 0));
        c("S14 boundary negative: a company with no suffix in the text is not changed", Find(r, "Acme") != null && r.Count == 1);

        r = Run("Fennimore Insurance (FNN) pays. FNN confirms.", C("Fennimore Insurance (FNN)", "VENDOR", "AI NuExtract", 0), C("FNN", "ORGANIZATION", "AI Qwen", 0));
        c("S14 boundary: a printed acronym in parentheses is not part of the company span", Find(r, "Fennimore Insurance") != null && Find(r, "Fennimore Insurance (FNN)") == null);
        r = Run("The Report (Draft) is open.", C("Report (Draft)", "PRODUCT", "AI Qwen", 0));
        c("S14 boundary negative: a parenthesised word that is not an acronym is untouched", Find(r, "Report (Draft)") != null || r.Count == 0);

        r = Run("Maria Santos. 3.1.1.1. IP address: 192.168.1.10", C("3.1.1.1", "IDENTIFIER", "Rules", 0.7), C("192.168.1.10", "IDENTIFIER", "Rules", 0.7));
        c("S14 boundary negative: a labelled network address is still found, an outline number is not", Find(r, "192.168.1.10") != null && Find(r, "3.1.1.1") == null, string.Join(" ; ", r.Select(x => x.Category + ":" + x.Value)));

        // Address city.
        text = "Office: 274 Sampaguita Drive, Quezon City. Branch: Tower 2, 5 Ayala Avenue, Makati City.";
        r = Run(text, C("274 Sampaguita Drive, Quezon City", "ADDRESS", "Privacy Filter", 1), C("Tower 2, 5 Ayala Avenue, Makati City", "ADDRESS", "Privacy Filter", 1));
        c("S14 boundary: an appended city is stripped from a street address", Find(r, "274 Sampaguita Drive") != null && Find(r, "274 Sampaguita Drive, Quezon City") == null);
        c("S14 boundary negative: an address without a plain street-plus-city shape is kept whole", Find(r, "Tower 2, 5 Ayala Avenue, Makati City") != null);

        // Reversed names.
        text = "Approver: Reyes, Marites. Reviewer: Marites Reyes.";
        r = Run(text, C("Reyes", "PERSON", "Privacy Filter", 1), C("Marites", "PERSON", "Privacy Filter", 1), C("Marites Reyes", "PERSON", "Privacy Filter", 1));
        c("S14 boundary: 'Surname, Given' is expanded across the comma when both parts are people", Find(r, "Reyes, Marites") != null);
        r = Run("Acme, Manila office opens.", C("Acme", "PERSON", "Privacy Filter", 1));
        c("S14 boundary negative: a comma between a person-like word and a place is not a reversed name", Find(r, "Acme, Manila") == null);

        // Redundant fragments.
        text = "Write to benigno.aquino@ironmarlow.example. Benigno Aquino approved.";
        r = Run(text, C("benigno.aquino@ironmarlow.example", "EMAIL", "Rules", 0.7), C("benigno.aquino", "PERSON", "Privacy Filter", 1), C("ironmarlow", "ORGANIZATION", "Local AI", 0), C("Benigno Aquino", "PERSON", "Privacy Filter", 1));
        c("S14 boundary: a mail handle that only occurs inside an email address is not a separate finding", Find(r, "benigno.aquino") == null && Find(r, "ironmarlow") == null);
        c("S14 boundary: the email and the full name remain", Find(r, "benigno.aquino@ironmarlow.example") != null && Find(r, "Benigno Aquino") != null);
        r = Run("Mail ana@acme.example. Acme also ships alone. Acme pays.", C("ana@acme.example", "EMAIL", "Rules", 0.7), C("Acme", "ORGANIZATION", "Local AI", 0));
        c("S14 boundary negative: a name that also stands alone in the text is kept", Find(r, "Acme") != null);
        r = Run("Maria Santos met Santos again. Santos left.", C("Maria Santos", "PERSON", "Privacy Filter", 1), C("Santos", "PERSON", "Privacy Filter", 1));
        c("S14 boundary negative: a surname that also stands alone is kept", Find(r, "Santos") != null);
    }

    private static void Decoys(Chk c)
    {
        var text = "Amount exceeds 1000 is escalated to Fennimore Approver. Fennimore Insurance owns it.";
        var r = Run(text, C("Amount exceeds 1000 is escalated to Fennimore Approver", "SYSTEM", "AI Qwen", 0), C("Fennimore Insurance", "ORGANIZATION", "GLiNER", 0.8));
        c("S14 decoy: a business rule proposed as a system is not a finding", r.All(x => !x.Value.StartsWith("Amount")), string.Join(" ; ", r.Select(x => x.Category + ":" + x.Value + ":" + x.Source)));
        r = Run("Velmora System Owner approves. Velmora Ltd. signs.", C("Velmora System Owner", "SYSTEM", "AI Qwen", 0), C("Velmora Ltd.", "ORGANIZATION", "GLiNER", 0.8));
        c("S14 decoy: a company followed by a role keeps only the company", Find(r, "Velmora System Owner") == null && Find(r, "Velmora") != null);
        r = Run("Role: Reviewer. The Finance Validator signs.", C("Reviewer", "PERSON", "Local AI", 0), C("Finance Validator", "PROJECT", "AI Qwen", 0));
        c("S14 decoy: role names alone are not findings", r.Count == 0);
        r = Run("Contact Reviewer Santos today.", C("Reviewer Santos", "PERSON", "Privacy Filter", 1));
        c("S14 decoy negative: a person whose name follows a role word is still found", Find(r, "Reviewer Santos") != null);

        var tech = "Hosted on PostgreSQL 15.4 with Kubernetes 1.29, Docker and .NET 8 and Oracle 19c. Oracle 19c is public.";
        r = Run(tech, C("PostgreSQL 15.4", "SYSTEM", "AI Qwen", 0), C("Kubernetes 1.29", "SYSTEM", "AI Qwen", 0), C("Docker", "PRODUCT", "AI Gemma", 0), C(".NET 8", "PRODUCT", "AI Gemma", 0),
            C("19c", "IDENTIFIER", "AI Gemma", 0), C("Oracle", "VENDOR", "AI Qwen", 0), C("NET", "ORGANIZATION", "Presidio", 0.8), C("Oracle 19c", "SYSTEM", "AI Qwen", 0));
        c("S14 decoy: public technologies with versions are not findings", r.Count == 0, string.Join(" ; ", r.Select(x => x.Category + ":" + x.Value)));
        r = Run("Orion Gateway 2.1 runs on Docker. Orion Gateway 2.1 is private.", C("Orion Gateway 2.1", "SYSTEM", "AI Qwen", 0), C("Orion Gateway", "SYSTEM", "AI Gemma", 0));
        c("S14 decoy negative: a private system with a version is still a finding", Find(r, "Orion Gateway") != null || Find(r, "Orion Gateway 2.1") != null);
        r = Run("Oracle signed the vendor agreement.", C("Oracle", "VENDOR", "AI Qwen", 0));
        c("S14 decoy negative: a vendor name without a version is still reported", Find(r, "Oracle") != null);

        r = Run("See R-11 and FR-4002. Employee ID: EMP-74215. Version 2.4.1 is final. Account Number: 4455667788.",
            C("R-11", "IDENTIFIER", "AI Qwen", 0), C("FR-4002", "IDENTIFIER", "AI Qwen", 0), C("Version 2.4.1", "IDENTIFIER", "AI Qwen", 0), C("EMP-74215", "IDENTIFIER", "Rules", 0.7), C("4455667788", "IDENTIFIER", "Rules", 0.7));
        c("S14 decoy: requirement ids and version labels are not findings", Find(r, "R-11") == null && Find(r, "FR-4002") == null && Find(r, "Version 2.4.1") == null);
        c("S14 decoy negative: employee ids and account numbers are still found", Find(r, "EMP-74215") != null && Find(r, "4455667788") != null);
        r = Run("Amount exceeds 1000 and review within 5 business days.", C("1000", "IDENTIFIER", "AI Gemma", 0), C("5 business days", "IDENTIFIER", "AI Gemma", 0));
        c("S14 decoy: a threshold and a quantity phrase are not identifiers", r.Count == 0);
        r = Run("Case ref 1000 belongs to the claimant.", C("1000", "IDENTIFIER", "AI Gemma", 0));
        c("S14 decoy negative: a number outside a threshold phrase is still reported", Find(r, "1000") != null);

        r = Run("Revision 22 September 2025 by the team. Date of birth: 12 March 1985.", C("22 September 2025", "DATE", "Privacy Filter", 1), C("12 March 1985", "DATE", "Privacy Filter", 1));
        c("S14 decoy: a revision date without person context is not a finding", Find(r, "22 September 2025") == null);
        c("S14 decoy negative: a birth date is still a finding", Find(r, "12 March 1985") != null);
    }

    private static void CategoryConflicts(Chk c)
    {
        var text = "Iron Marlow GmbH signs the contract. Iron Marlow will deliver Project Basilisk and the Nightjar Gateway.";
        var r = Run(text,
            C("Iron Marlow", "PERSON", "Privacy Filter", 1, "GLiNER", "Privacy Filter"),
            C("Iron Marlow", "ORGANIZATION", "Local AI", 0),
            C("Iron Marlow GmbH", "ORGANIZATION", "GLiNER", 0.8, "GLiNER"));
        c("S14 category: a short company form labelled PERSON by Privacy Filter becomes ORGANIZATION when a reviewer and the longer company name agree", Cat(r, "Iron Marlow", "ORGANIZATION"));
        c("S14 category: that resolution is quiet (no review flag) when the evidence is clear", Find(r, "Iron Marlow")?.RequiresContextReview == false);

        r = Run(text, C("Project Basilisk", "PERSON", "Presidio", 0.85), C("Project Basilisk", "PROJECT", "Local AI", 0));
        c("S14 category: Project <Name> labelled PERSON becomes PROJECT", Cat(r, "Project Basilisk", "PROJECT"));
        r = Run(text, C("Nightjar Gateway", "PERSON", "Presidio", 0.85), C("Nightjar Gateway", "SYSTEM", "Local AI", 0));
        c("S14 category: <Name> Gateway labelled PERSON becomes SYSTEM", Cat(r, "Nightjar Gateway", "SYSTEM"));
        r = Run(text, C("Project Basilisk", "PERSON", "Presidio", 0.85));
        c("S14 category: a lone Project <Name> labelled PERSON by Presidio only is relabelled PROJECT", Cat(r, "Project Basilisk", "PROJECT"));

        // Negative: a real person with a middle initial is not turned into a company by a single reviewer's wrong label.
        var person = "Maria T. Santos approved the request.";
        r = Run(person, C("Maria T. Santos", "PERSON", "Privacy Filter", 1, "Presidio", "Privacy Filter"), C("Maria T. Santos", "ORGANIZATION", "Local AI", 0));
        c("S14 category: a person with a middle initial stays PERSON against one wrong reviewer label", Cat(r, "Maria T. Santos", "PERSON"));
        c("S14 category: that close call is kept for review, not silent", Find(r, "Maria T. Santos")?.RequiresContextReview == true);
        // Negative: a plain two-word name with no company evidence stays PERSON.
        r = Run("Contact Jordan Hayes today.", C("Jordan Hayes", "PERSON", "Privacy Filter", 1, "Presidio", "Privacy Filter"), C("Jordan Hayes", "PERSON", "Local AI", 0));
        c("S14 category: an ordinary person name without competing labels is untouched", Cat(r, "Jordan Hayes", "PERSON") && Find(r, "Jordan Hayes")?.RequiresContextReview == false);
        // Negative: a short first name of a real person is not relabelled as the company that shares its first word.
        r = Run("Jordan Hayes works at Jordan Metals Inc. Jordan will call.",
            C("Jordan", "PERSON", "Privacy Filter", 1, "Presidio", "Privacy Filter"),
            C("Jordan", "PERSON", "Local AI", 0),
            C("Jordan Hayes", "PERSON", "Privacy Filter", 1),
            C("Jordan Metals Inc.", "ORGANIZATION", "GLiNER", 0.8));
        c("S14 category: a first name that two reviewers call PERSON and that starts a real person name is not taken over by a company", Cat(r, "Jordan", "PERSON"));

        // Email versus URL: the deterministic email shape wins over a reviewer URL label.
        r = Run("Write to ana.reyes@acme.example now.", C("ana.reyes@acme.example", "EMAIL", "Privacy Filter", 1, "Rules", "Privacy Filter"), C("ana.reyes@acme.example", "URL", "Local AI", 0));
        c("S14 category: an email address labelled URL by a reviewer stays EMAIL", Cat(r, "ana.reyes@acme.example", "EMAIL"));
    }

    private static void Usernames(Chk c)
    {
        var text = "Liza D. Mercado owns the account. Username: ldmercado\nEmployee ID: EMP-74215";
        var r = Run(text, C("ldmercado", "PERSON", "Privacy Filter", 1, "Privacy Filter"), C("Liza D. Mercado", "PERSON", "Privacy Filter", 1));
        c("S14 username: first initials plus surname labelled PERSON becomes IDENTIFIER", Cat(r, "ldmercado", "IDENTIFIER"));
        c("S14 username: the full name next to it stays PERSON", Cat(r, "Liza D. Mercado", "PERSON"));

        // Without the Username label, shape plus the person's own surname and initials is enough.
        text = "Liza Q. Pascual signs off. The login lqpascual is shared by the team.";
        r = Run(text, C("lqpascual", "PERSON", "Privacy Filter", 1), C("Liza Q. Pascual", "PERSON", "Privacy Filter", 1));
        c("S14 username: shape plus matching person evidence, no label needed", Cat(r, "lqpascual", "IDENTIFIER"));

        // Label context alone is enough for any token.
        r = Run("Login: zzquux77 is required.", C("zzquux77", "PERSON", "Local AI", 0));
        c("S14 username: a Login label forces IDENTIFIER", Cat(r, "zzquux77", "IDENTIFIER"));

        // Negatives: ordinary lowercase words, an email handle and a token with no person evidence.
        r = Run("The manager will review. Contact maria for details.", C("manager", "PERSON", "Local AI", 0), C("maria", "PERSON", "Privacy Filter", 1));
        c("S14 username: an ordinary lowercase word is not promoted to IDENTIFIER", r.All(x => x.Category != "IDENTIFIER"));
        r = Run("Mail maria.santos@acme.example today. Maria Santos replies.", C("maria.santos", "PERSON", "Privacy Filter", 1), C("Maria Santos", "PERSON", "Privacy Filter", 1));
        c("S14 username: an email handle is not promoted to IDENTIFIER", !Cat(r, "maria.santos", "IDENTIFIER"));
        r = Run("The lmercado token is mentioned once.", C("lmercado", "PERSON", "Privacy Filter", 1));
        c("S14 username: a lowercase token with no person or label evidence stays as reported", Cat(r, "lmercado", "PERSON"));
        c("S14 username: IsUsernameCandidate rejects a word that merely ends like a surname", !ReviewNoise.IsUsernameCandidate("the unmercado plan", "unmercado", new[] { C("Liza D. Mercado", "PERSON", "Privacy Filter", 1) }));
    }
}

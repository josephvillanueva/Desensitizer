using System.Text;

namespace SyntheticBenchmark;

public sealed class Rng
{
    private ulong _s;
    public Rng(long seed) { _s = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0x1234567UL); if (_s == 0) _s = 1; NextU(); NextU(); }
    public ulong NextU() { _s ^= _s >> 12; _s ^= _s << 25; _s ^= _s >> 27; return unchecked(_s * 0x2545F4914F6CDD1DUL); }
    public int Next(int n) => (int)(NextU() % (ulong)n);
    public bool Chance(double p) => (NextU() >> 11) / (double)(1UL << 53) < p;
    public T Pick<T>(IReadOnlyList<T> l) => l[Next(l.Count)];
    public List<T> Shuffle<T>(IEnumerable<T> src)
    {
        var l = src.ToList();
        for (var i = l.Count - 1; i > 0; i--) { var j = Next(i + 1); (l[i], l[j]) = (l[j], l[i]); }
        return l;
    }
    public string Digits(int n) { var sb = new StringBuilder(); for (var i = 0; i < n; i++) sb.Append((char)('0' + Next(10))); return sb.ToString(); }
}

public sealed class CellSpec
{
    public string Text = "";
    public int Span = 1;
    public string VMerge = "";   // "", "restart", "continue"
    public bool Bold;
    public CellSpec(string text, int span = 1, string vmerge = "", bool bold = false) { Text = text; Span = span; VMerge = vmerge; Bold = bold; }
}
public abstract class Block { }
public sealed class ParaBlock : Block { public string Text = ""; public bool Heading; public ParaBlock(string t, bool h = false) { Text = t; Heading = h; } }
public sealed class TableBlock : Block { public List<List<CellSpec>> Rows = new(); }

public sealed class GeneratedDoc
{
    public List<Block> Blocks = new();
    public DocTruth Truth = new();
}

// Deterministic generator. The same (seed, docIndex, entities) always yields the same document and truth.
public static class Generator
{
    // Set SYNTHBENCH_ACCENTS=1 to add fictional accented names to the pools (changes the random draw, so it has its own version tag).
    public static readonly bool Accents = Environment.GetEnvironmentVariable("SYNTHBENCH_ACCENTS") == "1";
    public static readonly string Version = Accents ? "1-accents" : "1";

    static readonly string[] PhFirst = Accents ? new[] { "Maria", "Jose", "Juan", "Ana", "Luz", "Ramon", "Teresita", "Rogelio", "Cristina", "Eduardo", "Liza", "Benigno", "Corazon", "Alfredo", "Marites", "Danilo", "Rosario", "Gerardo", "Imelda", "Narciso", "José", "María", "Ramón", "Inés" } : new[] { "Maria", "Jose", "Juan", "Ana", "Luz", "Ramon", "Teresita", "Rogelio", "Cristina", "Eduardo", "Liza", "Benigno", "Corazon", "Alfredo", "Marites", "Danilo", "Rosario", "Gerardo", "Imelda", "Narciso"};
    static readonly string[] WestFirst = Accents ? new[] { "Hannah", "Oliver", "Sophie", "Lukas", "Fiona", "Marcus", "Elena", "Tobias", "Ingrid", "Callum", "Greta", "Dominic", "Nadia", "Rupert", "Astrid", "Felix", "Zoë", "Renée", "Jürgen", "Søren" } : new[] { "Hannah", "Oliver", "Sophie", "Lukas", "Fiona", "Marcus", "Elena", "Tobias", "Ingrid", "Callum", "Greta", "Dominic", "Nadia", "Rupert", "Astrid", "Felix"};
    static readonly string[] PhSurname = Accents ? new[] { "Santos", "Reyes", "Cruz", "Bautista", "Ocampo", "Mercado", "Villareal", "Dizon", "Castillo", "Aquino", "Pascual", "Soriano", "Tolentino", "Navarro", "de la Cruz", "dela Rosa", "del Rosario", "de Guzman", "delos Santos", "Manalo", "Quiambao", "Macaraeg", "Lacson", "Evangelista", "Peña", "Muñoz", "Núñez", "Hernández" } : new[] { "Santos", "Reyes", "Cruz", "Bautista", "Ocampo", "Mercado", "Villareal", "Dizon", "Castillo", "Aquino", "Pascual", "Soriano", "Tolentino", "Navarro", "de la Cruz", "dela Rosa", "del Rosario", "de Guzman", "delos Santos", "Manalo", "Quiambao", "Macaraeg", "Lacson", "Evangelista"};
    static readonly string[] WestSurname = Accents ? new[] { "van Dijk", "van der Berg", "von Eisen", "de Vries", "O'Connell", "Fitzgerald", "Hargreaves", "Lindqvist", "Thornbury", "Ashworth", "Kowalczyk", "Brandvold", "della Rocca", "Pemberton", "Holloway", "Eriksen", "Müller", "Dvořák", "Åkesson", "Łukasiewicz" } : new[] { "van Dijk", "van der Berg", "von Eisen", "de Vries", "O'Connell", "Fitzgerald", "Hargreaves", "Lindqvist", "Thornbury", "Ashworth", "Kowalczyk", "Brandvold", "della Rocca", "Pemberton", "Holloway", "Eriksen"};
    static readonly string[] Stems = { "Brightmoor", "Quillfeather", "Tallowgate", "Harborlight", "Ostrander", "Velmora", "Kestrelbrook", "Dunmore", "Zephyrine", "Altamira", "Corvane", "Ludwick", "Mirabend", "Thistledown", "Rookwood", "Saltmarsh", "Fennimore", "Gallowmere", "Wrenfield", "Cindermoor", "Larkspur Bay", "Blue Harbor", "Iron Marlow", "Golden Tern" };
    static readonly string[] Second = { "Logistics", "Holdings", "Foods", "Systems", "Treasury Services", "Trading", "Shipping", "Energy", "Insurance", "Telecom", "Capital", "Manufacturing" };
    static readonly string[] Suffixes = { "Inc.", "Corp.", "Ltd.", "LLC", "GmbH", "Co., Ltd.", "Pte. Ltd." };
    static readonly string[] CodeNames = { "Nightjar", "Halcyon", "Kingfisher", "Tamarind", "Basilisk", "Quasar", "Marigold", "Sparrowhawk", "Cobaltine", "Junebug", "Starling", "Mockingbird", "Gossamer", "Peregrine" };
    static readonly string[] SysWord = { "Portal", "Platform", "Ledger", "Gateway", "Hub", "Engine" };
    static readonly string[] PhStreets = { "Sampaguita", "Narra", "Molave", "Acacia", "Ilang-Ilang", "Dama de Noche" };
    static readonly string[] WestStreets = { "Elmwood", "Larkspur", "Hawthorne", "Birchfield", "Cranmere", "Alderbrook" };
    static readonly string[] StreetType = { "Street", "Avenue", "Road", "Lane", "Drive" };
    static readonly string[] Techs = { "React.js 18", "PostgreSQL 15.4", ".NET 8", "Kubernetes 1.29", "Microsoft Word", "Oracle 19c", "Angular 17", "Redis 7.2", "Python 3.11", "Docker" };
    static readonly string[] Roles = { "Approver", "Manager", "Business Analyst", "Finance Validator", "Reviewer", "Administrator", "Project Manager", "System Owner" };
    static readonly string[] Headings = { "Business Rules", "Scope", "Assumptions", "Approval Workflow", "Document Control", "Revision History", "Introduction" };
    static readonly string[] DocTypes = { "Functional Design Specification", "Business Requirement Document", "Technical Design Document" };
    static readonly string[] BannedAcronyms = { "API", "SQL", "SSO", "VPN", "UAT", "SMS", "PDF", "ERP", "CRM", "SLA", "KPI", "FAQ", "URL", "SDK", "CSV" };

    sealed class Person
    {
        public string Id = "", First = "", Mi = "", Surname = "", Handle = "", Email = "", Phone = "", EmpId = "";
        public Company Employer = null!;
        public bool Ph;
        public string Canonical => Mi.Length > 0 ? $"{First} {Mi}. {Surname}" : $"{First} {Surname}";
        public string Reversed => Mi.Length > 0 ? $"{Surname}, {First} {Mi}." : $"{Surname}, {First}";
        public string Initial => $"{First[0]}. {Surname}";
        public string DropMiddle => $"{First} {Surname}";
    }
    sealed class Company
    {
        public string Id = "", Stem = "", Word2 = "", Suffix = "", Domain = "", Acronym = "", Street = "", Phone = "", AcctNo = "";
        public bool AcronymIntroduced;
        public string NoSuffix => Word2.Length > 0 ? $"{Stem} {Word2}" : Stem;
        public string Full => Suffix.Length > 0 ? $"{NoSuffix} {Suffix}" : NoSuffix;
    }
    sealed class Sys
    {
        public string Id = "", Code = "", Full = "";
    }

    sealed class Ctx
    {
        public Rng R = null!;
        public DocTruth T = new();
        public List<Block> Blocks = new();
        readonly Dictionary<string, SensitiveSpan> _sens = new();
        readonly HashSet<string> _pres = new();
        readonly HashSet<string> _neut = new();
        public string S(string value, string cat, string entity, string kind)
        {
            var key = Text.Norm(value) + "|" + Text.Label(cat);
            if (!_sens.TryGetValue(key, out var s))
            {
                s = new SensitiveSpan { Value = value, Category = cat, EntityId = entity, AliasKind = kind };
                _sens[key] = s; T.Sensitive.Add(s);
            }
            s.Occurrences++;
            return value;
        }
        public string K(string value, string kind)
        {
            if (_pres.Add(Text.Norm(value))) T.Preserve.Add(new PreserveSpan { Value = value, Kind = kind });
            return value;
        }
        public string N(string value) { if (_neut.Add(Text.Norm(value))) T.Neutral.Add(value); return value; }
    }

    static string Ascii(string s) => new string(s.Normalize(System.Text.NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray().Select(c => c == 'ø' ? 'o' : c == 'Ø' ? 'O' : c == 'ł' ? 'l' : c == 'Ł' ? 'L' : c).Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());

    public static GeneratedDoc Generate(int seed, int docIndex, int entities)
    {
        var r = new Rng(seed * 1000L + docIndex + 17);
        var c = new Ctx { R = r };
        c.T.Id = $"s{seed}-d{docIndex + 1}";
        c.T.File = c.T.Id + ".docx";
        c.T.Seed = seed;
        c.T.EntityCount = entities;

        var nPeople = Math.Max(2, (int)Math.Round(entities * 0.5));
        var nCompanies = Math.Max(1, (int)Math.Round(entities * 0.3));
        var nSystems = Math.Max(1, entities - nPeople - nCompanies);
        var seq = 0;

        // Companies: unique stems and acronyms per document.
        var stems = r.Shuffle(Stems).Take(nCompanies).ToList();
        var companies = new List<Company>();
        foreach (var stem in stems)
        {
            var co = new Company { Id = $"{c.T.Id}-org{++seq}", Stem = stem };
            if (r.Chance(0.7)) co.Word2 = r.Pick(Second);
            if (r.Chance(0.6)) co.Suffix = r.Pick(Suffixes);
            co.Domain = Ascii(stem) + ".example";
            if (r.Chance(0.6))
            {
                var letters = new string(stem.Where(char.IsLetter).ToArray()).ToUpperInvariant();
                var consonants = new string(letters.Where(ch => "AEIOU".IndexOf(ch) < 0).ToArray());
                var acr = consonants.Length >= 3 ? consonants.Substring(0, 3) : letters.Substring(0, 3);
                if (!BannedAcronyms.Contains(acr) && !companies.Any(x => x.Acronym == acr)) co.Acronym = acr;
            }
            var ph = r.Chance(0.5);
            co.Street = $"{1 + r.Next(480)} {r.Pick(ph ? PhStreets : WestStreets)} {r.Pick(StreetType)}";
            co.Phone = ph ? $"+63 2 8555 01{r.Digits(2)}" : $"+1 555 010 01{r.Digits(2)}";
            co.AcctNo = r.Digits(10);
            companies.Add(co);
        }

        // People: unique surnames per document, mix of Philippine and Western naming.
        var people = new List<Person>();
        var usedSurnames = new HashSet<string>();
        for (var i = 0; i < nPeople; i++)
        {
            var ph = r.Chance(0.6);
            string surname;
            var guard = 0;
            do { surname = r.Pick(ph ? PhSurname : WestSurname); }
            while (usedSurnames.Any(u => Text.Norm(u).Contains(Text.Norm(surname)) || Text.Norm(surname).Contains(Text.Norm(u))) && ++guard < 200);
            usedSurnames.Add(surname);
            var first = r.Pick(ph ? PhFirst : WestFirst);
            if (ph && r.Chance(0.2)) first += " " + r.Pick(new[] { "Theresa", "Grace", "Angelo", "Mae" });
            var p = new Person { Id = $"{c.T.Id}-per{++seq}", First = first, Surname = surname, Ph = ph, Employer = companies[i % companies.Count] };
            if (r.Chance(0.7)) p.Mi = ((char)('A' + r.Next(26))).ToString();
            var f0 = char.ToLowerInvariant(first[0]);
            p.Handle = $"{f0}{(p.Mi.Length > 0 ? p.Mi.ToLowerInvariant() : "")}{Ascii(surname)}";
            p.Email = $"{Ascii(first.Split(' ')[0])}.{Ascii(surname)}@{p.Employer.Domain}";
            p.Phone = ph ? r.Pick(new[] { $"+63 917 555 01{r.Digits(2)}", $"0917 555 01{r.Digits(2)}", $"(02) 8555 01{r.Digits(2)}" }) : $"(555) 010-01{r.Digits(2)}";
            p.EmpId = "EMP-" + r.Digits(5);
            people.Add(p);
        }

        var systems = new List<Sys>();
        foreach (var code in r.Shuffle(CodeNames).Take(nSystems))
            systems.Add(new Sys { Id = $"{c.T.Id}-sys{++seq}", Code = code, Full = r.Chance(0.5) ? $"Project {code}" : $"{code} {r.Pick(SysWord)}" });

        // Entity form helpers (each call registers one occurrence in the ground truth).
        string PName(Person p, int form)
        {
            switch (form % 4)
            {
                case 0: return c.S(p.Canonical, "PERSON", p.Id, "canonical");
                case 1: return c.S(p.Reversed, "PERSON", p.Id, "reversed");
                case 2: return c.S(p.Initial, "PERSON", p.Id, "initial");
                default:
                    return p.Mi.Length > 0 ? c.S(p.DropMiddle, "PERSON", p.Id, "dropMiddle") : c.S(p.Canonical, "PERSON", p.Id, "canonical");
            }
        }
        string PCanon(Person p) => c.S(p.Canonical, "PERSON", p.Id, "canonical");
        string PEmail(Person p) => c.S(p.Email, "EMAIL", p.Id, "email");
        string PPhone(Person p) => c.S(p.Phone, "PHONE", p.Id, "phone");
        string PHandle(Person p) => c.S(p.Handle, "IDENTIFIER", p.Id, "username");
        string PEmp(Person p) => c.S(p.EmpId, "IDENTIFIER", p.Id, "employeeId");
        string CFull(Company o) => c.S(o.Full, "ORGANIZATION", o.Id, "full");
        string CNoSuffix(Company o) => o.Suffix.Length > 0 ? c.S(o.NoSuffix, "ORGANIZATION", o.Id, "noSuffix") : CFull(o);
        string CShort(Company o) => o.Word2.Length > 0 || o.Suffix.Length > 0 ? c.S(o.Stem, "ORGANIZATION", o.Id, "short") : CFull(o);
        string CAcr(Company o) => o.Acronym.Length > 0 ? c.S(o.Acronym, "ORGANIZATION", o.Id, "acronym") : CShort(o);
        string CIntro(Company o)
        {
            if (o.Acronym.Length > 0 && !o.AcronymIntroduced) { o.AcronymIntroduced = true; return $"{CFull(o)} ({CAcr(o)})"; }
            return CFull(o);
        }
        string CAddr(Company o) => c.S(o.Street, "ADDRESS", o.Id, "street");
        string CPhone(Company o) => c.S(o.Phone, "PHONE", o.Id, "phone");
        string CAcct(Company o) => c.S(o.AcctNo, "IDENTIFIER", o.Id, "accountNumber");
        string SFull(Sys s) => c.S(s.Full, "PROJECT", s.Id, "full");
        string SShort(Sys s) => s.Full.StartsWith("Project ") || s.Full.Split(' ').Length > 1 ? c.S(s.Code, "PROJECT", s.Id, "short") : SFull(s);

        string Tech() => c.K(r.Pick(Techs), "tech");
        string Date()
        {
            var d = new DateTime(2025, 1, 1).AddDays(r.Next(600));
            return c.K(r.Pick(new[] { d.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture), d.ToString("yyyy-MM-dd") }), "date");
        }
        string Role() => c.K(r.Pick(Roles), "role");
        string Head(string h) => c.K(h, "heading");

        var docType = c.K(r.Pick(DocTypes), "doctype");
        var main = systems[0];
        var customer = companies[0];

        // Title block.
        c.Blocks.Add(new ParaBlock($"{docType} for {SFull(main)}", true));
        c.Blocks.Add(new ParaBlock($"Prepared for {CFull(customer)}"));

        // Document control table.
        var control = new TableBlock();
        control.Rows.Add(new() { new("Document Title", bold: true), new($"{docType}") });
        control.Rows.Add(new() { new("Version", bold: true), new("Version " + c.K("2.4.1", "version")) });
        control.Rows.Add(new() { new("Effective date", bold: true), new(Date()) });
        control.Rows.Add(new() { new("Owner", bold: true), new($"{CShort(customer)} {Role()} group") });
        c.K("Document Title", "label"); c.K("Effective date", "label");
        c.Blocks.Add(new ParaBlock(Head("Document Control"), true));
        c.Blocks.Add(control);

        // Revision history.
        c.Blocks.Add(new ParaBlock(Head("Revision History"), true));
        var rev = new TableBlock();
        rev.Rows.Add(new() { new("Version", bold: true), new("Date", bold: true), new("Author", bold: true), new("Description", bold: true) });
        var descs = new[] { "Initial draft", $"Added approval workflow rules", $"Updated {Tech()} upgrade notes", "Clarified Amount exceeds 1000 rule", "Reviewed by the Approver" };
        foreach (var d in descs) c.K(d, "label");
        var nRev = Math.Min(5, 2 + people.Count / 3);
        for (var i = 0; i < nRev; i++)
            rev.Rows.Add(new() { new($"1.{i}"), new(Date()), new(PName(people[i % people.Count], i + r.Next(2))), new(descs[i % descs.Length]) });
        c.Blocks.Add(rev);

        // Introduction.
        c.Blocks.Add(new ParaBlock($"1. {Head("Introduction")}", true));
        foreach (var co in companies)
        {
            var owner = people.First(p => p.Employer == co);
            var sys = r.Pick(systems);
            c.Blocks.Add(new ParaBlock($"{CIntro(co)} is a participant in the {SFull(sys)} program. {PCanon(owner)} acts as its {Role()}, and the group may also be referred to as {CShort(co)}."));
        }
        foreach (var sys in systems.Skip(1))
            c.Blocks.Add(new ParaBlock($"{SFull(sys)} (also called {SShort(sys)}) is hosted by {CNoSuffix(r.Pick(companies))} and is built on {Tech()} with {Tech()}."));

        // Requirements.
        c.Blocks.Add(new ParaBlock($"2. {Head("Scope")}", true));
        c.Blocks.Add(new ParaBlock($"This {docType} covers version {c.K("2.4.1", "version")} of the solution. It is limited to the approval flow and excludes payment settlement."));
        c.Blocks.Add(new ParaBlock($"3.1.1.1 {Head("Approval Workflow")}", true));
        c.K("3.1.1.1", "outline");
        var req = 4000 + r.Next(3000);
        var n = people.Count * 2;
        for (var i = 0; i < n; i++)
        {
            var p = people[i % people.Count]; var co = r.Pick(companies); var sys = r.Pick(systems);
            var id = c.K($"FR-{req++}", "reqid");
            string text;
            switch (i % 12)
            {
                case 0: text = $"{id}: The system shall notify {PName(p, i)} at {PEmail(p)} {c.K("within 30 days", "quantity")} when the request is rejected."; break;
                case 1: text = $"{id}: The system shall route {c.K("amounts exceeding 1000", "threshold")} to {CNoSuffix(co)} for approval by the {Role()}."; break;
                case 2: text = $"{id}: The {SFull(sys)} shall integrate with {CShort(co)} through {Tech()}."; break;
                case 3: text = $"{id}: Only users with the {Role()} role may release payments. {c.K("Amount exceeds 1000", "threshold")} requires {Role()} review."; break;
                case 4: text = $"For questions about {SShort(sys)}, contact {PName(p, i + 1)} ({PEmail(p)}, {PPhone(p)})."; break;
                case 5: text = $"Account details. Username: {PHandle(p)}. Employee ID: {PEmp(p)}."; break;
                case 6: text = $"{id}: {c.K("Data retention period is 5 years", "quantity")}. {c.K("Maximum attachment size 12 MB", "quantity")}."; break;
                case 7: text = $"Send the signed form to {CShort(co)}, {CAddr(co)}, {c.N("Quezon City")}, or call {CPhone(co)}."; break;
                case 8: text = $"Account number: {CAcct(co)} must be validated by the {Role()} before {CAcr(co)} funds are released."; break;
                case 9: text = $"{id}: The system shall allow {PName(p, i)} to approve requests. {c.K("Approval Workflow", "heading")} steps are logged for {c.K("30 days", "quantity")}."; break;
                case 10: text = $"{CAcr(co)} staff such as {PName(p, i + 2)} must use {Tech()} to open the {SShort(sys)} reports."; break;
                default: text = $"{id}: The system shall display the status \"Pending\" until {PName(p, i)} signs off on {Date()}."; break;
            }
            c.Blocks.Add(new ParaBlock(text));
        }

        // Business rules table with merged cells.
        c.Blocks.Add(new ParaBlock(Head("Business Rules"), true));
        var rules = new TableBlock();
        rules.Rows.Add(new() { new(c.K("Business Rules", "heading"), span: 3, bold: true) });
        rules.Rows.Add(new() { new("ID", bold: true), new("Rule", bold: true), new("Owner", bold: true) });
        for (var i = 0; i < Math.Min(4, people.Count); i++)
        {
            var p = people[(i * 2) % people.Count]; var co = companies[i % companies.Count];
            rules.Rows.Add(new() { new(c.K($"R-{10 + i}", "reqid"), vmerge: "restart"), new($"Amount exceeds 1000 is escalated to {CShort(co)} {Role()}".Replace("Amount exceeds 1000", c.K("Amount exceeds 1000", "threshold"))), new(PName(p, i + 1)) });
            rules.Rows.Add(new() { new("", vmerge: "continue"), new($"{c.K("Review required", "label")} within {c.K("5 business days", "quantity")}"), new(PCanon(p)) });
        }
        c.Blocks.Add(rules);

        // Contact directory.
        c.Blocks.Add(new ParaBlock("Contact Directory", true));
        var dir = new TableBlock();
        dir.Rows.Add(new() { new("Name", bold: true), new("Email", bold: true), new("Phone", bold: true), new("Employee ID", bold: true), new("Company", bold: true) });
        c.K("Employee ID", "label"); c.K("Contact Directory", "heading");
        foreach (var p in people)
            dir.Rows.Add(new() { new(PCanon(p)), new(PEmail(p)), new(PPhone(p)), new(PEmp(p)), new(CNoSuffix(p.Employer)) });
        c.Blocks.Add(dir);

        // Sign-off.
        c.Blocks.Add(new ParaBlock("Sign-off Sheet", true));
        c.K("Sign-off Sheet", "heading");
        var sign = new TableBlock();
        sign.Rows.Add(new() { new("Name", bold: true), new("Role", bold: true), new("Company", bold: true), new("Date", bold: true) });
        foreach (var p in people.Take(Math.Max(3, people.Count / 2)))
            sign.Rows.Add(new() { new(PName(p, 0)), new(Role()), new(CAcr(p.Employer)), new(Date()) });
        c.Blocks.Add(sign);

        Validate(c);
        return new GeneratedDoc { Blocks = c.Blocks, Truth = c.T };
    }

    // The ground truth must be internally consistent: no preserve span may contain a sensitive value,
    // and every sensitive value must actually appear in the document text.
    static void Validate(Ctx c)
    {
        var all = Text.Norm(string.Join("\n", Flatten(c.Blocks)));
        foreach (var s in c.T.Sensitive.ToList())
            if (!all.Contains(Text.Norm(s.Value))) c.T.Sensitive.Remove(s);
        foreach (var p in c.T.Preserve.ToList())
        {
            var pn = Text.Norm(p.Value);
            if (!all.Contains(pn) || c.T.Sensitive.Any(s => pn.Contains(Text.Norm(s.Value))))
                c.T.Preserve.Remove(p);
        }
    }

    public static IEnumerable<string> Flatten(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is ParaBlock p) yield return p.Text;
            else if (b is TableBlock t) foreach (var row in t.Rows) foreach (var cell in row) yield return cell.Text;
        }
    }
}

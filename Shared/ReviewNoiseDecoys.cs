using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Decoys that must be preserved (public technology, requirement ids, roles, dates) and span-boundary repairs.
// Each rule is exercised both ways in tests/Detection2/ReviewNoiseTests.cs.
public static partial class ReviewNoise
{
    // ---- decoys ------------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> TechNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PostgreSQL", "Postgres", "MySQL", "MariaDB", "SQL Server", "MongoDB", "Redis", "Cassandra", "SQLite", "Kubernetes", "Docker",
        "Terraform", "Ansible", "Jenkins", "Kafka", "RabbitMQ", "Elasticsearch", "Nginx", "Tomcat", "Angular", "AngularJS",
        "React", "React.js", "ReactJS", "Vue", "Vue.js", "Svelte", "Python", "JavaScript", "TypeScript", "Node.js",
        ".NET", "ASP.NET", "Spring Boot", "Django", "Flask", "Linux", "Ubuntu", "Windows Server", "Android", "iOS",
        "Chrome", "Firefox", "PowerShell", "GitHub", "GitLab", "Azure", "AWS", "Power BI", "Next.js",
        "Express.js", "Laravel", "Oracle Database",
    };
    // Names that are ordinary words or may be a real counterparty: public technology only when a version follows.
    private static readonly HashSet<string> VersionedOnlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Oracle", "Microsoft", "SAP", "Java", "Node", "Edge", "Word", "Windows", "Office", "Go", "Swift", "Rust", "Ruby", "Apache",
    };
    private static readonly Regex VersionToken = new(@"^v?\d+(?:\.\d+){0,3}[A-Za-z]?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "PostgreSQL 15.4", "Kubernetes 1.29", ".NET 8", "Oracle 19c", "Docker": a public product name, with a version if given.
    public static bool IsVersionedPublicTechnology(string value)
    {
        var v = value.Trim();
        if (v.Length < 2) return false;
        var space = v.LastIndexOf(' ');
        if (space > 0 && VersionToken.IsMatch(v.Substring(space + 1)))
        {
            var name = v.Substring(0, space).Trim();
            return TechNames.Contains(name) || VersionedOnlyNames.Contains(name) || name.Equals("NET", StringComparison.OrdinalIgnoreCase);
        }
        return TechNames.Contains(v);
    }

    // A bare version or number ("18", "19c") or the "NET" of ".NET" whose every occurrence follows a public technology name.
    public static bool IsVersionOfPublicTechnology(string text, string value)
    {
        var v = value.Trim();
        var isNet = v.Equals("NET", StringComparison.OrdinalIgnoreCase);
        if (!isNet && !VersionToken.IsMatch(v)) return false;
        var ms = Regex.Matches(text, DocumentPrivacyPolicy.Pattern(v), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (ms.Count == 0) return false;
        foreach (Match m in ms)
        {
            var before = text.Substring(Math.Max(0, m.Index - 40), Math.Min(40, m.Index));
            if (isNet) { if (!before.EndsWith(".")) return false; continue; }
            var name = Regex.Match(before, @"([^\s]+)\s+v?$");
            if (!name.Success) return false;
            var word = name.Groups[1].Value;
            if (!(IsVersionedPublicTechnology(word) || VersionedOnlyNames.Contains(word))) return false;
        }
        return true;
    }

    private static readonly Regex TechFollower = new(@"^\s+(?:v?\d|Word|Excel|Office|Server|Database)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "Oracle" or "Microsoft" proposed as a vendor when every occurrence is the first word of "Oracle 19c" or "Microsoft Word".
    public static bool IsNameOfVersionedTechnology(string text, string value)
    {
        var v = value.Trim();
        if (!VersionedOnlyNames.Contains(v) && !TechNames.Contains(v)) return false;
        var ms = Regex.Matches(text, DocumentPrivacyPolicy.Pattern(v), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return ms.Count > 0 && ms.Cast<Match>().All(m => TechFollower.IsMatch(text.Substring(m.Index + m.Length, Math.Min(12, text.Length - m.Index - m.Length))));
    }

    // "Project X" or "X Gateway": the shape of a system name rather than a company.
    public static bool LooksLikeSystemName(string value)
    {
        var words = Regex.Split(value.Trim(), @"\s+").Where(w => w.Length > 0).ToArray();
        return (words.Length >= 2 && (words[0].Equals("Project", StringComparison.OrdinalIgnoreCase) || ReviewNoiseCategory.SystemWords.Contains(words[^1].TrimEnd('.', ',')))) || IsVersionedPublicTechnology(value);
    }

    private static readonly Regex RequirementId = new(@"^(?:N?FR|BR|SR|REQ|RQ|R)[-_ ]?\d{1,5}(?:\.\d+)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VersionLabel = new(@"^(?:version|ver\.?|release|revision|rev\.?|v)\s*\d+(?:\.\d+){1,3}$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuantityPhrase = new(@"^\d+\s+(?:business\s+|working\s+|calendar\s+)?(?:days?|weeks?|months?|years?|hours?|minutes?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ThresholdContext = new(@"(?:exceeds?|exceeding|above|over|greater than|more than|less than|below|under|at least|at most|minimum|maximum|threshold|limit|up to)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Requirement ids (R-11, FR-4002), version labels (Version 2.4.1) and quantity phrases (5 business days) are document
    // structure. Employee ids (EMP-74215) and account numbers have other shapes and are not matched.
    public static bool IsStructureToken(string value)
    {
        var v = value.Trim();
        return RequirementId.IsMatch(v) || VersionLabel.IsMatch(v) || QuantityPhrase.IsMatch(v);
    }

    // A short number that only ever follows a threshold word ("Amount exceeds 1000").
    public static bool IsThresholdNumber(string text, string value)
    {
        var v = value.Trim();
        if (!Regex.IsMatch(v, @"^\d{1,5}$")) return false;
        var ms = Regex.Matches(text, DocumentPrivacyPolicy.Pattern(v), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return ms.Count > 0 && ms.Cast<Match>().All(m => ThresholdContext.IsMatch(text.Substring(Math.Max(0, m.Index - 25), Math.Min(25, m.Index))));
    }

    private static readonly HashSet<string> RoleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Reviewer", "Approver", "Validator", "Administrator", "Owner", "Manager", "Analyst", "Director", "Officer", "Lead", "Supervisor",
        "Coordinator", "Specialist", "Engineer", "Developer", "Tester", "Auditor", "Requester", "Submitter", "Executive", "Assistant",
        "Controller", "Custodian", "Approvers", "Reviewers", "Validators",
    };
    private static readonly HashSet<string> RoleQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Finance", "Business", "Project", "System", "Product", "Team", "Department", "Senior", "Junior", "Chief", "Technical",
        "Operations", "Compliance", "Risk", "Security", "Data", "Quality", "Head", "Program", "Release", "Account", "Process",
    };

    // "Finance Validator", "Business Analyst", "Reviewer": operational roles, never identifying on their own.
    public static bool IsRoleOnly(string value)
    {
        var words = Regex.Split(value.Trim(), @"\s+").Where(w => w.Length > 0).ToArray();
        return words.Length is >= 1 and <= 4 && words.Any(RoleWords.Contains) && words.All(w => RoleWords.Contains(w) || RoleQualifiers.Contains(w));
    }

    // "Velmora System Owner" (a company followed by a role) keeps only the company: the role is business structure.
    public static bool TryStripTrailingRole(string value, out string stripped)
    {
        stripped = value;
        var words = Regex.Split(value.Trim(), @"\s+").Where(w => w.Length > 0).ToList();
        if (words.Count < 2 || !RoleWords.Contains(words[^1])) return false;
        var end = words.Count;
        while (end > 1 && (RoleWords.Contains(words[end - 1]) || RoleQualifiers.Contains(words[end - 1]))) end--;
        if (end == words.Count) return false;
        var rest = words.Take(end).ToList();
        if (rest.Any(w => !char.IsUpper(w[0]))) return false;
        stripped = string.Join(" ", rest);
        return true;
    }

    private static readonly Regex BirthContext = new(@"(?:birth|born|\bDOB\b|d\.o\.b|passport|licen[cs]e|identity|ID\s*(?:card|number))[^\r\n]{0,40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A date is only identifying with person context (birth date, dates of an identity document). Revision dates, meeting
    // dates and deadlines are business content and are preserved.
    public static bool IsNonIdentifyingDate(string text, Candidate candidate)
    {
        if (candidate.Source is "Explicit birth date" or "Date range") return false;
        if (!IsDateShape(candidate.Value)) return false;
        var ms = Regex.Matches(text, DocumentPrivacyPolicy.Pattern(candidate.Value, true), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        foreach (Match m in ms)
        {
            var before = text.Substring(Math.Max(0, m.Index - 60), Math.Min(60, m.Index));
            var nl = before.LastIndexOfAny(new[] { '\n', '\r' });
            if (nl >= 0) before = before.Substring(nl + 1);
            if (BirthContext.IsMatch(before)) return false;
        }
        return true;
    }

    // ---- boundaries --------------------------------------------------------------------------------------------------

    private static readonly Regex CompanyLegalEnd = new(@"(?:^|\s)(?:Inc|Corp|Ltd|Co|Limited|Corporation)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LegalTail = new(@"^(?:\.|,?\s+(?:Inc|Corp|Corporation|Ltd|Limited|LLC|Co|GmbH|LLP|PLC)\b\.?)+", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The legal suffix that follows an organization span in the text ("Velmora" before " Ltd.", "Corp" before "."). Returns the
    // distinct extended values and whether every occurrence was extended.
    public static List<string> LegalSuffixExtensions(string text, string value, out bool allExtended)
    {
        allExtended = false;
        var found = new List<string>();
        var ms = Regex.Matches(text, DocumentPrivacyPolicy.Pattern(value, true), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (ms.Count == 0) return found;
        var extended = 0;
        foreach (Match m in ms)
        {
            var after = text.Substring(m.Index + m.Length, Math.Min(16, text.Length - m.Index - m.Length));
            var tail = LegalTail.Match(after);
            if (!tail.Success) continue;
            // A sentence period after an ordinary word is not a suffix: it only counts after a legal-suffix word.
            if (tail.Value == "." && !CompanyLegalEnd.IsMatch(m.Value)) continue;
            extended++;
            var full = (m.Value + tail.Value).TrimEnd();
            if (!found.Contains(full, StringComparer.OrdinalIgnoreCase)) found.Add(full);
        }
        allExtended = extended == ms.Count;
        return found;
    }

    private static readonly Regex AddressWithCity = new(
        @"^(?:\p{Lu}[^,\d]{1,40},\s*)?(?<street>\d+[A-Za-z]?\s+[^,]+?\s(?:Street|St|Road|Rd|Avenue|Ave|Drive|Dr|Boulevard|Blvd|Lane|Ln|Highway|Hwy|Way|Court|Ct|Place|Plaza|Circle|Square|Terrace)\.?)(?:,\s*(?<city>\p{Lu}[\p{L}.'-]*(?:\s+\p{Lu}[\p{L}.'-]*){0,2}))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "274 Sampaguita Drive, Quezon City": the street is the identifying span, the appended city is public.
    public static bool TryStripAddressCity(string value, out string street)
    {
        var m = AddressWithCity.Match(value.Trim());
        street = m.Success ? m.Groups["street"].Value : value;
        return m.Success;
    }

    private static readonly Regex ReversedName = new(
        @"(?<![\p{L}])(?<sur>(?:(?:de|del|dela|delos|della|van|von|der|di|da|la)\s+)*\p{Lu}[\p{L}'-]+),[ \t]+(?<given>\p{Lu}[\p{L}'-]+(?:[ \t]+\p{Lu}\.)?)(?![\p{L}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "Reyes, Marites" when "Reyes" and "Marites" were each reported as a person: the whole reversed form is one name.
    public static IEnumerable<string> ReversedNames(string text, IEnumerable<Candidate> persons)
    {
        var known = persons.Where(c => c.Category == "PERSON").Select(c => Project.Normalize(c.Value).TrimEnd('.')).ToHashSet();
        foreach (Match m in ReversedName.Matches(text))
        {
            var sur = Project.Normalize(m.Groups["sur"].Value).TrimEnd('.');
            var given = Project.Normalize(m.Groups["given"].Value).TrimEnd('.');
            var surLast = sur.Split(' ').Last();
            var givenFirst = given.Split(' ').First();
            if ((known.Contains(sur) || known.Contains(surLast)) && (known.Contains(given) || known.Contains(givenFirst)))
                yield return m.Value;
        }
    }

    // A finding whose every occurrence lies inside a longer accepted finding is already covered by it (a surname inside a full
    // name, a mail handle inside an address). Values that also stand alone elsewhere in the document are kept.
    public static List<Candidate> DropRedundantFragments(string text, List<Candidate> kept)
    {
        var spans = new Dictionary<Candidate, List<(int Start, int End)>>(ReferenceEqualityComparer.Instance as IEqualityComparer<Candidate>);
        List<(int Start, int End)> Occ(Candidate c)
        {
            if (!spans.TryGetValue(c, out var list))
            {
                list = new List<(int, int)>();
                try { foreach (Match m in Regex.Matches(text, DocumentPrivacyPolicy.Pattern(c.Value, true), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) list.Add((m.Index, m.Index + m.Length)); }
                catch (RegexMatchTimeoutException) { }
                spans[c] = list;
            }
            return list;
        }
        var drop = new HashSet<Candidate>(ReferenceEqualityComparer.Instance as IEqualityComparer<Candidate>);
        foreach (var v in kept)
        {
            if (v.Category is "ROLE" or "DATE") continue;
            // Values added on purpose by a rule (an acronym beside its company, a labelled network address) keep their own mapping.
            if (v.Source is "Explicit username" or "Explicit birth date" or "Date range" or "Complete URL" or "Document entity context" or "Related acronym"
                or "Explicit legal company name" or "Document geographic context" or "Contract party" or "Contextual role" or "Explicit network address") continue;
            var own = Occ(v);
            if (own.Count == 0) continue;
            var containers = kept.Where(l => !ReferenceEquals(l, v) && l.Value.Length > v.Value.Length && l.Category != "ROLE" && l.Value.IndexOf(v.Value, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (containers.Count == 0) continue;
            if (own.All(o => containers.Any(l => Occ(l).Any(s => s.Start <= o.Start && s.End >= o.End)))) drop.Add(v);
        }
        return kept.Where(c => !drop.Contains(c)).ToList();
    }
}

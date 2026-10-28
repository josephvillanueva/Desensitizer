using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer
{
 // Complete related spans before they become analyst decisions. Partial URL and
 // date replacements can leave identifying fragments or damage document meaning.
 public static class FindingPostProcessor
 {
  private static readonly HashSet<string> GenericTimeUnits=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {"day","days","week","weeks","month","months","year","years"};
  private static readonly Regex Url=new Regex(@"(?<![\w@])(?:https?://|www\.|[A-Za-z0-9-]+\.)[A-Za-z0-9.-]+\.[A-Za-z]{2,}(?:/[^\s<>\""']*)?",RegexOptions.IgnoreCase|RegexOptions.Compiled);
  private static readonly Regex DateRange=new Regex(@"\b(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{4}\s*[-–—]\s*(?:(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{4}|Present)\b",RegexOptions.IgnoreCase|RegexOptions.Compiled);
  // Reference lines can identify a person even after their name is replaced.
  // Capture the title as a separate, reviewable contextual finding, not PERSON.
  private static readonly Regex ReferenceRole=new Regex(@"(?<!\w)(?:,\s*|[-–]\s*)(?<role>Commissioner\s+of\s+the\s+[A-Za-z ]{3,50}?\s+Division(?:\s*\(Retired\))?|Secretary|President)(?=\s*(?:[,;.\r\n]|$))",RegexOptions.IgnoreCase|RegexOptions.Compiled);
  private static readonly Regex RoleValue=new Regex(@"^(?:Commissioner\s+of\s+the\s+[A-Za-z ]{3,50}?\s+Division(?:\s*\(Retired\))?|Secretary|President)$",RegexOptions.IgnoreCase|RegexOptions.Compiled);
  private static readonly Regex ContractPartyName=new Regex(@"(?m)^\s*(?<name>[A-Z][A-Z .'-]{7,79}?[A-Z])\s*,\s*(?:(?:Filipino|Filipina|of legal age|single|married|widowed)\b)",RegexOptions.Compiled);

  public static List<Candidate> Complete(string text,IEnumerable<Candidate> input)
  {
   var findings=input.Where(x=>!string.IsNullOrWhiteSpace(x.Value)&&!PublicTechnologyPolicy.IsPublicTechnology(x.Value)&&!GenericTimeUnits.Contains(x.Value.Trim())).ToList();
   foreach(var finding in findings.Where(x=>x.Category=="PERSON"&&RoleValue.IsMatch(x.Value)))
   {
    finding.Category="ROLE";
    finding.Explanation="Distinctive reference title, not a person's name. Review whether it could reveal identity.";
   }
   var additions=new List<Candidate>();
   foreach(Match match in ContractPartyName.Matches(text))
   {
    var name=match.Groups["name"].Value.Trim();
    if(name.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<2)continue;
    additions.Add(new Candidate{Value=name,Category="PERSON",Confidence=0.94,Source="Contract party",Explanation="Name in a contract party clause. Review all occurrences and aliases before sharing."});
   }
   foreach(Match match in ReferenceRole.Matches(text))
   {
    var role=match.Groups["role"].Value.Trim();
    additions.Add(new Candidate{Value=role,Category="ROLE",Confidence=0.85,Source="Contextual role",Explanation="Reference title may identify a person when combined with surrounding details. Review before sharing."});
   }
   var introductions=new Dictionary<string,List<string>>();
   foreach(var finding in findings.Where(x=>x.Category=="ORGANIZATION"||x.Category=="VENDOR"||x.Category=="DEPARTMENT"))
   {
    foreach(Match name in Regex.Matches(text,Regex.Escape(finding.Value),RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(500)))
    {
     var suffix=text.Substring(name.Index+name.Length);
     if(!CompanyIdentity.TryReadIntroduction(suffix,out var value,out var quotedAcronym))continue;
     if(value.Equals(finding.Value,StringComparison.OrdinalIgnoreCase))continue;
     CompanyIdentity.RememberIntroduction(introductions,value,finding.Value,quotedAcronym);
     additions.Add(new Candidate{Value=value,Category=finding.Category,Confidence=0.85,Source="Related acronym",Explanation="Acronym printed beside a detected organization. Review together with its full name. "+CompanyIdentity.NoteFor(finding.Value,quotedAcronym)});
    }
   }
   foreach(Match range in DateRange.Matches(text))
   {
    findings.RemoveAll(f=>f.Category=="DATE"&&range.Value.IndexOf(f.Value,StringComparison.OrdinalIgnoreCase)>=0&&f.Value.Length<range.Length);
    additions.Add(new Candidate{Value=range.Value,Category="DATE",Confidence=0.9,Source="Date range",Explanation="Complete employment date range; replace together to preserve its meaning."});
   }
   foreach(Match url in Url.Matches(text))
   {
    if(!findings.Any(f=>f.Category=="URL"&&url.Value.IndexOf(f.Value,StringComparison.OrdinalIgnoreCase)>=0))continue;
    findings.RemoveAll(f=>f.Category=="URL"&&url.Value.IndexOf(f.Value,StringComparison.OrdinalIgnoreCase)>=0&&f.Value.Length<url.Length);
    additions.Add(new Candidate{Value=url.Value.TrimEnd('.',',',';',')'),Category="URL",Confidence=0.9,Source="Complete URL",Explanation="Full URL including its path; replacing only a fragment would leave a broken link."});
   }
   findings.AddRange(additions);
   var eligible=DocumentPrivacyPolicy.Apply(text,findings,true);
   ReviewDisagreement.Mark(text,eligible);
   return eligible.GroupBy(f=>Project.Normalize(f.Value)).Select(group=>
   {
    var best=group.OrderByDescending(f=>f.RequiresContextReview).ThenByDescending(f=>f.ConsensusVotes).ThenByDescending(f=>f.RecommendedReplace).ThenByDescending(f=>f.Confidence).First();
    best.RequiresContextReview=group.Any(f=>f.RequiresContextReview);
    CompanyIdentity.CarryIntroductions(best,introductions);
    if(best.RequiresContextReview)best.RecommendedReplace=false;
    best.DetectionSources=group.SelectMany(f=>f.DetectionSources??new List<string>()).Concat(group.Select(f=>f.Source)).Where(source=>!string.IsNullOrWhiteSpace(source)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    return best;
   }).ToList();
  }
 }
}

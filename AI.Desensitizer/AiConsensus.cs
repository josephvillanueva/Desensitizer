using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer
{
 public static class AiConsensus
 {
  private sealed class EntityCluster
  {
   public string Category {get;set;}="";
   public List<Candidate> Findings {get;}=new List<Candidate>();
  }

  public static List<Candidate> Resolve(string document,IEnumerable<IEnumerable<Candidate>> reviewerFindings,IEnumerable<Candidate>? detectorFindings=null)
  {
   var reviewers=reviewerFindings.Select(findings=>findings.Where(IsEligible).ToList()).ToList();
   var detectorList=(detectorFindings??Array.Empty<Candidate>()).Where(IsEligible).ToList();
   var clusters=new List<EntityCluster>();
   var support=new ShortFormSupport(reviewers,detectorList);
   foreach(var finding in reviewers.SelectMany(findings=>findings))
   {
    var cluster=clusters.FirstOrDefault(existing=>existing.Category.Equals(finding.Category,StringComparison.OrdinalIgnoreCase)&&existing.Findings.All(member=>AreAliases(finding.Category,finding.Value,member.Value)||support.AreSupportedShortForms(finding.Category,finding.Value,member.Value)));
    if(cluster==null){cluster=new EntityCluster{Category=finding.Category.ToUpperInvariant()};clusters.Add(cluster);}
    cluster.Findings.Add(finding);
   }

   var resolved=new List<Candidate>();
   foreach(var cluster in clusters)
   {
    var sources=cluster.Findings.Select(finding=>finding.Source).Where(source=>!string.IsNullOrWhiteSpace(source)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var votes=reviewers.Count(findings=>findings.Any(finding=>cluster.Findings.Contains(finding)));
    var representative=ChooseRepresentative(cluster.Findings);
    var entityKey=cluster.Category+"|"+Project.Normalize(representative.Value);
    var supportedDetectors=detectorList.Where(detector=>detector.Category.Equals(cluster.Category,StringComparison.OrdinalIgnoreCase)&&cluster.Findings.Any(finding=>AreAliases(cluster.Category,finding.Value,detector.Value)||support.AreSupportedShortForms(cluster.Category,finding.Value,detector.Value))).ToList();
    var detectorSupported=supportedDetectors.Any();
    var replace=votes>=2||(votes>=1&&detectorSupported&&IsDirectIdentifier(cluster.Category));
    // Votes describe agreement only. They are not measured accuracy percentages.
    var confidence=0.0;
    var allSources=sources.Concat(supportedDetectors.SelectMany(detector=>detector.DetectionSources??new List<string>())).Concat(supportedDetectors.Select(detector=>detector.Source)).Where(source=>!string.IsNullOrWhiteSpace(source)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    foreach(var variant in cluster.Findings.GroupBy(finding=>Project.Normalize(finding.Value)).Select(group=>group.OrderByDescending(finding=>finding.Value.Length).First()))
    {
     resolved.Add(new Candidate
     {
      Value=variant.Value,
      Category=cluster.Category,
      Confidence=confidence,
      Source=votes>=2?"AI consensus":variant.Source,
      DetectionSources=allSources,
      Explanation=votes>=2?votes+" of "+reviewers.Count+" local AI reviewers agreed on this interpretation. Agreement is not calibrated accuracy.":detectorSupported?"One local AI reviewer and an independent detector identified this direct identifier. Accuracy is not calibrated.":"One local AI reviewer identified this value. Review it before replacing.",
      EntityKey=entityKey,
      ConsensusVotes=votes,
      RecommendedReplace=replace
     });
    }
   }
   // One value labelled in several category classes (Privacy Filter says PERSON, three reviewers say ORGANIZATION):
   // choose by evidence first, so only close calls reach the disagreement flag below.
   var surviving=new HashSet<Candidate>(ReviewNoiseCategory.Resolve(document,resolved.Concat(detectorList),true),ReferenceEqualityComparer.Instance);
   resolved.RemoveAll(candidate=>!surviving.Contains(candidate));
   detectorList.RemoveAll(candidate=>!surviving.Contains(candidate));
   // "Reyes, Marites" printed in the document completes two separately reported parts into one name, which makes the parts redundant.
   foreach(var reversed in ReviewNoise.ReversedNames(document,resolved.Concat(detectorList).ToList()))
    if(!resolved.Concat(detectorList).Any(candidate=>Project.Normalize(candidate.Value)==Project.Normalize(reversed)))
     resolved.Add(new Candidate{Value=reversed,Category="PERSON",Confidence=0.9,Source="Reversed name",DetectionSources=new List<string>{"Reversed name"},Explanation="Surname, given name: both parts were reported as people, so the whole reversed form is one name.",EntityKey="PERSON|"+Project.Normalize(reversed)});
   // A surname or first name that only occurs inside a longer reported name is not a competing interpretation.
   var covered=new HashSet<Candidate>(ReviewNoise.DropRedundantFragments(document,resolved.Concat(detectorList).ToList()),ReferenceEqualityComparer.Instance);
   resolved.RemoveAll(candidate=>!covered.Contains(candidate));
   detectorList.RemoveAll(candidate=>!covered.Contains(candidate));
   var checkedFindings=ReviewDisagreement.Mark(document,resolved.Concat(detectorList)).Where(candidate=>resolved.Contains(candidate)).ToList();
   foreach(var candidate in checkedFindings.Where(candidate=>candidate.RequiresContextReview))candidate.RecommendedReplace=false;
   var expanded=ReviewDisagreement.Mark(document,ExpandDocumentVariants(document,checkedFindings));
   foreach(var candidate in expanded.Where(candidate=>candidate.RequiresContextReview))candidate.RecommendedReplace=false;
   return expanded;
  }

  public static bool AreAliases(string category,string left,string right)
  {
   if(Project.Normalize(left)==Project.Normalize(right))return true;
   category=category.ToUpperInvariant();
   if(category=="PERSON")return ArePersonAliases(left,right);
   if(category is "ORGANIZATION" or "VENDOR" or "DEPARTMENT")return AreOrganizationAliases(left,right);
   return false;
  }

  private static bool IsEligible(Candidate finding)=>!string.IsNullOrWhiteSpace(finding.Value)&&!string.IsNullOrWhiteSpace(finding.Category)&&!LocalAiReview.IsClearlyGenericCandidate(finding.Value,finding.Category);
  private static bool IsDirectIdentifier(string category)=>category.ToUpperInvariant() is "PERSON" or "ADDRESS" or "EMAIL" or "PHONE" or "URL" or "IDENTIFIER";

  private static Candidate ChooseRepresentative(IEnumerable<Candidate> findings)
  {
   return findings.OrderByDescending(finding=>PersonWords(finding.Value).Count).ThenByDescending(finding=>finding.Value.Length).First();
  }

  private static bool ArePersonAliases(string left,string right) => PersonIdentity.AreAliases(left,right);

  private static List<string> PersonWords(string value)
  {
   var ignored=new HashSet<string>(new[]{"DE","DEL","DELA","LA","VAN","VON","JR","SR","MR","MS","MRS","DR"});
   var words=Regex.Matches(value.ToUpperInvariant(),@"[\p{L}]+(?:['-][\p{L}]+)?").Cast<Match>().Select(match=>match.Value).Where(word=>!ignored.Contains(word)).ToList();
   if(words.Count>=3)
   {
    var joined=words[0]+words[^2];
    var initial=words[0][..1]+words[^2];
    if(words[^1]==joined||words[^1]==initial)words.RemoveAt(words.Count-1);
   }
   return words;
  }

  private static bool AreOrganizationAliases(string left,string right)
  {
   var a=OrganizationWords(left);var b=OrganizationWords(right);
   if(a.Count==0||b.Count==0)return false;
   var aInitials=string.Concat(a.Select(word=>word[0]));
   var bInitials=string.Concat(b.Select(word=>word[0]));
   var leftCompact=Regex.Replace(left.ToUpperInvariant(),@"[^A-Z0-9]","");
   var rightCompact=Regex.Replace(right.ToUpperInvariant(),@"[^A-Z0-9]","");
   if(leftCompact==bInitials||rightCompact==aInitials)return true;
   return AreLegalSuffixVariants(left,right);
  }

  // Legal forms of one name: after removing trailing legal suffixes the word
  // lists are identical ("Acme" and "Acme Corporation"). When both names carry
  // a legal suffix it must be the same kind, so "Acme Inc" and "Acme LLC" stay
  // separate entities, matching the token rule that different suffixes never
  // share a token. A single common word is never linked by this rule.
  private static bool AreLegalSuffixVariants(string left,string right)
  {
   var (a,aSuffix)=SplitLegalSuffix(left);var (b,bSuffix)=SplitLegalSuffix(right);
   if(a.Count==0||b.Count==0||!a.SequenceEqual(b))return false;
   if(a.Count==1&&CommonOrganizationWords.Contains(a[0]))return false;
   if(aSuffix.Count>0&&bSuffix.Count>0)return aSuffix.SequenceEqual(bSuffix);
   return true;
  }

  private static readonly HashSet<string> LegalSuffixes=new HashSet<string>{"INC","INCORPORATED","CORP","CORPORATION","COMPANY","CO","LTD","LIMITED","LLC","LLP","LP","PLC","GMBH","AG","SA","BV","NV","PTE","PTY","PLLC"};
  private static readonly HashSet<string> CommonOrganizationWords=new HashSet<string>{"GLOBAL","NATIONAL","INTERNATIONAL","UNITED","GENERAL","FIRST","NEW","CENTRAL","PACIFIC","ASIA","AMERICAN","PHILIPPINE","PHILIPPINES","DIGITAL","SYSTEM","SYSTEMS","SERVICE","SERVICES","SOLUTION","SOLUTIONS","TECHNOLOGY","TECHNOLOGIES","GROUP","HOLDINGS","BANK","FINANCE","FINANCIAL","TREASURY","OPERATIONS","MARKETING","SALES","LEGAL","ENGINEERING","SUPPORT","CUSTOMER","ADMIN","ADMINISTRATION","DEPARTMENT","DIVISION","OFFICE","BUREAU","MINISTRY","AGENCY","AUTHORITY","COMMISSION","COUNCIL","BOARD","COMMITTEE","TEAM","UNIT","PROJECT","BUSINESS","INDUSTRIES","ENTERPRISES","PARTNERS","ASSOCIATES","CLIENT","VENDOR","SUPPLIER","PROVIDER","COMPANY","CORPORATION"};
  private static readonly HashSet<string> OrganizationStopWords=new HashSet<string>{"THE","OF","AND"};

  private static (List<string> Core,List<string> Suffix) SplitLegalSuffix(string value)
  {
   var words=Regex.Matches(value.ToUpperInvariant(),@"[A-Z0-9]+(?:(?:'|-)[A-Z0-9]+)?").Cast<Match>().Select(match=>match.Value).Where(word=>!OrganizationStopWords.Contains(word)).ToList();
   var suffix=new List<string>();
   while(words.Count>1&&LegalSuffixes.Contains(words[^1])){suffix.Insert(0,LegalFamily(words[^1]));words.RemoveAt(words.Count-1);}
   return (words,suffix);
  }

  private static string LegalFamily(string word)=>word switch{"INCORPORATED"=>"INC","CORPORATION"=>"CORP","LIMITED"=>"LTD",_=>word};

  // A one-word short form ("Acme") of a longer name ("Acme Treasury Services")
  // is linked only with independent support: a detector or a different reviewer
  // reports the short form while another source reports the long form, the short
  // form is not a common word, and the short form does not fit two different
  // longer names. Without that evidence the two stay separate entities.
  private sealed class ShortFormSupport
  {
   private readonly Dictionary<string,HashSet<string>> origins=new Dictionary<string,HashSet<string>>();
   private readonly List<string> values=new List<string>();

   public ShortFormSupport(List<List<Candidate>> reviewers,List<Candidate> detectors)
   {
    for(var index=0;index<reviewers.Count;index++)foreach(var finding in reviewers[index])Add(finding,"R"+index);
    foreach(var finding in detectors)Add(finding,"D");
   }

   private static string Key(string category,string value)=>category.ToUpperInvariant()+"|"+Project.Normalize(value);

   private void Add(Candidate finding,string origin)
   {
    var key=Key(finding.Category,finding.Value);
    if(!origins.TryGetValue(key,out var set)){set=new HashSet<string>();origins[key]=set;values.Add(finding.Category.ToUpperInvariant()+"|"+finding.Value);}
    set.Add(origin);
   }

   public bool AreSupportedShortForms(string category,string left,string right)
   {
    category=category.ToUpperInvariant();
    if(category is not ("ORGANIZATION" or "VENDOR" or "DEPARTMENT"))return false;
    var (a,_)=SplitLegalSuffix(left);var (b,_)=SplitLegalSuffix(right);
    List<string> shortWords,longWords;string shortValue,longValue;
    if(a.Count==1&&b.Count>=2){shortWords=a;longWords=b;shortValue=left;longValue=right;}
    else if(b.Count==1&&a.Count>=2){shortWords=b;longWords=a;shortValue=right;longValue=left;}
    else return false;
    var word=shortWords[0];
    if(longWords[0]!=word||word.Length<3||CommonOrganizationWords.Contains(word)||!word.Any(char.IsLetter))return false;
    if(DocumentPrivacyPolicy.IsGeneric(shortValue,category)||LocalAiReview.IsClearlyGenericCandidate(shortValue,category))return false;
    if(!origins.TryGetValue(Key(category,shortValue),out var shortOrigins)||!origins.TryGetValue(Key(category,longValue),out var longOrigins))return false;
    if(!shortOrigins.Any(x=>longOrigins.Any(y=>x!=y)))return false;
    // Ambiguous when two genuinely different longer names start with the same word.
    var longerCores=values.Where(item=>item.StartsWith(category+"|",StringComparison.Ordinal)).Select(item=>SplitLegalSuffix(item.Substring(category.Length+1)).Core).Where(core=>core.Count>=2&&core[0]==word).Select(core=>string.Join(" ",core)).Distinct().Count();
    return longerCores<=1;
   }
  }

  private static List<string> OrganizationWords(string value)
  {
   var ignored=new HashSet<string>(new[]{"THE","OF","AND","INC","CORP","CORPORATION","COMPANY","CO","LTD"});
   return Regex.Matches(value.ToUpperInvariant(),@"[A-Z0-9]+(?:(?:'|-)[A-Z0-9]+)?").Cast<Match>().Select(match=>match.Value).Where(word=>!ignored.Contains(word)).ToList();
  }

  private static List<Candidate> ExpandDocumentVariants(string document,List<Candidate> resolved)
  {
   var expanded=new List<Candidate>(resolved);
   var entities=resolved.Where(candidate=>candidate.Category is "PERSON" or "ORGANIZATION" or "VENDOR" or "DEPARTMENT").ToList();
   List<Candidate> Grouped()=>expanded.GroupBy(candidate=>candidate.Category+"|"+Project.Normalize(candidate.Value)).Select(group=>group.OrderByDescending(candidate=>candidate.RequiresContextReview).ThenByDescending(candidate=>candidate.ConsensusVotes).ThenByDescending(candidate=>candidate.Confidence).First()).ToList();
   if(entities.Count==0)return Grouped();
   // The document scan does not depend on the entity, so it runs once. Matches
   // keep document order and are visited in the same order for every entity.
   var scanned=new List<(string Value,string Normalized)>();
   var scannedValues=new HashSet<string>(StringComparer.Ordinal);
   foreach(Match match in Regex.Matches(document,@"\b(?:[\p{Lu}][\p{L}'-]{1,}|[\p{Lu}]\.)(?:\s+(?:de|del|dela|la|van|von|[\p{Lu}][\p{L}'-]{1,}|[\p{Lu}]\.)){0,5}\b",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))
   {
    var value=match.Value.Trim();
    if(value.Length>=3&&scannedValues.Add(value))scanned.Add((value,Project.Normalize(value)));
   }
   var known=new HashSet<string>(expanded.Select(candidate=>Project.Normalize(candidate.Value)),StringComparer.Ordinal);
   foreach(var entity in entities)
   {
    foreach(var (value,normalized) in scanned)
    {
     if(entity.Category=="PERSON"&&Regex.IsMatch(value,@"^(?:Contact|Prepared|Approved|Reviewed|Signed|Submitted|Requested|Applicant|Passenger|Employee|Manager|Director|Secretary|President)\b",RegexOptions.IgnoreCase))continue;
     if(known.Contains(normalized))continue;
     if(!AreAliases(entity.Category,entity.Value,value))continue;
     known.Add(normalized);
     expanded.Add(new Candidate{Value=value,Category=entity.Category,Confidence=entity.Confidence,Source="AI entity graph",DetectionSources=new List<string>(entity.DetectionSources),Explanation="Document-wide alias of a consensus entity.",EntityKey=entity.EntityKey,ConsensusVotes=entity.ConsensusVotes,RecommendedReplace=entity.RecommendedReplace,RequiresContextReview=entity.RequiresContextReview});
    }
   }
   return Grouped();
  }
 }
}

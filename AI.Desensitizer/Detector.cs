using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer
{
 public static class Detector
 {
  private static readonly Regex Email=new Regex(@"\b[A-Z0-9._%+-]{1,64}@[A-Z0-9.-]{1,255}\.[A-Z]{2,24}\b",RegexOptions.IgnoreCase|RegexOptions.Compiled,TimeSpan.FromSeconds(2));
  // Generic phone shape. Any run of 7 to 15 digits with common separators.
  private static readonly Regex GenericPhone=new Regex(@"(?<!\w)(?:(?:\(\+?\d{1,3}\)|\+?\d{1,3})[ .-]?)?(?:\(?\d{2,4}\)?[ .-]?)?\d{3,4}[ .-]?\d{4}(?!\w)",RegexOptions.Compiled);
  // Philippine forms. Each requires a + or ( or a leading 0, so a bare run of digits never qualifies here.
  // They are longer than the generic shape where the area code is a single digit (+63 2 8555 0186).
  private static readonly Regex[] PhilippinePhones={
   new Regex(@"(?<![\w+])(?:\+63|\(\+63\))[ .-]?\(?\d{1,4}\)?[ .-]?\d{3,4}[ .-]?\d{4}(?!\w)",RegexOptions.Compiled),
   new Regex(@"(?<![\w+(])0\d{1,4}[ .-]?\d{3,4}[ .-]?\d{4}(?!\w)",RegexOptions.Compiled),
   new Regex(@"(?<![\w)])\(0?\d{1,4}\)[ .-]?\d{3,4}[ .-]?\d{4}(?!\w)",RegexOptions.Compiled)};
  // Employee, staff and generic identifier labels. Only the value after the label is emitted. The value
  // needs at least one digit and may contain single hyphens (EMP-74215). The bare ID, SSN and Passport
  // labels need 5 or more characters, which is the old behavior. The trailing guard after the label keeps
  // "identified", "identify" and "ID management" from ever matching.
  private static readonly Regex LabelledId=new Regex(
   @"(?<![A-Za-z0-9])(?<label>Employee\s*(?:ID|No\.?|Number|#)|Emp\.?\s*(?:ID|No\.?|Number|#)|Staff\s*(?:ID|No\.?|Number|#)|Badge\s*(?:ID|No\.?|Number|#)|SSN|Passport(?:\s*(?:No\.?|Number|ID))?|ID)(?![A-Za-z])\s*[:#=-]?\s*(?<value>(?=[A-Za-z0-9-]*\d)[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*)(?![A-Za-z0-9-])",
   RegexOptions.IgnoreCase|RegexOptions.Compiled);
  // Requirement and document ids are business structure, not identity (FR-4002, NFR-12, BR-7).
  private static readonly Regex RequirementIdShape=new Regex(@"^(?:FR|NFR|BR|SR|REQ|UC|US|TC|BRD|FS|FDS|TS|R)-\d+$",RegexOptions.IgnoreCase|RegexOptions.Compiled);
  private static readonly Regex NotAPersonalIdLabel=new Regex(@"\b(?:requirement|req|rule|test|case|step|section|screen|story|feature)\s*$",RegexOptions.IgnoreCase|RegexOptions.Compiled);
  // Format-anchored ids with a distinctive prefix. EMP- followed by 4 to 8 digits is an employee number.
  private static readonly Regex EmployeeNumber=new Regex(@"(?<![A-Za-z0-9-])EMP-\d{4,8}(?![A-Za-z0-9-])",RegexOptions.Compiled);
  // Philippine government ids, most specific first. A near miss (wrong group lengths) never matches.
  private static readonly Regex[] PhilippineIds={
   new Regex(@"(?<![\w-])\d{2}-\d{9}-\d(?![\w-])",RegexOptions.Compiled),            // PhilHealth
   new Regex(@"(?<![\w-])\d{2}-\d{7}-\d(?![\w-])",RegexOptions.Compiled),            // SSS
   new Regex(@"(?<![\w-])\d{4}-\d{7}-\d(?![\w-])",RegexOptions.Compiled),            // UMID
   new Regex(@"(?<![\w-])\d{4}-\d{4}-\d{4}(?![\w-])",RegexOptions.Compiled),         // Pag-IBIG
   new Regex(@"(?<![\w-])\d{3}-\d{3}-\d{3}(?:-\d{3,5})?(?![\w-])",RegexOptions.Compiled), // TIN
   new Regex(@"(?<![A-Za-z0-9])P\d{7}[A-Z](?![A-Za-z0-9])",RegexOptions.Compiled)};  // Passport
  // Case sensitive name and street groups: a lowercase word is never a name or a street name.
  private const string NameParticle=@"(?:de[ \t]+la|del|dela|delos|de|la|las|los|van[ \t]+der|van|von|der|di|da)";
  // A lowercase particle may sit between capitalized words (34 Dama de Noche Road), never start or end the name.
  private static readonly Regex Address=new Regex(@"\b\d{1,5}[ \t]+(?-i:\p{Lu}[\p{L}.'-]+(?:[ \t]+(?:"+NameParticle+@"[ \t]+)*\p{Lu}[\p{L}.'-]+){0,3})[ \t]+(?:Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Lane|Ln|Drive|Dr|Way|Court|Ct)\b",RegexOptions.IgnoreCase|RegexOptions.Compiled);
  private static readonly Regex AllCapsName=new Regex(@"^(?:[A-Z][A-Z.'-]*\s+){1,4}[A-Z][A-Z.'-]*$",RegexOptions.Compiled);
  private static readonly Regex LabelledName=new Regex(@"\b(?:Name|Author|Prepared\s+by|Contact|Employee)\s*:\s*(?<name>(?-i:\p{Lu}[\p{L}.'-]+(?:[ \t]+(?:"+NameParticle+@"[ \t]+)*(?:\p{Lu}\.|\p{Lu}[\p{L}.'-]+)){1,4}))",RegexOptions.IgnoreCase|RegexOptions.Compiled);
  private static readonly Regex ReferenceName=new Regex(@"^(?:Comm\.\s+)?(?<name>[A-Z][A-Za-z.'-]+(?:\s+(?:[A-Z]\.|[A-Z][A-Za-z.'-]+)){1,4})\s+-",RegexOptions.Compiled);
  private static readonly Regex IdentifierLabel=new Regex(@"\b(?:account|acct|a/c|reference|ref|tin|tax\s+id(?:entification)?|sss|philhealth|pag-?ibig|umid|passport|policy|card|customer|member|membership|licen[cs]e|invoice|order|ticket|transaction|claim)\b(?:\s*(?:no\.?|number|num|#|id|code))?",RegexOptions.IgnoreCase|RegexOptions.Compiled);
  private static readonly Regex ContactLabel=new Regex(@"\b(?:phone|tel|telephone|mobile|cell|cellphone|fax|contact|call|hotline|whatsapp|viber)\b",RegexOptions.IgnoreCase|RegexOptions.Compiled);
  private static readonly Regex ThresholdWord=new Regex(@"\b(?:exceeds?|exceeding|at\s+least|limit(?:ed)?|threshold|amount|minimum|maximum|more\s+than|less\s+than|greater\s+than|up\s+to)\b(?<rest>[^.;]*)$",RegexOptions.IgnoreCase|RegexOptions.Compiled);

  public static IEnumerable<Candidate> Find(string text)
  {
   var candidates=new List<Candidate>();
   candidates.AddRange(Email.Matches(text).Cast<Match>().Select(m=>new Candidate{Value=m.Value.Trim(),Category="EMAIL"}));
   var idSpans=new List<(int Start,int End,string Value)>();
   foreach(Match m in LabelledId.Matches(text))
   {
    var value=m.Groups["value"];
    var label=m.Groups["label"].Value;
    var weakLabel=Regex.IsMatch(label,@"^(?:ID|SSN|Passport.*)$",RegexOptions.IgnoreCase);
    if(weakLabel&&value.Length<5)continue;
    if(!weakLabel&&value.Length<3)continue;
    if(RequirementIdShape.IsMatch(value.Value))continue;
    if(label.Equals("ID",StringComparison.OrdinalIgnoreCase)&&NotAPersonalIdLabel.IsMatch(text.Substring(Math.Max(0,m.Index-24),m.Index-Math.Max(0,m.Index-24))))continue;
    idSpans.Add((value.Index,value.Index+value.Length,value.Value));
   }
   foreach(Match m in EmployeeNumber.Matches(text))idSpans.Add((m.Index,m.Index+m.Length,m.Value));
   foreach(var rule in PhilippineIds)
    foreach(Match m in rule.Matches(text))
     if(!idSpans.Any(s=>m.Index<s.End&&s.Start<m.Index+m.Length))idSpans.Add((m.Index,m.Index+m.Length,m.Value));
   candidates.AddRange(idSpans.OrderBy(s=>s.Start).Select(s=>new Candidate{Value=s.Value.Trim(),Category="IDENTIFIER"}));
   candidates.AddRange(PhoneCandidates(text,idSpans));
   candidates.AddRange(Address.Matches(text).Cast<Match>().Select(m=>new Candidate{Value=m.Value.Trim(),Category="ADDRESS"}));
   var lines=text.Replace("\r\n","\n").Replace('\r','\n').Split('\n').Select(x=>Regex.Replace(x,@"\s+"," ").Trim()).Where(x=>x.Length>0).ToList();
   var firstContent=lines.FirstOrDefault();
   if(firstContent!=null&&firstContent.Length<=80&&AllCapsName.IsMatch(firstContent)&&!LooksLikeHeading(firstContent))candidates.Add(new Candidate{Value=ToDisplayCase(firstContent),Category="PERSON"});
   candidates.AddRange(LabelledName.Matches(text).Cast<Match>().Select(m=>new Candidate{Value=m.Groups["name"].Value.Trim(),Category="PERSON"}));
   var referencesIndex=lines.FindIndex(x=>string.Equals(x,"REFERENCES",StringComparison.OrdinalIgnoreCase));
   if(referencesIndex>=0)foreach(var line in lines.Skip(referencesIndex+1)){var match=ReferenceName.Match(line);if(match.Success)candidates.Add(new Candidate{Value=match.Groups["name"].Value.Trim(),Category="PERSON"});}
   return candidates.Where(x=>x.Value.Length>1).GroupBy(x=>Project.Normalize(x.Value)).Select(x=>x.First());
  }

  // The longest span wins: the generic shape finds "2 8555 0186" inside "+63 2 8555 0186", and only the
  // whole number is a finding. Numbers that follow a threshold word are quantities, never phones. A long
  // digit run that follows an account, reference or tax-id label is an identifier, not a phone.
  private static IEnumerable<Candidate> PhoneCandidates(string text,List<(int Start,int End,string Value)> idSpans)
  {
   var spans=new List<(int Start,int End)>();
   foreach(Match m in GenericPhone.Matches(text))spans.Add((m.Index,m.Index+m.Length));
   foreach(var rule in PhilippinePhones)foreach(Match m in rule.Matches(text))spans.Add((m.Index,m.Index+m.Length));
   var kept=spans.Distinct().Where(a=>!spans.Any(b=>(b.Start<=a.Start&&b.End>=a.End)&&(b.End-b.Start)>(a.End-a.Start)))
    .Where(a=>!idSpans.Any(s=>s.Start<=a.Start&&s.End>=a.End)).OrderBy(a=>a.Start).ToList();
   foreach(var span in kept)
   {
    var value=text.Substring(span.Start,span.End-span.Start).Trim();
    var before=LineBefore(text,span.Start,40);
    if(IsThresholdNumber(before,value))continue;
    var category="PHONE";
    if(Regex.IsMatch(value,@"^\d[\d -]*$")&&value.Count(char.IsDigit)>=8)
    {
     var id=IdentifierLabel.Matches(before).Cast<Match>().LastOrDefault();
     var contact=ContactLabel.Matches(before).Cast<Match>().LastOrDefault();
     if(id!=null&&(contact==null||contact.Index<id.Index))category="IDENTIFIER";
    }
    yield return new Candidate{Value=value,Category=category};
   }
  }

  private static string LineBefore(string text,int index,int length)
  {
   var start=Math.Max(0,index-length);
   var before=text.Substring(start,index-start);
   var lineBreak=before.LastIndexOfAny(new[]{'\n','\r'});
   return lineBreak>=0?before.Substring(lineBreak+1):before;
  }

  private static bool IsThresholdNumber(string before,string value)
  {
   // Plain digits that do not start with 0 (phone numbers commonly do). A contact label between the
   // threshold word and the number means the number is a phone after all.
   if(!Regex.IsMatch(value,@"^[1-9]\d*$"))return false;
   var match=ThresholdWord.Match(before);
   return match.Success&&!ContactLabel.IsMatch(match.Groups["rest"].Value);
  }

  private static bool LooksLikeHeading(string value){var headings=new[]{"SUMMARY","EXPERIENCE","EDUCATION","SKILLS","REFERENCES","WORK EXPERIENCE","PROFESSIONAL EXPERIENCE"};return headings.Contains(value,StringComparer.OrdinalIgnoreCase);}
  private static string ToDisplayCase(string value){return string.Join(" ",value.Split(' ').Select(part=>part.Length<=2||part.EndsWith(".",StringComparison.Ordinal)?part:char.ToUpperInvariant(part[0])+part.Substring(1).ToLowerInvariant()));}
 }
}

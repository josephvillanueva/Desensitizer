using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Desensitizer
{
 // A separate, bundled generative review. The server listens only on loopback,
 // requires a per-run key, and is stopped before this call returns.
 public static class LocalAiReview
 {
  private static readonly HashSet<string> Categories=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {"PERSON","ORGANIZATION","VENDOR","PROJECT","SYSTEM","PRODUCT","DEPARTMENT","ADDRESS","URL","EMAIL","PHONE","IDENTIFIER"};
  private static readonly HashSet<string> PublicTechnologies=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {"Terraform","React.js","Next.js","Node.js","Angular.js","Express.js","GraphQL","Tailwind","Tailwind CSS","Fastify","Lit","Git","GitHub","SCSS","CSS","HTML","JavaScript","Python","SQL","C#","Microsoft Excel","Microsoft Word","Power BI","Framer Motion","REST API","NEAR Protocol","Apollo","OCR","NFT technology","Decentralized finance","Traditional gaming markets","Blockchain","IT Company"};
  private const string SystemPrompt="You review business documents locally for identifying details before they are shared for external analysis. Identify exact text spans naming people, employers, organizations, clients, vendors, internal projects, internal systems, proprietary products, addresses, contact information, or personal URLs. Look for spelling and punctuation variants of the same name. Exclude public technologies and frameworks such as React.js, Terraform, GraphQL, and Tailwind; also exclude job titles, dates, degrees, courses, industries, generic business phrases, and sentence fragments. A URL must look like a real web address, an email must contain @, and a phone must contain digits. Never invent or paraphrase a value. Return only JSON in this shape: {\"entities\":[{\"value\":\"exact substring from input\",\"category\":\"PERSON|ORGANIZATION|VENDOR|PROJECT|SYSTEM|PRODUCT|DEPARTMENT|ADDRESS|URL|EMAIL|PHONE|IDENTIFIER\"}]}.";


  // Compatibility entry point. A section that could not be reviewed makes the
  // whole check incomplete, so this throws IncompleteReviewException (its Result
  // keeps the completed work). Use FindDetailed to keep partial results.
  public static List<Candidate> Find(string text,Action<AnalysisProgress>? progress=null,CancellationToken cancellationToken=default,Action<IReadOnlyList<Candidate>>? findings=null,ExportReviewCache? cache=null,IReadOnlyList<Candidate>? detectorFindings=null,TableReviewContext? tableContext=null)
  {
   var result=FindDetailed(text,progress,cancellationToken,findings,cache,detectorFindings,tableContext);
   if(!result.Complete)throw new IncompleteReviewException(result);
   return result.Candidates;
  }

  // One failing section is recorded and skipped, not fatal. The result lists the
  // failed sections and is incomplete, which must never release a file. Finished
  // sections can be resumed through a cache that was exported and imported.
  public static LocalReviewResult FindDetailed(string text,Action<AnalysisProgress>? progress=null,CancellationToken cancellationToken=default,Action<IReadOnlyList<Candidate>>? findings=null,ExportReviewCache? cache=null,IReadOnlyList<Candidate>? detectorFindings=null,TableReviewContext? tableContext=null)
  {
   var basePath=AppContext.BaseDirectory;
   var runtime=RuntimeOverrides.Get("DESENSITIZER_LLM_RUNTIME")??Path.Combine(basePath,"model-packs","context-llm","llama-server.exe");
   var model=RuntimeOverrides.Get("DESENSITIZER_LLM_MODEL")??Path.Combine(basePath,"model-packs","context-llm","Qwen3-4B-Q4_K_M.gguf");
   if(!File.Exists(runtime)||!File.Exists(model))throw new FileNotFoundException("The bundled local AI review is missing. Reinstall the complete portable folder; no online service was used.");
   var outcome=new LocalReviewResult();
   if(string.IsNullOrWhiteSpace(text))return outcome;
   // A caller-supplied cache survives export passes. Otherwise reuse identical
   // requests only within this run, bounded in memory and disposed on every exit.
   using var ownedCache=cache==null?new ExportReviewCache():null;
   var activeCache=cache??ownedCache!;
   // Keep each response small enough that a dense resume cannot truncate its JSON.
   var sections=DeepReview.Sections(text,1000,150);
   var documentAnchors=DocumentPrivacyPolicy.Apply(text,detectorFindings??Array.Empty<Candidate>());
   // The server belongs to the export operation when a cache is supplied, so it
   // stays alive across export passes and stops when the cache is disposed.
   // Without a cache it is stopped before this call returns.
   var pool=activeCache.GetOrAttach("llama-server-pool",()=>new LlamaServerPool());
   try
   {
    var host=pool.Acquire("Qwen",runtime,model);
    using var killOnCancel=host.KillOnCancel(cancellationToken);
    List<Candidate> Review(string section,string prompt)
    {
     try{return ReviewSection(host.EnsureReady(cancellationToken),section,cancellationToken,prompt);}
     catch(HttpRequestException) when(!cancellationToken.IsCancellationRequested)
     {
      // The server may have exited. EnsureReady restarts it; try once more.
      return ReviewSection(host.EnsureReady(cancellationToken),section,cancellationToken,prompt);
     }
    }
    var found=new List<Candidate>();var tableSearchStart=0;
    for(var i=0;i<sections.Count;i++)
    {
     cancellationToken.ThrowIfCancellationRequested();
     progress?.Invoke(new AnalysisProgress{Step=i+1,Total=sections.Count,Name="Local AI review",Detail="Understanding section "+(i+1)+" of "+sections.Count+" in context. Nothing is uploaded."});
     // The cursor must advance for every section, reviewed or not.
     var tablePart=tableContext?.ForSection(sections[i],ref tableSearchStart)??"";
     if(!ReviewTriage.NeedsModelReview(sections[i])){outcome.SectionsSkippedByTriage++;continue;}
     try
     {
      var prompt=SystemPrompt+DocumentPrivacyPolicy.ReviewContract+DocumentPrivacyPolicy.Context(text,documentAnchors,sections[i])+tablePart;
      var cacheConfiguration=ExportReviewCache.Configuration(runtime,model,prompt+LocalReviewJson.StrictPrompt+JsonSerializer.Serialize(LocalReviewJson.ResponseSchema(Categories)));
      var sectionFindings=activeCache.Review(cacheConfiguration,sections[i],()=>Review(sections[i],prompt),cancellationToken);
      found.AddRange(sectionFindings);
      if(sectionFindings.Count>0)findings?.Invoke(sectionFindings);
     }
     catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
     catch(Exception error) when(error is not OutOfMemoryException)
     {
      // Isolate the failure. The section is reported, never treated as empty.
      outcome.FailedSections.Add(new SectionFailure{Reviewer="Qwen",SectionIndex=i+1,SectionCount=sections.Count,Reason=LocalReviewResult.SafeReason(error)});
     }
    }
    var expanded=new List<Candidate>(found);
    foreach(var candidate in found.Where(c=>c.Category=="PERSON"||c.Category=="ORGANIZATION"||c.Category=="VENDOR"||c.Category=="PROJECT"||c.Category=="SYSTEM"||c.Category=="PRODUCT"||c.Category=="DEPARTMENT"))
    {
     var words=Regex.Split(candidate.Value,@"[^\p{L}\p{N}]+").Where(word=>word.Length>0).ToArray();
     if(words.Length<2||words.Length>6)continue;
     var pattern=string.Join(@"[\s._-]{0,3}",words.Select(Regex.Escape));
     foreach(Match match in Regex.Matches(text,pattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(500)))
     {
      var variant=match.Value.Trim();
      if(variant.Length<3||variant.Equals(candidate.Value,StringComparison.OrdinalIgnoreCase))continue;
      expanded.Add(new Candidate{Value=variant,Category=candidate.Category,Confidence=0,Source="Local AI",Explanation="Spelling or punctuation variant of a local AI finding. Review before replacing."});
     }
    }
    // Keep one finding per category and value so a category disagreement stays visible.
    var completed=MergeReviewerFindings(expanded);
    var variants=completed.Where(candidate=>!found.Any(existing=>Project.Normalize(existing.Value)==Project.Normalize(candidate.Value))).ToList();
    if(variants.Count>0)findings?.Invoke(variants);
    outcome.Candidates=DocumentPrivacyPolicy.Apply(text,completed);
    return outcome;
   }
   finally
   {
    if(cancellationToken.IsCancellationRequested)pool.StopAll();
   }
  }

  // The same value with a different category is a disagreement, not a duplicate.
  public static List<Candidate> MergeReviewerFindings(IEnumerable<Candidate> findings)
  {
   return findings.GroupBy(candidate=>candidate.Category.ToUpperInvariant()+"|"+Project.Normalize(candidate.Value)).Select(group=>group.First()).ToList();
  }

  private static List<Candidate> ReviewSection(HttpClient client,string section,CancellationToken token,string prompt,int retryDepth=0)
  {
   try
   {
    try{return ReviewSectionOnce(client,section,token,prompt);}
    catch(InvalidDataException){return ReviewSectionOnce(client,section,token,prompt+LocalReviewJson.StrictPrompt);}
   }
   catch(InvalidDataException) when(retryDepth<3&&section.Length>450)
   {
    // A dense section can exceed the model's response budget. Retry smaller,
    // overlapping pieces rather than treating a partial JSON answer as valid.
    var middle=section.Length/2;
    var left=section.Substring(0,Math.Min(section.Length,middle+90));
    var right=section.Substring(Math.Max(0,middle-90));
    return MergeReviewerFindings(ReviewSection(client,left,token,prompt,retryDepth+1)
     .Concat(ReviewSection(client,right,token,prompt,retryDepth+1)));
   }
  }

  private static List<Candidate> ReviewSectionOnce(HttpClient client,string section,CancellationToken token,string prompt)
  {
   var readableSection=Regex.Replace(section,@"\[[A-Z_]+_\d{3,}\]"," ");
   var request=new{temperature=0,max_tokens=1600,stream=false,response_format=new{type="json_object",schema=LocalReviewJson.ResponseSchema(Categories)},messages=new[]{new{role="system",content=prompt},new{role="user",content=readableSection}}};
   using var body=new StringContent(JsonSerializer.Serialize(request),Encoding.UTF8,"application/json");
   using var response=client.PostAsync("/v1/chat/completions",body,token).GetAwaiter().GetResult();
   response.EnsureSuccessStatusCode();
   var json=response.Content.ReadAsStringAsync(token).GetAwaiter().GetResult();
   var output=LocalReviewEnvelope.ReadChat(json);
   var candidates=new List<Candidate>();
   foreach(var entity in LocalReviewJson.Read(output,Categories,readableSection))
   {
    var category=entity.Category;
    var occurrence=Regex.Match(readableSection,DocumentPrivacyPolicy.Pattern(entity.Value,true),RegexOptions.IgnoreCase);
    var value=occurrence.Success?occurrence.Value:LocalReviewJson.RecoverKnownSourceAcronym(readableSection,entity.Value,category);
    if(value==null)throw LocalReviewJson.Ungrounded(entity.Value,readableSection);
    if(DocumentPrivacyPolicy.IsPublicWorkflowLabel(readableSection,value))continue;
    if(DocumentPrivacyPolicy.IsPublicQuantity(readableSection,value))continue;
    if(value.Length>512)throw new InvalidDataException("The local AI returned an oversized entity span. The scan is incomplete.");
    if(value.Length<2&&category=="PERSON")continue;
    if(Regex.IsMatch(value,@"^\[?[A-Z_]+_\d{3,}\]?$"))continue;
    if(ContextKeepPolicy.CanKeep(value,category,readableSection)||IsClearlyGenericCandidate(value,category))continue;
    candidates.Add(new Candidate{Value=value,Category=category,Confidence=0,Source="Local AI",Explanation="Context-aware local language-model suggestion. Review before replacing."});
   }
   return candidates;
  }

  public static bool IsClearlyGenericCandidate(string value,string category,bool labelAnchoredUsername=false)
  {
   value=value.Trim();category=category.ToUpperInvariant();
   if(DocumentPrivacyPolicy.IsGeneric(value,category)&&!Regex.IsMatch(value,@"^\d{1,3}(?:\.\d{1,3}){3}$"))return true;
   if(PublicTechnologies.Contains(value)||PublicTechnologyPolicy.IsPublicTechnology(value))return true;
   // Built-in headings plus the labels the user chose to keep as-is.
   if(GenericBusinessLabels.IsGeneric(value,category))return true;
   if(category=="PROJECT"&&value.Equals("Background",StringComparison.OrdinalIgnoreCase))return true;
   if(Regex.IsMatch(value,@"\b(of|the|and|for)\s*$",RegexOptions.IgnoreCase))return true;
   if(category=="ADDRESS"&&Regex.IsMatch(value,@"^(?:city|province|state|country|street|address|municipality|postal code|zip code)$",RegexOptions.IgnoreCase))return true;
   if(category=="PRODUCT"&&Regex.IsMatch(value,@"^(?:leased|rented|owned|property|premises|unit|water heater|\d+\s*(?:-\s*)?(?:bedroom\s+unit|water\s+heater))$",RegexOptions.IgnoreCase))return true;
   // The language model can mislabel an ordinary sentence fragment as a URL,
   // email, or phone. Validate those categories before they can block export.
   if(category=="URL"&&(value.Any(char.IsWhiteSpace)||!Regex.IsMatch(value,@"(https?://|\.[a-z]{2,}|^[a-z0-9_-]+/[a-z0-9_-]+)",RegexOptions.IgnoreCase)))return true;
   if(category=="EMAIL"&&!Regex.IsMatch(value,@"^[^\s@]+@[^\s@]+\.[^\s@]+$"))return true;
   if(category=="PHONE"&&value.Count(char.IsDigit)<7)return true;
   if(category=="IDENTIFIER")
   {
   if(Regex.IsMatch(value,@"^_\d{3,}$"))return true;
    // Versions and outline numbers describe the document structure. They are
    // not account, employee, transaction, or reference identifiers.
    if(Regex.IsMatch(value,@"^v?\d+(?:\.\d+){1,7}\.?$",RegexOptions.IgnoreCase))return true;
    // A model-sourced identifier needs a digit to be credible. A username that follows an explicit
    // Username/Login label in the source text is evidence by itself (for example "ldmercado").
    if(!labelAnchoredUsername&&!value.Any(char.IsDigit))return true;
    // Written-out quantities in contract clauses are not account or identity
    // numbers merely because they include a parenthesized digit.
    if(Regex.IsMatch(value,@"\b(?:one|two|three|four|five|six|seven|eight|nine|ten)\s*\(\d{1,2}\)\s*(?:month|mon|day|week|year)s?\b",RegexOptions.IgnoreCase))return true;
    if(Regex.IsMatch(value,@"\b(?:rental|rent|notice|payment|deposit|period)\b",RegexOptions.IgnoreCase)&&!Regex.IsMatch(value,@"\b(?:account|identification|passport|license|reference|ID)\s*(?:no\.?|number|#|:)\b",RegexOptions.IgnoreCase))return true;
   }
   if(category=="DEPARTMENT"&&!Regex.IsMatch(value,@"\b(department|dept|division|office|bureau|agency|unit|team)\b",RegexOptions.IgnoreCase))return true;
   // Letter case alone is not evidence that a candidate is public. Known
   // business rules and labels are preserved by the shared content policy.
   if(category=="DEPARTMENT"&&Regex.IsMatch(value,@"^(Bachelor|Diploma|Master|Doctor|Certificate|Course)\b",RegexOptions.IgnoreCase))return true;
   if(category=="PRODUCT"&&Regex.IsMatch(value,@"^(decentralized finance|NFT technology|traditional gaming markets)$",RegexOptions.IgnoreCase))return true;
   return false;
  }

  public static bool IsHighRiskResidualCandidate(Candidate candidate)
  {
   var value=candidate.Value.Trim();
   var category=candidate.Category.ToUpperInvariant();
   if(IsClearlyGenericCandidate(value,category)||Regex.IsMatch(value,@"\[[A-Z_]+_\d{3,}\]",RegexOptions.IgnoreCase))return false;
   if(candidate.RequiresContextReview)return true;
   if(category is "EMAIL" or "PHONE" or "URL" or "ADDRESS" or "IDENTIFIER")return true;
   if(category=="PERSON")
   {
    if(Regex.IsMatch(value,@"\b(?:agent|analyst|applicant|approver|clerk|customer|head|manager|passenger|requester|reviewer|staff|user)\b",RegexOptions.IgnoreCase))return false;
    return Regex.IsMatch(value,@"^[\p{L}][\p{L}'-]{1,}(?:\s+(?:[\p{L}]\.|de|del|dela|de\s+la|van|von|[\p{L}][\p{L}'-]{1,})){0,5}$");
   }
   if(category is "ORGANIZATION" or "VENDOR" or "DEPARTMENT")
   {
    if(Regex.IsMatch(value,@"^[A-Z][A-Z0-9&.-]{1,11}$"))return true;
    return Regex.IsMatch(value,@"\b(?:Agency|Authority|Bank|Bureau|Commission|Company|Corporation|Department|Foundation|Group|Holdings|Inc\.?|Institute|Ministry|Office|Organization|University)\b",RegexOptions.IgnoreCase);
   }
   // The initial deep review still presents project, system, and product
   // suggestions. At the release gate, an uncorroborated local-AI label in
   // these broad categories is advisory rather than a reason for an endless
   // export loop.
   return false;
  }

 }
}

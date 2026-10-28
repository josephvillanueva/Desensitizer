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
  // These exact lowercase words are ordinary prose in the BRD, not names or
  // identifiers. Keep capitalized names and longer values eligible for review.
  private static readonly HashSet<string> GenericLowercaseWords=new HashSet<string>(StringComparer.Ordinal)
  {"end","vicinity"};
  private const string BasePrompt="Review the supplied business document locally before it is shared externally. Return exact source substrings only. Exclude public technologies, job titles, dates, outline numbers, software versions, generic business terms, and sentence fragments. Never invent or paraphrase. Return only JSON in this shape: {\"entities\":[{\"value\":\"exact substring from input\",\"category\":\"PERSON|ORGANIZATION|VENDOR|PROJECT|SYSTEM|PRODUCT|DEPARTMENT|ADDRESS|URL|EMAIL|PHONE|IDENTIFIER\"}]}. ";

  private sealed class ReviewerDefinition
  {
   public string Name {get;init;}="";
   public string Source {get;init;}="";
   public string ModelPath {get;init;}="";
   public string Prompt {get;init;}="";
  }

  // Compatibility entry point. A section that could not be reviewed makes the
  // whole check incomplete, so this throws IncompleteReviewException (its Result
  // keeps the completed work). Use FindDetailed to keep partial results.
  public static List<Candidate> Find(string text,Action<AnalysisProgress>? progress=null,CancellationToken cancellationToken=default,Action<IReadOnlyList<Candidate>>? findings=null,IReadOnlyList<Candidate>? detectorFindings=null,ExportReviewCache? cache=null,TableReviewContext? tableContext=null)
  {
   var result=FindDetailed(text,progress,cancellationToken,findings,detectorFindings,cache,tableContext);
   if(!result.Complete)throw new IncompleteReviewException(result);
   return result.Candidates;
  }

  // One failing section is recorded and skipped, not fatal. The result lists the
  // failed sections and is incomplete, which must never release a file. Finished
  // sections can be resumed through a cache that was exported and imported.
  public static LocalReviewResult FindDetailed(string text,Action<AnalysisProgress>? progress=null,CancellationToken cancellationToken=default,Action<IReadOnlyList<Candidate>>? findings=null,IReadOnlyList<Candidate>? detectorFindings=null,ExportReviewCache? cache=null,TableReviewContext? tableContext=null)
  {
   var basePath=AppContext.BaseDirectory;
   var runtime=RuntimeOverrides.Get("DESENSITIZER_LLM_RUNTIME")??Path.Combine(basePath,"model-packs","context-llm","llama-server.exe");
   var reviewers=new[]{
    new ReviewerDefinition{Name="Qwen",Source="AI Qwen",ModelPath=RuntimeOverrides.Get("AI_DESENSITIZER_QWEN_MODEL")??Path.Combine(basePath,"model-packs","context-llm","Qwen3-4B-Q4_K_M.gguf"),Prompt=BasePrompt+"Act as the broad document privacy reviewer. Find proper names, named organizations, vendors, internal projects, systems, products, locations, contact details, and unique identifiers. Include meaningful spelling and punctuation variants."},
    new ReviewerDefinition{Name="NuExtract",Source="AI NuExtract",ModelPath=RuntimeOverrides.Get("AI_DESENSITIZER_NUEXTRACT_MODEL")??Path.Combine(basePath,"model-packs","consensus-llm","NuExtract-2.0-2B-Q4_K_M.gguf"),Prompt=BasePrompt+"Act as the exact structured extraction reviewer. Extract only literal spans that fit the requested privacy categories. Focus on full names, aliases, initials, usernames, organizations, addresses, contact details, and unique identifiers. Do not include surrounding prose."},
    new ReviewerDefinition{Name="Gemma",Source="AI Gemma",ModelPath=RuntimeOverrides.Get("AI_DESENSITIZER_GEMMA_MODEL")??Path.Combine(basePath,"model-packs","consensus-llm","gemma-3-4b-it-q4_0.gguf"),Prompt=BasePrompt+"Act as the conservative privacy reviewer. Find real identifying or confidential proper nouns and direct identifiers, but reject generic words, headings, requirements language, outline numbers, and public product or technology names."}
   };
   if(!File.Exists(runtime))throw new FileNotFoundException("The bundled local AI runtime is missing. Reinstall the complete portable folder.");
   var missing=reviewers.Where(reviewer=>!File.Exists(reviewer.ModelPath)).Select(reviewer=>reviewer.Name).ToList();
   if(missing.Count>0)throw new FileNotFoundException("The following local AI reviewer models are missing: "+string.Join(", ",missing)+". Reinstall the complete AI Desensitizer folder.");
   var outcome=new LocalReviewResult();
   if(string.IsNullOrWhiteSpace(text))return outcome;
   // Reuse exact successful requests within a standalone review without adding
   // global state, changing model settings, or retaining plaintext after the run.
   using var ownedCache=cache==null?new ExportReviewCache():null;
   var activeCache=cache??ownedCache!;
   var sections=DeepReview.Sections(text,1000,150);
   var reviewerResults=new IReadOnlyList<Candidate>[reviewers.Length];
   // The pool belongs to the export operation when a cache is supplied, so each
   // server stays alive across its passes and is stopped when the cache is
   // disposed. Without a cache the servers are stopped before this call returns.
   var pool=activeCache.GetOrAttach("llama-server-pool",()=>new LlamaServerPool());
   try
   {
    // A reviewer whose server is already running goes first so it is not reloaded.
    var order=Enumerable.Range(0,reviewers.Length).OrderBy(index=>pool.IsRunning(reviewers[index].Name)?0:1).ThenBy(index=>index).ToList();
    for(var position=0;position<order.Count;position++)
    {
     var reviewerIndex=order[position];
     var reviewer=reviewers[reviewerIndex];
     var documentAnchors=DocumentPrivacyPolicy.Apply(text,detectorFindings??Array.Empty<Candidate>());
     var host=pool.Acquire(reviewer.Name,runtime,reviewer.ModelPath);
     using var killOnCancel=host.KillOnCancel(cancellationToken);
     List<Candidate> Review(string section,string context)
     {
      try{return ReviewSection(host.EnsureReady(cancellationToken),reviewer,section,cancellationToken,context);}
      catch(HttpRequestException) when(!cancellationToken.IsCancellationRequested)
      {
       // The server may have exited. EnsureReady restarts it; try once more.
       return ReviewSection(host.EnsureReady(cancellationToken),reviewer,section,cancellationToken,context);
      }
     }
     var found=new List<Candidate>();var tableSearchStart=0;
     for(var sectionIndex=0;sectionIndex<sections.Count;sectionIndex++)
     {
      cancellationToken.ThrowIfCancellationRequested();
      var completed=position*sections.Count+sectionIndex+1;
      progress?.Invoke(new AnalysisProgress{Step=completed,Total=reviewers.Length*sections.Count,Name="AI reviewer "+(position+1)+" of 3: "+reviewer.Name,Detail="Reviewing section "+(sectionIndex+1)+" of "+sections.Count+" locally. Nothing is uploaded."});
      var section=sections[sectionIndex];
      // The cursor must advance for every section, reviewed or not.
      var tablePart=tableContext?.ForSection(section,ref tableSearchStart)??"";
      if(!ReviewTriage.NeedsModelReview(section)){outcome.SectionsSkippedByTriage++;continue;}
      try
      {
       var context=DocumentPrivacyPolicy.ReviewContract+DocumentPrivacyPolicy.Context(text,documentAnchors,section)+tablePart;
       var cacheConfiguration=ExportReviewCache.Configuration(runtime,reviewer.ModelPath,reviewer.Name+reviewer.Prompt+LocalReviewJson.StrictPrompt+context+LocalReviewJson.NuExtractInput("",Categories)+LocalReviewJson.GemmaInput("","")+JsonSerializer.Serialize(LocalReviewJson.ResponseSchema(Categories))+JsonSerializer.Serialize(LocalReviewJson.NuExtractResponseSchema(Categories)));
       found.AddRange(activeCache.Review(cacheConfiguration,section,()=>Review(section,context),cancellationToken));
      }
      catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
      catch(Exception error) when(error is not OutOfMemoryException)
      {
       // Isolate the failure. The section is reported, never treated as empty.
       outcome.FailedSections.Add(new SectionFailure{Reviewer=reviewer.Name,SectionIndex=sectionIndex+1,SectionCount=sections.Count,Reason=LocalReviewResult.SafeReason(error)});
      }
     }
     reviewerResults[reviewerIndex]=MergeReviewerFindings(found);
    }
   }
   finally
   {
    if(cancellationToken.IsCancellationRequested)pool.StopAll();
   }
   var consensus=DocumentPrivacyPolicy.Apply(text,AiConsensus.Resolve(text,reviewerResults,detectorFindings??Array.Empty<Candidate>()));
   if(consensus.Count>0)findings?.Invoke(consensus);
   outcome.Candidates=consensus;
   return outcome;
  }

  // The same value with a different category is a disagreement, not a duplicate.
  // Keep one finding per (category, value) so consensus and review can see it.
  public static List<Candidate> MergeReviewerFindings(IEnumerable<Candidate> findings)
  {
   return findings.GroupBy(candidate=>candidate.Category.ToUpperInvariant()+"|"+Project.Normalize(candidate.Value)).Select(group=>group.First()).ToList();
  }

  private static List<Candidate> ReviewSection(HttpClient client,ReviewerDefinition reviewer,string section,CancellationToken token,string context,int retryDepth=0)
  {
   try
   {
    try{return ReviewSectionOnce(client,reviewer,section,token,reviewer.Prompt+context);}
    catch(InvalidDataException)
    {
     // Small models can answer in prose or repeat JSON entries until the
     // response limit. Retry with a strict independent request. Never
     // interpret an incomplete answer as an empty finding list.
     // NuExtract has a fixed native template. Repeat that exact request rather
     // than inserting a system prompt or changing sampling/model settings.
     return ReviewSectionOnce(client,reviewer,section,token,reviewer.Name=="NuExtract"?reviewer.Prompt+context:context+LocalReviewJson.StrictPrompt);
    }
   }
   catch(InvalidDataException) when(retryDepth<3&&section.Length>450)
   {
    // A dense section can exceed the model's response budget. Retry smaller,
    // overlapping pieces rather than treating a partial JSON answer as valid.
    var middle=section.Length/2;
    var left=section.Substring(0,Math.Min(section.Length,middle+90));
    var right=section.Substring(Math.Max(0,middle-90));
    return MergeReviewerFindings(ReviewSection(client,reviewer,left,token,context,retryDepth+1)
     .Concat(ReviewSection(client,reviewer,right,token,context,retryDepth+1)));
   }
  }

  private static List<Candidate> ReviewSectionOnce(HttpClient client,ReviewerDefinition reviewer,string section,CancellationToken token,string? promptOverride=null)
  {
   var readableSection=Regex.Replace(section,@"\[[A-Z_]+_\d{3,}\]"," ");
   var messages=reviewer.Name=="NuExtract"
    ?new[]{new{role="user",content=LocalReviewJson.NuExtractInput(readableSection,Categories)}}
    :new[]{new{role="system",content=promptOverride??reviewer.Prompt},new{role="user",content=readableSection}};
   // The chat formatter rejects Gemma's role prefix when grammar is enabled.
   // Native completion uses Gemma's documented turn format and avoids that
   // parser, while still enforcing exactly the required response schema.
   var maxTokens=1600;
   object request=reviewer.Name=="Gemma"
    ?new{temperature=0,n_predict=maxTokens,stream=false,json_schema=LocalReviewJson.ResponseSchema(Categories),prompt=LocalReviewJson.GemmaInput(promptOverride??reviewer.Prompt,readableSection)}
    :new{temperature=0,max_tokens=maxTokens,stream=false,response_format=new{type="json_object",schema=reviewer.Name=="NuExtract"?LocalReviewJson.NuExtractResponseSchema(Categories):LocalReviewJson.ResponseSchema(Categories)},messages};
   using var body=new StringContent(JsonSerializer.Serialize(request),Encoding.UTF8,"application/json");
   using var response=client.PostAsync(reviewer.Name=="Gemma"?"/completion":"/v1/chat/completions",body,token).GetAwaiter().GetResult();
   response.EnsureSuccessStatusCode();
   var json=response.Content.ReadAsStringAsync(token).GetAwaiter().GetResult();
   var output=reviewer.Name=="Gemma"?LocalReviewEnvelope.ReadCompletion(json):LocalReviewEnvelope.ReadChat(json);
   var candidates=new List<Candidate>();
   foreach(var entity in reviewer.Name=="NuExtract"?LocalReviewJson.ReadNuExtract(output,Categories,readableSection):LocalReviewJson.Read(output,Categories,readableSection))
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
    candidates.Add(new Candidate{Value=value,Category=category,Confidence=0,Source=reviewer.Source,Explanation=reviewer.Name+" local reviewer suggestion."});
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
   if(GenericLowercaseWords.Contains(value))return true;
   var compact=Regex.Replace(value,@"\s+"," ").Trim();
   if(Regex.IsMatch(compact,@"^(?:(?:and\s+)?(?:updated|prepared|reviewed|approved|accepted|submitted)\s+by)(?:\s+(?:update|approval|review|submission)\s+date)?$",RegexOptions.IgnoreCase))return true;
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
   // Conflicts remain visible at the release gate, but never authorize
   // automatic replacement. Agreement is evidence, not a confidence score.
   if(candidate.RequiresContextReview)return true;
   if(candidate.ConsensusVotes>=2&&candidate.RecommendedReplace)return true;
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

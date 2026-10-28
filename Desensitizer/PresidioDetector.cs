using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;

namespace Desensitizer
{
 public sealed class AnalysisResult
 {
  public List<Candidate> Candidates { get; set; } = new List<Candidate>();
  public bool UsedPresidio { get; set; }
  public List<string> Engines { get; set; } = new List<string>();
  public string Warning { get; set; } = "";
  public bool Complete { get; set; }
  // Non-null when no integrity manifest ships beside the app (workers were not hash-checked). Callers may show it.
  public string? IntegrityWarning => RuntimeIntegrity.Default.IntegrityWarning;
  public string EngineLabel => Engines.Count == 0 ? "built-in safety rules" : string.Join(" + ", Engines);
 }

 public sealed class AnalysisProgress
 {
  public int Step { get; set; }
  public int Total { get; set; } = 6;
  public string Name { get; set; } = "";
  public string Detail { get; set; } = "";
 }

 public static class PresidioDetector
 {
  // Every regex that runs over document-sized or model-supplied text has a time limit.
  public static readonly TimeSpan RegexTimeout=TimeSpan.FromSeconds(2);

  private sealed class SidecarResponse { public List<Candidate>? Candidates {get;set;} public string? Error {get;set;} }
  private sealed class BatchSidecarResponse { public List<List<Candidate>>? Batches {get;set;} public string? Error {get;set;} }
  private sealed class EngineDefinition
  {
   public string Name { get; set; } = "";
   public string Path { get; set; } = "";
   public int TimeoutMilliseconds { get; set; } = 120000;
  }

  public static AnalysisResult Find(string text,Action<AnalysisProgress>? progress=null,CancellationToken cancellationToken=default,Action<IReadOnlyList<Candidate>>? findings=null)
  {
   cancellationToken.ThrowIfCancellationRequested();
   Report(progress,2,"Checking exact patterns","Looking for emails, phone numbers, identifiers, and known formatting patterns.");
   var fallback=Detector.Find(text).ToList();
   PublishFindings(text,fallback,findings);
   var engines=Engines();
   var combined=new List<Candidate>(fallback);
   var used=new List<string>();
   var warnings=new List<string>();
   // Engines run at most DetectorConcurrency at a time (2 by default; 1 on a small
   // machine). Results are merged in engine order, so the output equals a sequential run.
   var outcomes=PerfEngineRunner.RunAll<List<Candidate>>(engines.Select(x=>x.Name).ToList(),
    index=>File.Exists(engines[index].Path),
    (index,token)=>RunEngine(engines[index],text,token).ToList(),
    ConcurrencyFor(engines.Length),cancellationToken,
    index=>
    {
     var engine=engines[index];
     var detail=engine.Name=="Presidio"?"Checking conventional identity and contact information.":engine.Name=="GLiNER"?"Understanding organizations, projects, vendors, products, and internal systems.":"Running an independent context-aware privacy review.";
     Report(progress,index+3,engine.Name,detail);
    },
    outcome=>
    {
     if(outcome.Skipped)Report(progress,outcome.Index+3,outcome.Name,outcome.Name+" is not installed; this layer will be skipped.");
     else if(outcome.Succeeded)PublishFindings(text,outcome.Result!,findings);
    });
   foreach(var outcome in outcomes)
   {
    if(outcome.Skipped){warnings.Add(outcome.Name+" is not installed.");continue;}
    if(outcome.Error!=null){warnings.Add(outcome.Name+" could not start: "+outcome.Error.Message);continue;}
    combined.AddRange(outcome.Result!);
    used.Add(outcome.Name);
   }
   cancellationToken.ThrowIfCancellationRequested();
   Report(progress,6,"Finalizing findings","Combining duplicate findings and keeping the strongest local result for review.");
   if(!engines.Any(x=>File.Exists(x.Path)))warnings.Add("Local analyzers are missing; built-in safety rules were used.");
   var deduped=combined.Where(IsEligibleCandidate)
    .GroupBy(x=>Project.Normalize(x.Value))
    .Select(g=>{var best=g.OrderByDescending(SourcePriority).ThenByDescending(x=>x.Confidence).First();best.DetectionSources=g.Select(x=>x.Source).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct().ToList();return best;}).ToList();
   return new AnalysisResult{Candidates=FindingPostProcessor.Complete(text,deduped).Where(IsEligibleCandidate).ToList(),UsedPresidio=used.Contains("Presidio"),Engines=used,Complete=used.Count==engines.Length,Warning=string.Join(" ",warnings)};
  }

  public static AnalysisResult FindSections(IReadOnlyList<string> sections,Action<AnalysisProgress>? progress=null,CancellationToken cancellationToken=default,Action<IReadOnlyList<Candidate>>? findings=null)
  {
   if(sections.Count==0)return new AnalysisResult{Complete=true};
   var sectionFindings=sections.Select(section=>Detector.Find(section).ToList()).ToList();
   for(var i=0;i<sections.Count;i++)PublishFindings(sections[i],sectionFindings[i],findings);
   var engines=Engines();
   var warnings=new List<string>();var used=new List<string>();
   var outcomes=PerfEngineRunner.RunAll<List<List<Candidate>>>(engines.Select(x=>x.Name).ToList(),
    index=>File.Exists(engines[index].Path),
    (index,token)=>RunSectionsWithFallback(engines[index],sections,token),
    ConcurrencyFor(engines.Length),cancellationToken,
    index=>progress?.Invoke(new AnalysisProgress{Step=index+1,Total=engines.Length,Name="Deep check",Detail="Checking "+sections.Count+" sections with "+engines[index].Name+"."}),
    outcome=>
    {
     if(!outcome.Succeeded)return;
     for(var i=0;i<sections.Count;i++)PublishFindings(sections[i],outcome.Result![i],findings);
    });
   foreach(var outcome in outcomes)
   {
    if(outcome.Skipped){warnings.Add(outcome.Name+" is not installed.");continue;}
    if(outcome.Error!=null){warnings.Add(outcome.Name+" could not finish: "+outcome.Error.Message);continue;}
    for(var i=0;i<sections.Count;i++)sectionFindings[i].AddRange(outcome.Result![i]);
    used.Add(outcome.Name);
   }
   var combined=new List<Candidate>();
   for(var i=0;i<sections.Count;i++)
   {
    var deduped=sectionFindings[i].Where(IsEligibleCandidate)
     .GroupBy(x=>Project.Normalize(x.Value))
     .Select(g=>{var best=g.OrderByDescending(SourcePriority).ThenByDescending(x=>x.Confidence).First();best.DetectionSources=g.Select(x=>x.Source).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct().ToList();return best;});
    combined.AddRange(FindingPostProcessor.Complete(sections[i],deduped).Where(IsEligibleCandidate));
   }
   return new AnalysisResult{Candidates=combined,UsedPresidio=used.Contains("Presidio"),Engines=used,Complete=used.Count==engines.Length,Warning=string.Join(" ",warnings)};
  }

  private static int ConcurrencyFor(int engineCount)=>PerfPolicy.DecideDetectorConcurrency(ResourceUsage.TotalPhysicalBytes(),engineCount,PerfConfig.DetectorConcurrency);

  // One engine over all sections: the batch call, then (for a legacy sidecar
  // without batch support) one call per section. Every section is always checked;
  // when both fail the message names both failures, as before.
  private static List<List<Candidate>> RunSectionsWithFallback(EngineDefinition engine,IReadOnlyList<string> sections,CancellationToken cancellationToken)
  {
   try{return RunEngineBatch(engine,sections,cancellationToken);}
   catch(OperationCanceledException){throw;}
   catch(Exception batchError)
   {
    try
    {
     var result=new List<List<Candidate>>();
     for(var i=0;i<sections.Count;i++)
     {
      cancellationToken.ThrowIfCancellationRequested();
      result.Add(RunEngine(engine,sections[i],cancellationToken).ToList());
     }
     return result;
    }
    catch(OperationCanceledException){throw;}
    catch(Exception fallbackError){throw new InvalidOperationException(batchError.Message+" "+fallbackError.Message);}
   }
  }

  // The DESENSITIZER_*_PATH overrides are honoured only when DESENSITIZER_DEV=1.
  private static EngineDefinition[] Engines()
  {
   var baseDirectory=AppContext.BaseDirectory;
   return new[]
   {
    new EngineDefinition{Name="Presidio",Path=RuntimeOverrides.Get("DESENSITIZER_ANALYZER_PATH")??Path.Combine(baseDirectory,"analyzer","DesensitizerAnalyzer.exe"),TimeoutMilliseconds=120000},
    new EngineDefinition{Name="GLiNER",Path=RuntimeOverrides.Get("DESENSITIZER_GLINER_PATH")??Path.Combine(baseDirectory,"model-packs","business-context","BusinessContextAnalyzer.exe"),TimeoutMilliseconds=180000},
    new EngineDefinition{Name="Privacy Filter",Path=RuntimeOverrides.Get("DESENSITIZER_PRIVACY_FILTER_PATH")??Path.Combine(baseDirectory,"model-packs","privacy-filter","PrivacyFilterAnalyzer.exe"),TimeoutMilliseconds=300000}
   };
  }

  // Parses a worker answer without ever echoing worker stderr or input fragments.
  private static T ParseResponse<T>(WorkerOutput result) where T:class
  {
   T? response;
   try{response=JsonSerializer.Deserialize<T>(result.Output,new JsonSerializerOptions{PropertyNameCaseInsensitive=true});}
   catch(JsonException){throw new InvalidOperationException("The analyzer returned an unreadable answer.");}
   if(response==null)throw new InvalidOperationException("The analyzer returned no answer (exit code "+result.ExitCode+").");
   return response;
  }

  private static void Report(Action<AnalysisProgress>? progress,int step,string name,string detail)=>progress?.Invoke(new AnalysisProgress{Step=step,Total=6,Name=name,Detail=detail});

  private static void PublishFindings(string text,IEnumerable<Candidate> candidates,Action<IReadOnlyList<Candidate>>? findings)
  {
   if(findings==null)return;
   var batch=FindingPostProcessor.Complete(text,candidates.Where(IsEligibleCandidate)).Where(IsEligibleCandidate).ToList();
   if(batch.Count>0)findings(batch);
  }

  public static bool IsEligibleCandidate(Candidate candidate)
  {
   return !string.IsNullOrWhiteSpace(candidate.Value)&&!LooksLikeRoleTitle(candidate)&&!LooksClearlyNonSensitive(candidate);
  }

  private static IEnumerable<Candidate> RunEngine(EngineDefinition engine,string text,CancellationToken cancellationToken)
  {
   cancellationToken.ThrowIfCancellationRequested();
   // A worker whose hash does not match the shipped manifest is never started.
   RuntimeIntegrity.Default.Require(engine.Path);
   var result=RuntimeWorkerProcess.Run(new ProcessStartInfo(engine.Path),JsonSerializer.Serialize(new{text}),engine.TimeoutMilliseconds,cancellationToken);
   var response=ParseResponse<SidecarResponse>(result);
   if(result.ExitCode!=0||!string.IsNullOrWhiteSpace(response.Error))
    throw new InvalidOperationException(RuntimeWorkerProcess.SafeMessage(response.Error,"The analyzer failed (exit code "+result.ExitCode+")."));
   return response.Candidates??new List<Candidate>();
  }

  private static List<List<Candidate>> RunEngineBatch(EngineDefinition engine,IReadOnlyList<string> sections,CancellationToken cancellationToken)
  {
   cancellationToken.ThrowIfCancellationRequested();
   RuntimeIntegrity.Default.Require(engine.Path);
   var result=RuntimeWorkerProcess.Run(new ProcessStartInfo(engine.Path),JsonSerializer.Serialize(new{texts=sections}),Math.Min(1800000,engine.TimeoutMilliseconds*sections.Count),cancellationToken);
   var response=ParseResponse<BatchSidecarResponse>(result);
   if(result.ExitCode!=0||response.Batches==null||response.Batches.Count!=sections.Count||response.Batches.Any(batch=>batch==null)||!string.IsNullOrWhiteSpace(response.Error))
    throw new InvalidOperationException(RuntimeWorkerProcess.SafeMessage(response.Error,"The batch response is incomplete (exit code "+result.ExitCode+")."));
   return response.Batches;
  }

  private static int SourcePriority(Candidate candidate)
  {
   if(candidate.Source=="Privacy Filter")return 4;
   if(candidate.Source=="GLiNER")return 3;
   if(candidate.Source=="Presidio")return 2;
   return 1;
  }

  private static bool LooksLikeRoleTitle(Candidate candidate)
  {
   List<string> words;
   try{words=Regex.Matches(candidate.Value.ToLowerInvariant(),@"[a-z]+",RegexOptions.None,RegexTimeout).Cast<Match>().Select(x=>x.Value).ToList();}
   catch(RegexMatchTimeoutException){return false;}
   var roleWords=new HashSet<string>(new[]{"analyst","application","architect","backend","business","cloud","computing","consultant","developer","engineer","frontend","information","junior","lead","manager","services","software","specialist","system","technical","technology"});
   return words.Count>0&&words.All(word=>roleWords.Contains(word)||(word.EndsWith("s",StringComparison.Ordinal)&&roleWords.Contains(word.Substring(0,word.Length-1))));
  }

  private static bool LooksClearlyNonSensitive(Candidate candidate)
  {
   // Presidio explicitly classifies IP addresses as identifiers. Preserve that
   // classification even though an IPv4 address can resemble an outline number.
   if(candidate.Source=="Presidio"&&candidate.Category=="IDENTIFIER"&&candidate.Explanation.Contains("ip",StringComparison.OrdinalIgnoreCase))return false;
   // A username printed after an explicit Username/Login label is evidence on its own, so it keeps
   // the no-digit exemption. Model-sourced identifiers still need a digit.
   var labelAnchored=candidate.Category=="IDENTIFIER"&&(candidate.Source=="Explicit username"||(candidate.DetectionSources?.Contains("Explicit username")??false));
   return LocalAiReview.IsClearlyGenericCandidate(candidate.Value,candidate.Category,labelAnchored);
  }
 }
}

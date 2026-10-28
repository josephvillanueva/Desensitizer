using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Desensitizer
{
 public sealed class DeepReviewResult
 {
  public List<Candidate> Candidates {get;set;}=new List<Candidate>();
  public bool Complete {get;set;}=true;
  public string Warning {get;set;}="";
  public int SectionsChecked {get;set;}
 }

 // Detector sections are followed by three distinct bundled local language models.
 public static class DeepReview
 {
  public static IReadOnlyList<string> Sections(string text,int limit=1800,int overlap=240)
  {
   if(string.IsNullOrWhiteSpace(text))return Array.Empty<string>();
   if(limit<=overlap||overlap<0)throw new ArgumentOutOfRangeException(nameof(limit));
   var sections=new List<string>();
   for(var start=0;start<text.Length;)
   {
    var end=Math.Min(start+limit,text.Length);
    if(end<text.Length)
    {
     var breakAt=text.LastIndexOfAny(new[]{'\n','.',';',' '},end-1,end-start);
     if(breakAt>start+limit/2)end=breakAt+1;
    }
    sections.Add(text.Substring(start,end-start));
    if(end==text.Length)break;
    start=Math.Max(start+1,end-overlap);
   }
   return sections;
  }

  public static DeepReviewResult Find(string text,Action<AnalysisProgress>? progress=null,CancellationToken cancellationToken=default,Action<IReadOnlyList<Candidate>>? findings=null,TableReviewContext? tableContext=null,ExportReviewCache? cache=null)
  {
   var sections=Sections(text);
   var candidates=new List<Candidate>();var warnings=new List<string>();var complete=true;
   if(sections.Count>0)
   {
    cancellationToken.ThrowIfCancellationRequested();
    var result=PresidioDetector.FindSections(sections,p=>progress?.Invoke(new AnalysisProgress{Step=Math.Min(sections.Count,p.Step*sections.Count/3),Total=sections.Count,Name=p.Name,Detail=p.Detail}),cancellationToken,findings);
    candidates.AddRange(result.Candidates);
    if(!result.Complete){complete=false;warnings.Add(result.Warning);}
   }
   if(sections.Count>0)
   {
    try
    {
     // A failed section does not discard the other sections' findings. The
     // result is marked incomplete and names the sections that were not checked.
     var ai=LocalAiReview.FindDetailed(text,p=>progress?.Invoke(new AnalysisProgress{Step=sections.Count+p.Step,Total=sections.Count+p.Total,Name=p.Name,Detail=p.Detail}),cancellationToken,findings,candidates,cache,tableContext);
     candidates.AddRange(ai.Candidates);
     if(!ai.Complete){complete=false;warnings.Add(ai.Warning);}
    }
    catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
    catch(Exception ex){complete=false;warnings.Add("Local AI review could not finish: "+ex.Message);}
   }
   cancellationToken.ThrowIfCancellationRequested();
   ReviewDisagreement.Mark(text,candidates);
   var merged=candidates.GroupBy(x=>Project.Normalize(x.Value)).Select(group=>{var best=group.OrderByDescending(c=>c.RequiresContextReview).ThenByDescending(c=>c.ConsensusVotes).ThenByDescending(c=>c.Confidence).First();best.RequiresContextReview=group.Any(c=>c.RequiresContextReview);if(best.RequiresContextReview)best.RecommendedReplace=false;best.DetectionSources=group.SelectMany(x=>x.DetectionSources??new List<string>()).Concat(group.Select(x=>x.Source)).Distinct().ToList();return best;});
   return new DeepReviewResult{Candidates=FindingPostProcessor.Complete(text,merged),Complete=complete,Warning=string.Join(" ",warnings),SectionsChecked=sections.Count};
  }
 }
}

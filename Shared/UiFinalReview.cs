using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Desensitizer
{
 public enum FinalReviewChoice { Cancel, RunAiReview, ExportWithoutAi }

 // Item U8: the final AI review of the protected copy is no longer something that is silently skipped.
 // With Automatic deep check off, every export asks once, in plain words, and the safer answer is the default.
 public static class UiFinalReview
 {
  public const string DialogTitle="Final AI review is off";
  public const string RunButton="Run the AI review too";
  public const string SkipButton="Export without the AI review";

  // The question is needed only when the setting leaves the AI out of the final scan.
  public static bool NeedsChoice(bool automaticDeepReview)=>!automaticDeepReview;

  // Whether the final scan includes the AI reviewers. Cancel never exports, so it never reaches this.
  public static bool AiReviewWillRun(bool automaticDeepReview,FinalReviewChoice choice)=>automaticDeepReview||choice==FinalReviewChoice.RunAiReview;

  // False only when the user closed the question: nothing is exported.
  public static bool MayExport(bool automaticDeepReview,FinalReviewChoice choice)=>automaticDeepReview||choice!=FinalReviewChoice.Cancel;

  // The description in the "Export complete" dialog: says when the AI review was skipped and counts advice not applied.
  public static string CompletionText(bool aiReviewRan,int advisoryCount)
  {
   var text="Local checks passed"+(aiReviewRan?"":" (rule-based detectors only: the final AI review was skipped)")+". Review the desensitized copy before sharing it.";
   var line=UiAdvisory.Line(advisoryCount);
   return line.Length==0?text:text+"\n\n"+line;
  }

  public static string Explanation(int reviewerCount,string estimate)
  {
   var reviewers=reviewerCount<=1?"The AI reviewer":"The AI reviewers";
   var text="With the final AI review off, only the rule-based detectors check the protected copy. "+reviewers+" will not look for names, companies or systems in it, so a value the detectors do not recognise could remain.";
   text+="\n\nRunning the AI review too is slower but is the stronger check.";
   if(!string.IsNullOrWhiteSpace(estimate))text+=" "+estimate+".";
   return text;
  }
 }

 // Project, system and product names found only by an AI reviewer are advice: they are never replaced by themselves
 // and never block an export. They are counted (never shown) so they are not silently ignored.
 public static class UiAdvisory
 {
  public static readonly IReadOnlyList<string> Categories=new[]{"PROJECT","SYSTEM","PRODUCT"};

  public static bool IsAdvisoryCategory(string? category)=>category!=null&&Categories.Contains(category.Trim().ToUpperInvariant());

  public static bool IsAiSource(string? source)=>!string.IsNullOrWhiteSpace(source)&&(string.Equals(source,"Local AI",StringComparison.OrdinalIgnoreCase)||source.StartsWith("AI",StringComparison.OrdinalIgnoreCase));

  // Mappings of this file in an advisory category, found only by AI, that the user has not decided on.
  public static int CountOpenMappings(IEnumerable<Mapping> mappings,string sourceFileId)
  {
   return mappings.Count(m=>IsOpenAdvisory(m,sourceFileId));
  }

  private static bool IsOpenAdvisory(Mapping m,string sourceFileId)
  {
   if(m==null||m.IsManual||m.Approved||m.Ignored||!IsAdvisoryCategory(m.Category))return false;
   if(!(m.SourceFileIds?.Contains(sourceFileId)??false))return false;
   var sources=(m.DetectionSources??new List<string>()).Where(s=>!string.IsNullOrWhiteSpace(s)).ToList();
   if(sources.Count==0&&!string.IsNullOrWhiteSpace(m.DetectionSource))sources.Add(m.DetectionSource);
   return sources.Count>0&&sources.All(IsAiSource);
  }

  // Open mappings plus advisory AI findings of the final scan that have no mapping the user decided on.
  public static int Count(IEnumerable<Mapping> mappings,string sourceFileId,IEnumerable<Candidate>? finalScanCandidates)
  {
   var all=mappings.ToList();
   var keys=new HashSet<string>(StringComparer.Ordinal);
   foreach(var m in all)if(IsOpenAdvisory(m,sourceFileId))keys.Add(Project.Normalize(m.OriginalValue));
   foreach(var c in finalScanCandidates??Array.Empty<Candidate>())
   {
    if(c==null||string.IsNullOrWhiteSpace(c.Value)||!IsAdvisoryCategory(c.Category))continue;
    var sources=(c.DetectionSources??new List<string>()).Where(s=>!string.IsNullOrWhiteSpace(s)).ToList();
    if(sources.Count==0)sources.Add(c.Source);
    if(!sources.All(IsAiSource))continue;
    var key=Project.Normalize(c.Value);
    var decided=all.Any(m=>(m.Approved||m.Ignored)&&Project.Normalize(m.OriginalValue)==key);
    if(!decided)keys.Add(key);
   }
   return keys.Count;
  }

  // Count only, never the values.
  public static string Line(int count)
  {
   if(count<=0)return "";
   return count==1?"1 AI suggestion was not applied (a project, system or product name found only by an AI reviewer). It is advice only and stays in Review."
                  :count+" AI suggestions were not applied (project, system or product names found only by AI reviewers). They are advice only and stay in Review.";
  }
 }

 // The one-time question when an export would run without the final AI review.
 public sealed class FinalReviewChoiceDialog : Window
 {
  public FinalReviewChoice Choice {get;private set;}=FinalReviewChoice.Cancel;
  public FinalReviewChoiceDialog(int reviewerCount,string estimate,int advisoryCount)
  {
   DialogLayout.Configure(this,UiFinalReview.DialogTitle,540,330,true);
   var panel=DialogLayout.Panel();
   panel.Children.Add(new TextBlock{Text="Check the protected copy with the AI too?",FontSize=17,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
   panel.Children.Add(new TextBlock{Text=UiFinalReview.Explanation(reviewerCount,estimate),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)});
   var advisory=UiAdvisory.Line(advisoryCount);
   if(advisory.Length>0)panel.Children.Add(DialogLayout.Note(advisory,10));
   var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0)};
   var skip=new Button{Content=UiFinalReview.SkipButton,MinHeight=36,Padding=new Thickness(12,0,12,0),Margin=new Thickness(0,0,8,0),ToolTip="Only the rule-based detectors check the protected copy."};
   skip.Click+=(s,e)=>{Choice=FinalReviewChoice.ExportWithoutAi;DialogResult=true;};
   var run=DialogLayout.Primary(UiFinalReview.RunButton);run.Margin=new Thickness(0);run.Padding=new Thickness(12,0,12,0);run.Click+=(s,e)=>{Choice=FinalReviewChoice.RunAiReview;DialogResult=true;};
   actions.Children.Add(skip);actions.Children.Add(run);panel.Children.Add(actions);Content=panel;
   Loaded+=(s,e)=>run.Focus();
  }
 }
}

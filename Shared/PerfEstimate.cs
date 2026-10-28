using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Desensitizer
{
 // Rolling average of measured seconds per section per reviewer (one reviewer
 // pass over one section). Only the numbers are kept, never any text.
 public sealed class PerfTimingTracker
 {
  private readonly Queue<double> samples=new Queue<double>();
  private readonly object gate=new object();
  public int Window {get;}
  public PerfTimingTracker(int window=20){Window=Math.Max(1,window);}

  public int Count{get{lock(gate){return samples.Count;}}}

  // seconds is the time one section took across reviewerPasses reviewer passes.
  public void Add(double seconds,int reviewerPasses=1)
  {
   if(double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<=0||reviewerPasses<1)return;
   lock(gate)
   {
    samples.Enqueue(seconds/reviewerPasses);
    while(samples.Count>Window)samples.Dequeue();
   }
  }

  public double? AverageSecondsPerPass{get{lock(gate){return samples.Count==0?null:samples.Average();}}}
 }

 // Human-readable estimate shown before a long scan. All numbers are estimates:
 // with no measurements it uses deliberately conservative defaults, and says so.
 public static class PerfEstimate
 {
  // Conservative defaults for a CPU-only 4B model on a work laptop (not measured):
  // a fixed cost per pass plus a cost per 1000 characters.
  public const double DefaultFixedSecondsPerPass=6;
  public const double DefaultSecondsPer1000Chars=9;
  // Used when only the total text length is known.
  public const int AssumedSectionChars=1500;

  public sealed class Result
  {
   public int Sections {get;init;}
   public int Reviewers {get;init;}
   public double Seconds {get;init;}
   public bool Measured {get;init;}
   public string Text {get;init;}="";
  }

  public static Result FromTextLength(int textLength,int reviewers,PerfTimingTracker? tracker=null)
  {
   var sections=Math.Max(0,textLength)==0?0:(int)Math.Ceiling(textLength/(double)AssumedSectionChars);
   return FromSections(Enumerable.Repeat(Math.Min(AssumedSectionChars,Math.Max(0,textLength)),sections).ToList(),reviewers,tracker);
  }

  public static Result FromSections(IReadOnlyList<int> sectionSizes,int reviewers,PerfTimingTracker? tracker=null)
  {
   reviewers=Math.Max(0,reviewers);
   var sections=sectionSizes.Count;
   var measuredAverage=tracker?.AverageSecondsPerPass;
   double seconds;
   if(measuredAverage.HasValue)seconds=sections*reviewers*measuredAverage.Value;
   else seconds=sectionSizes.Sum(size=>DefaultFixedSecondsPerPass+Math.Max(0,size)/1000.0*DefaultSecondsPer1000Chars)*reviewers;
   var measured=measuredAverage.HasValue;
   string text;
   if(sections==0||reviewers==0)text="Nothing to review.";
   else
   {
    var what=sections.ToString(CultureInfo.InvariantCulture)+(sections==1?" section":" sections")+" x "+reviewers.ToString(CultureInfo.InvariantCulture)+(reviewers==1?" reviewer":" reviewers");
    text="about "+what+", roughly "+FormatDuration(seconds)+(measured?"":" (first estimate, may differ)");
   }
   return new Result{Sections=sections,Reviewers=reviewers,Seconds=seconds,Measured=measured,Text=text};
  }

  // Rounds so it never claims false precision: whole minutes under 10 minutes,
  // 5 minute steps up to an hour and beyond.
  public static string FormatDuration(double seconds)
  {
   if(double.IsNaN(seconds)||seconds<=0)return "under 1 min";
   if(seconds<60)return "under 1 min";
   var minutes=seconds/60.0;
   int rounded=minutes<10?(int)Math.Round(minutes,MidpointRounding.AwayFromZero):(int)(Math.Round(minutes/5.0,MidpointRounding.AwayFromZero)*5);
   if(rounded<1)rounded=1;
   if(rounded<60)return rounded.ToString(CultureInfo.InvariantCulture)+" min";
   var hours=rounded/60;var rest=rounded%60;
   return rest==0?hours.ToString(CultureInfo.InvariantCulture)+" h":hours.ToString(CultureInfo.InvariantCulture)+" h "+rest.ToString(CultureInfo.InvariantCulture)+" min";
  }
 }
}

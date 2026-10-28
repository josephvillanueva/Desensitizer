using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Desensitizer
{
 // Collects memory samples (numbers only, never document text) and produces a
 // CSV plus a peak summary. tools/Measure-PeakMemory.ps1 writes the same CSV
 // format from outside the app; in-process callers feed it ResourceUsage.Snapshot().
 public sealed class PerfMemoryLog
 {
  public sealed class Sample
  {
   public double ElapsedSeconds {get;init;}
   public long TotalWorkingSetBytes {get;init;}
   public int ProcessCount {get;init;}
  }

  private readonly List<Sample> samples=new List<Sample>();
  public IReadOnlyList<Sample> Samples=>samples;

  public void Add(double elapsedSeconds,long totalWorkingSetBytes,int processCount)
  {
   if(totalWorkingSetBytes<0||processCount<0)return;
   samples.Add(new Sample{ElapsedSeconds=elapsedSeconds,TotalWorkingSetBytes=totalWorkingSetBytes,ProcessCount=processCount});
  }

  public Sample? Peak
  {
   get
   {
    Sample? best=null;
    foreach(var s in samples)if(best==null||s.TotalWorkingSetBytes>best.TotalWorkingSetBytes)best=s;
    return best;
   }
  }

  public const string CsvHeader="elapsed_seconds,total_working_set_mb,process_count";

  public string ToCsv()
  {
   var b=new StringBuilder();
   b.AppendLine(CsvHeader);
   foreach(var s in samples)
    b.Append(s.ElapsedSeconds.ToString("0.0",CultureInfo.InvariantCulture)).Append(',')
     .Append((s.TotalWorkingSetBytes/1048576.0).ToString("0.0",CultureInfo.InvariantCulture)).Append(',')
     .Append(s.ProcessCount.ToString(CultureInfo.InvariantCulture)).AppendLine();
   return b.ToString();
  }

  public string Summary()
  {
   var peak=Peak;
   if(peak==null)return "No samples.";
   return "Peak "+(peak.TotalWorkingSetBytes/1073741824.0).ToString("0.00",CultureInfo.InvariantCulture)+" GB across "+peak.ProcessCount.ToString(CultureInfo.InvariantCulture)+" processes at "+peak.ElapsedSeconds.ToString("0",CultureInfo.InvariantCulture)+" s ("+samples.Count.ToString(CultureInfo.InvariantCulture)+" samples).";
  }
 }
}

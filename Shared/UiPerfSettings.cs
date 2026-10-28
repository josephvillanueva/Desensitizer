using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Desensitizer
{
 // Logic of the Performance group of the Settings dialog (no window code, so it is tested directly).
 public static class UiPerfSettings
 {
  public const string Automatic="Automatic";
  public const int MaxResidentLimit=3;
  public const int MaxDetectorLimit=3;
  public const int MaxIdleMinutes=60;
  public const int DefaultIdleMinutes=5;

  public static string Display(int value)=>value<=0?Automatic:value.ToString(CultureInfo.InvariantCulture);

  // Blank, "Automatic", "auto" and 0 mean automatic. A whole number above the limit is lowered to the limit.
  // Anything else (letters, decimals, negative numbers) is not accepted and the problem is returned.
  public static bool TryParse(string? text,int maximum,out int value,out string? problem)
  {
   value=0;problem=null;
   var trimmed=(text??"").Trim();
   if(trimmed.Length==0||trimmed.Equals(Automatic,StringComparison.OrdinalIgnoreCase)||trimmed.Equals("auto",StringComparison.OrdinalIgnoreCase))return true;
   if(trimmed.Length>9||!int.TryParse(trimmed,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed))
   {
    problem="Enter a whole number from 0 to "+maximum+", or leave the field as Automatic.";
    return false;
   }
   value=Math.Clamp(parsed,0,maximum);
   return true;
  }

  // Idle minutes have no automatic form: blank or 0 means the default of 5, other values are kept within 1 to 60.
  public static bool TryParseIdle(string? text,out int value,out string? problem)
  {
   if(!TryParse(text,MaxIdleMinutes,out value,out problem))return false;
   if(value<=0)value=DefaultIdleMinutes;
   return true;
  }

  public static int ThreadLimit(int logicalCores)=>Math.Max(1,logicalCores);

  // Validates all four fields and writes them into the settings only if every one is acceptable.
  // field: which field had the problem (0 threads, 1 models, 2 detectors, 3 idle), or -1.
  public static string? Apply(AppSettings target,string? threads,string? resident,string? detectors,string? idle,int logicalCores,int modelCount,out int field)
  {
   field=-1;
   if(!TryParse(threads,ThreadLimit(logicalCores),out var t,out var problem)){field=0;return "AI threads: "+problem;}
   if(!TryParse(resident,Math.Clamp(modelCount,1,MaxResidentLimit),out var r,out problem)){field=1;return "AI models kept loaded: "+problem;}
   if(!TryParse(detectors,MaxDetectorLimit,out var d,out problem)){field=2;return "Detector engines at once: "+problem;}
   if(!TryParseIdle(idle,out var i,out problem)){field=3;return "Idle minutes: "+problem;}
   target.LlamaThreads=t;target.MaxResidentModels=r;target.DetectorConcurrency=d;target.WorkerIdleMinutes=i;
   return null;
  }

  // The saved performance values must survive when the Settings dialog copies the other settings.
  public static void CopyPerformance(AppSettings from,AppSettings to)
  {
   to.LlamaThreads=Math.Clamp(from.LlamaThreads,0,256);
   to.MaxResidentModels=Math.Clamp(from.MaxResidentModels,0,MaxResidentLimit);
   to.DetectorConcurrency=Math.Clamp(from.DetectorConcurrency,0,MaxDetectorLimit);
   to.WorkerIdleMinutes=Math.Clamp(from.WorkerIdleMinutes<=0?DefaultIdleMinutes:from.WorkerIdleMinutes,1,MaxIdleMinutes);
  }

  public static string Gigabytes(long bytes)=>bytes<=0?"unknown":(bytes/1073741824.0).ToString("0.0",CultureInfo.InvariantCulture)+" GB";

  // Read-only line: what this computer has and what is in use right now (before any change made in the dialog).
  public static string EffectiveSummary(long totalBytes,int logicalCores,int llamaThreads,int residentModels,int detectorEngines,int idleMinutes)
  =>"This computer: "+Gigabytes(totalBytes)+" memory, "+logicalCores.ToString(CultureInfo.InvariantCulture)+" logical cores.\nIn use now: "+llamaThreads.ToString(CultureInfo.InvariantCulture)+" AI threads, "+residentModels.ToString(CultureInfo.InvariantCulture)+" AI model"+(residentModels==1?"":"s")+" kept loaded, "+detectorEngines.ToString(CultureInfo.InvariantCulture)+" detector engine"+(detectorEngines==1?"":"s")+" at once, workers close after "+idleMinutes.ToString(CultureInfo.InvariantCulture)+" idle minutes.";

  public const string ThreadsTip="How many processor threads each AI model uses. Automatic leaves 2 cores free for this window and the detectors. More threads can be faster, but the computer feels slower while a check runs. It has little effect on memory.";
  public const string ResidentTip="How many AI models stay in memory between sections. Each loaded model takes several GB (roughly 3 to 4 GB for the larger ones, an estimate). On a 16 GB laptop Automatic keeps 1: switching models costs time but leaves room for Windows and the detectors. Choose 2 only if you have 24 GB or more.";
  public const string DetectorsTip="How many detector engines (Presidio, GLiNER, Privacy Filter) run at the same time. Each uses roughly 1 to 2 GB (an estimate). On a 16 GB laptop Automatic runs 2 at once; choose 1 if the computer becomes sluggish or runs short of memory.";
  public const string IdleTip="How long a detector worker stays loaded after its last use before it closes and gives its memory back. Longer is faster for repeated checks, shorter frees memory sooner.";
  public const string AppliesNote="Changes apply to the next scan. A check that is already running keeps its current settings. Automatic is recommended.";
 }

 // The "Performance" group of the Settings dialog. Both apps add it with one line and read it back with Apply.
 public sealed class PerformanceSettingsPanel : StackPanel
 {
  private readonly TextBox _threads=DialogLayout.Field(new TextBox{MaxLength=9,MinWidth=110,HorizontalAlignment=HorizontalAlignment.Left});
  private readonly TextBox _resident=DialogLayout.Field(new TextBox{MaxLength=9,MinWidth=110,HorizontalAlignment=HorizontalAlignment.Left});
  private readonly TextBox _detectors=DialogLayout.Field(new TextBox{MaxLength=9,MinWidth=110,HorizontalAlignment=HorizontalAlignment.Left});
  private readonly TextBox _idle=DialogLayout.Field(new TextBox{MaxLength=9,MinWidth=110,HorizontalAlignment=HorizontalAlignment.Left});
  private readonly int _cores=Environment.ProcessorCount;
  private readonly int _modelCount;

  public PerformanceSettingsPanel(AppSettings current,int modelCount)
  {
   _modelCount=Math.Max(1,modelCount);
   Children.Add(new TextBlock{Text="Performance",FontSize=17,FontWeight=FontWeights.SemiBold});
   Children.Add(DialogLayout.Note("Leave a field as Automatic unless a check is too slow or the computer runs short of memory. 0 also means Automatic.",6));
   AddField("AI threads",_threads,UiPerfSettings.Display(current.LlamaThreads),UiPerfSettings.ThreadsTip,"0 to "+UiPerfSettings.ThreadLimit(_cores));
   AddField("AI models kept loaded",_resident,UiPerfSettings.Display(current.MaxResidentModels),UiPerfSettings.ResidentTip,"0 to "+Math.Clamp(modelCount,1,UiPerfSettings.MaxResidentLimit));
   AddField("Detector engines at once",_detectors,UiPerfSettings.Display(current.DetectorConcurrency),UiPerfSettings.DetectorsTip,"0 to "+UiPerfSettings.MaxDetectorLimit);
   AddField("Idle minutes before a worker closes",_idle,UiPerfSettings.Display(Math.Max(1,current.WorkerIdleMinutes)),UiPerfSettings.IdleTip,"1 to "+UiPerfSettings.MaxIdleMinutes);
   Children.Add(DialogLayout.Note(UiPerfSettings.AppliesNote,10));
   long total=0;int threadsInUse=0;
   try{total=ResourceUsage.TotalPhysicalBytes();}catch(Exception){}
   try{threadsInUse=LlamaServerHost.DefaultThreads();}catch(Exception){}
   var resident=PerfPolicy.DecideMaxResident(total,Math.Max(1,modelCount),PerfConfig.MaxResidentModels);
   var detectorsInUse=PerfPolicy.DecideDetectorConcurrency(total,3,PerfConfig.DetectorConcurrency);
   var summary=DialogLayout.Note(UiPerfSettings.EffectiveSummary(total,_cores,threadsInUse,resident,detectorsInUse,PerfConfig.WorkerIdleMinutes),8);
   System.Windows.Automation.AutomationProperties.SetName(summary,"Values in use now");
   Children.Add(summary);
  }

  private void AddField(string label,TextBox box,string value,string tip,string range)
  {
   var text=DialogLayout.Label(label+" ("+range+")",12);
   Children.Add(text);
   box.Text=value;box.ToolTip=tip;
   DialogLayout.Named(box,label);
   System.Windows.Automation.AutomationProperties.SetLabeledBy(box,text);
   System.Windows.Automation.AutomationProperties.SetHelpText(box,tip);
   text.ToolTip=tip;
   Children.Add(box);
  }

  // Null when everything was accepted and written into target; otherwise the sentence to show (and the field is focused).
  public string? Apply(AppSettings target)
  {
   var problem=UiPerfSettings.Apply(target,_threads.Text,_resident.Text,_detectors.Text,_idle.Text,_cores,_modelCount,out var field);
   if(problem!=null)
   {
    var box=field switch{0=>_threads,1=>_resident,2=>_detectors,_=>_idle};
    box.Focus();box.SelectAll();
   }
   return problem;
  }
 }
}

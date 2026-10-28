using System;
using System.Globalization;

namespace Desensitizer
{
 // Pure, testable performance decisions. Nothing here starts a process or reads
 // the machine: callers pass the core count and RAM in.
 public static class PerfPolicy
 {
  public const string ThreadsVariable="DESENSITIZER_LLM_THREADS";
  public const long GiB=1073741824L;

  // llama-server --threads. Auto leaves two cores for the UI and the Python
  // workers (never fewer than 2 threads). A configured value (settings) is
  // clamped to 1..logicalCores.
  public static int DecideLlamaThreads(int logicalCores,int configured=0)
  {
   var cores=Math.Max(1,logicalCores);
   if(configured>0)return Math.Clamp(configured,1,cores);
   return Math.Max(2,cores-2);
  }

  // Production resolution: the settings value, overridden by
  // DESENSITIZER_LLM_THREADS only when DESENSITIZER_DEV=1.
  public static int ResolveLlamaThreads(int logicalCores,int configured,Func<string,string?> readEnvironment)
  {
   var fromEnvironment=RuntimeOverrides.Get(ThreadsVariable,readEnvironment);
   if(fromEnvironment!=null&&int.TryParse(fromEnvironment.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var value)&&value>0)configured=value;
   return DecideLlamaThreads(logicalCores,configured);
  }

  // How many llama-server models may stay loaded at once. 1 on 16 GB or less,
  // 2 from 24 GB, never more than the number of models, always within 1..3.
  // A configured value (>0) wins but is still clamped. Unknown RAM (<=0) is
  // treated as small, which is the memory-safe answer.
  public static int DecideMaxResident(long totalPhysicalBytes,int modelCount,int configured=0)
  {
   var ceiling=Math.Clamp(modelCount,1,3);
   if(configured>0)return Math.Clamp(configured,1,ceiling);
   var automatic=totalPhysicalBytes>=23*GiB?2:1;
   return Math.Clamp(automatic,1,ceiling);
  }

  // How many analyzer engines (Presidio, GLiNER, Privacy Filter) may run at the
  // same time. Default 2, but 1 when the machine has less than about 12 GB
  // (unknown RAM counts as small). Configured values are clamped to 1..3.
  public static int DecideDetectorConcurrency(long totalPhysicalBytes,int engineCount,int configured=0)
  {
   var ceiling=Math.Clamp(engineCount,1,3);
   if(configured>0)return Math.Clamp(configured,1,ceiling);
   var automatic=totalPhysicalBytes>=12*GiB?2:1;
   return Math.Clamp(automatic,1,ceiling);
  }
 }

 // Values taken from the persisted app settings (AppSettingsStore pushes them
 // here on every Load and Save). 0 means automatic.
 public static class PerfConfig
 {
  private static int llamaThreads,maxResident,detectorConcurrency,idleMinutes=5;
  public static int LlamaThreads=>llamaThreads;
  public static int MaxResidentModels=>maxResident;
  public static int DetectorConcurrency=>detectorConcurrency;
  public static int WorkerIdleMinutes=>idleMinutes;

  public static void Apply(int threads,int resident,int concurrency,int idle=5)
  {
   llamaThreads=Math.Clamp(threads,0,256);
   maxResident=Math.Clamp(resident,0,3);
   detectorConcurrency=Math.Clamp(concurrency,0,3);
   idleMinutes=Math.Clamp(idle,1,60);
  }
 }
}

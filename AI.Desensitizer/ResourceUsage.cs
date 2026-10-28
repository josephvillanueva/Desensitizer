using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Desensitizer
{
 public static class ResourceUsage
 {
  private static readonly ConcurrentDictionary<int,long> Workers=new ConcurrentDictionary<int,long>();
  private const uint SnapshotProcesses=0x00000002;

  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
  private struct ProcessEntry
  {
   public uint Size;
   public uint Usage;
   public uint ProcessId;
   public IntPtr DefaultHeapId;
   public uint ModuleId;
   public uint Threads;
   public uint ParentProcessId;
   public int BasePriority;
   public uint Flags;
   [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Executable;
  }

  [DllImport("kernel32.dll",SetLastError=true)]
  private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint processId);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="Process32FirstW",SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool Process32First(IntPtr snapshot,ref ProcessEntry entry);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="Process32NextW",SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry entry);
  [DllImport("kernel32.dll",SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CloseHandle(IntPtr handle);

  public static void RegisterWorker(Process process)
  {
   try{Workers[process.Id]=process.StartTime.ToUniversalTime().Ticks;}
   catch(InvalidOperationException){}
   catch(System.ComponentModel.Win32Exception){}
   // One process-wide job with KILL_ON_JOB_CLOSE: if the app dies, Windows ends every worker.
   RuntimeJob.Shared.TryAssign(process);
  }

  public static bool IsTrackedWorker(int processId)=>Workers.ContainsKey(processId);

  // Ends every tracked worker (and its children). Used by the crash handler and
  // by App.OnExit. Matching the recorded start time avoids a reused process id.
  public static void StopAllWorkers()
  {
   foreach(var worker in Workers.ToArray())
   {
    try
    {
     using var process=Process.GetProcessById(worker.Key);
     if(!process.HasExited&&process.StartTime.ToUniversalTime().Ticks==worker.Value)process.Kill(true);
    }
    catch(ArgumentException){}
    catch(InvalidOperationException){}
    catch(System.ComponentModel.Win32Exception){}
    Workers.TryRemove(worker.Key,out _);
   }
  }

  public static void UnregisterWorker(Process process)
  {
   try{Workers.TryRemove(process.Id,out _);}catch(InvalidOperationException){}
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct MemoryStatus
  {
   public uint Length;
   public uint MemoryLoad;
   public ulong TotalPhysical;
   public ulong AvailablePhysical;
   public ulong TotalPageFile;
   public ulong AvailablePageFile;
   public ulong TotalVirtual;
   public ulong AvailableVirtual;
   public ulong AvailableExtendedVirtual;
  }

  [DllImport("kernel32.dll",SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

  // Total physical RAM of the machine in bytes, or 0 when it cannot be read
  // (callers treat 0 as "small", the memory-safe answer).
  public static long TotalPhysicalBytes()
  {
   var status=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};
   return GlobalMemoryStatusEx(ref status)?(long)Math.Min(status.TotalPhysical,long.MaxValue):0;
  }

  // Working set of this app plus every tracked local worker and its children,
  // as numbers only. Used by Label and by the peak-memory measurement tooling.
  public static (long Bytes,int WorkerCount) Snapshot()
  {
   using var process=Process.GetCurrentProcess();
   process.Refresh();
   long appBytes=process.WorkingSet64;
   var workerCount=0;
   var descendants=DescendantProcessIds(ActiveWorkerIds());
   foreach(var id in descendants)
   {
    try
    {
     using var worker=Process.GetProcessById(id);
     if(worker.HasExited)continue;
     if(Workers.TryGetValue(id,out var startTicks)&&worker.StartTime.ToUniversalTime().Ticks!=startTicks)continue;
     worker.Refresh();appBytes+=worker.WorkingSet64;workerCount++;
    }
    catch(ArgumentException){}
    catch(InvalidOperationException){}
    catch(System.ComponentModel.Win32Exception){}
   }
   return (appBytes,workerCount);
  }

  public static string Label()
  {
   var (appBytes,workerCount)=Snapshot();
   var appGiB=appBytes/1073741824.0;
   var appLabel=workerCount==0?"App RAM":$"App + {workerCount} local worker{(workerCount==1?"":"s")} RAM";
   var status=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};
   if(!GlobalMemoryStatusEx(ref status))return $"{appLabel} {appGiB:0.0} GB";
   var usedGiB=(status.TotalPhysical-status.AvailablePhysical)/1073741824.0;
   var totalGiB=status.TotalPhysical/1073741824.0;
   return $"{appLabel} {appGiB:0.0} GB  ·  Computer RAM {usedGiB:0.0}/{totalGiB:0.0} GB";
  }

  private static int[] ActiveWorkerIds()
  {
   var active=new List<int>();
   foreach(var worker in Workers.ToArray())
   {
    try
    {
     using var process=Process.GetProcessById(worker.Key);
     if(!process.HasExited&&process.StartTime.ToUniversalTime().Ticks==worker.Value){active.Add(worker.Key);continue;}
    }
    catch(ArgumentException){}
    catch(InvalidOperationException){}
    catch(System.ComponentModel.Win32Exception){}
    Workers.TryRemove(worker.Key,out _);
   }
   return active.ToArray();
  }

  private static HashSet<int> DescendantProcessIds(int[] rootIds)
  {
   var found=new HashSet<int>(rootIds);
   if(rootIds.Length==0)return found;
   var snapshot=CreateToolhelp32Snapshot(SnapshotProcesses,0);
   if(snapshot==new IntPtr(-1))return found;
   try
   {
    var children=new Dictionary<int,List<int>>();
    var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>(),Executable=""};
    if(Process32First(snapshot,ref entry))
    {
     do
     {
      var parent=(int)entry.ParentProcessId;
      if(!children.TryGetValue(parent,out var ids)){ids=new List<int>();children[parent]=ids;}
      ids.Add((int)entry.ProcessId);
      entry.Size=(uint)Marshal.SizeOf<ProcessEntry>();
     }while(Process32Next(snapshot,ref entry));
    }
    var queue=new Queue<int>(rootIds);
    while(queue.Count>0)
    {
     var parent=queue.Dequeue();
     if(!children.TryGetValue(parent,out var ids))continue;
     foreach(var id in ids)if(found.Add(id))queue.Enqueue(id);
    }
   }
   finally{CloseHandle(snapshot);}
   return found;
  }
 }
}

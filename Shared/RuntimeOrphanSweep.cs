using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Desensitizer
{
 // At startup, ends worker processes left behind by an earlier crash. Only a
 // process that satisfies ALL of these is touched:
 //  - its executable lives in a subfolder of the app's own folder (analyzer,
 //    model-packs). The app's own executables are never swept, so a second
 //    running copy of the app is safe;
 //  - it is not this process and not a worker this run already tracks;
 //  - it is stale: its parent process no longer exists (or the parent id was
 //    reused by a newer process). A worker of another live app instance has a
 //    live parent and is left alone.
 public static class RuntimeOrphanSweep
 {
  private const uint SnapshotProcesses=0x00000002;
  private const uint QueryLimited=0x1000;

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
  [DllImport("kernel32.dll",SetLastError=true)]
  private static extern IntPtr OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)] bool inherit,uint processId);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool QueryFullProcessImageNameW(IntPtr process,uint flags,StringBuilder name,ref uint size);

  // Returns the ids of the processes that were ended.
  public static IReadOnlyList<int> Sweep(string appFolder,bool workerSubfoldersOnly=true)
  {
   var ended=new List<int>();
   var root=Path.GetFullPath(appFolder);
   if(!root.EndsWith(Path.DirectorySeparatorChar))root+=Path.DirectorySeparatorChar;
   var parents=Snapshot();
   if(parents.Count==0)return ended;
   var self=Environment.ProcessId;
   foreach(var pair in parents)
   {
    var id=pair.Key;
    if(id==self||id<=4||ResourceUsage.IsTrackedWorker(id))continue;
    var path=ImagePath(id);
    if(path==null||!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))continue;
    if(workerSubfoldersOnly&&path.IndexOf(Path.DirectorySeparatorChar,root.Length)<0)continue;
    try
    {
     using var candidate=Process.GetProcessById(id);
     if(!IsStale(candidate,pair.Value,parents))continue;
     candidate.Kill(true);
     ended.Add(id);
    }
    catch(ArgumentException){}
    catch(InvalidOperationException){}
    catch(System.ComponentModel.Win32Exception){}
   }
   return ended;
  }

  private static bool IsStale(Process candidate,int parentId,Dictionary<int,int> snapshot)
  {
   if(parentId==0||!snapshot.ContainsKey(parentId))return true;
   try
   {
    using var parent=Process.GetProcessById(parentId);
    // A parent that started after the child cannot be its parent: the id was reused.
    return parent.StartTime>candidate.StartTime;
   }
   catch(ArgumentException){return true;}
   catch(InvalidOperationException){return true;}
   catch(System.ComponentModel.Win32Exception){return false;}
  }

  private static string? ImagePath(int id)
  {
   var handle=OpenProcess(QueryLimited,false,(uint)id);
   if(handle==IntPtr.Zero)return null;
   try
   {
    var builder=new StringBuilder(1024);
    var size=(uint)builder.Capacity;
    return QueryFullProcessImageNameW(handle,0,builder,ref size)?builder.ToString():null;
   }
   finally{CloseHandle(handle);}
  }

  private static Dictionary<int,int> Snapshot()
  {
   var map=new Dictionary<int,int>();
   var snapshot=CreateToolhelp32Snapshot(SnapshotProcesses,0);
   if(snapshot==new IntPtr(-1))return map;
   try
   {
    var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>(),Executable=""};
    if(Process32First(snapshot,ref entry))
    {
     do
     {
      map[(int)entry.ProcessId]=(int)entry.ParentProcessId;
      entry.Size=(uint)Marshal.SizeOf<ProcessEntry>();
     }while(Process32Next(snapshot,ref entry));
    }
   }
   finally{CloseHandle(snapshot);}
   return map;
  }
 }
}

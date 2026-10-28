using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Desensitizer
{
 // A Windows Job Object with KILL_ON_JOB_CLOSE. Every worker the app starts
 // (llama-server, the Python analyzers) is assigned to one process-wide job, so
 // when the app crashes, is killed from Task Manager or exits without cleanup,
 // Windows closes the job handle and ends the workers with it.
 //
 // Limitation: .NET cannot start a process suspended, so a worker is assigned a
 // few milliseconds after it starts. A kill of the app inside that window can
 // still leave one worker; the startup orphan sweep (RuntimeOrphanSweep) covers it.
 public sealed class RuntimeJob : IDisposable
 {
  private const int ExtendedLimitInformation=9;
  private const uint KillOnJobClose=0x2000;
  private static readonly Lazy<RuntimeJob> SharedJob=new Lazy<RuntimeJob>(()=>new RuntimeJob());
  private IntPtr handle;

  [StructLayout(LayoutKind.Sequential)]
  private struct BasicLimits
  {
   public long PerProcessUserTimeLimit;
   public long PerJobUserTimeLimit;
   public uint LimitFlags;
   public UIntPtr MinimumWorkingSetSize;
   public UIntPtr MaximumWorkingSetSize;
   public uint ActiveProcessLimit;
   public UIntPtr Affinity;
   public uint PriorityClass;
   public uint SchedulingClass;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct IoCounters
  {
   public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct ExtendedLimits
  {
   public BasicLimits Basic;
   public IoCounters Io;
   public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;
  }

  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  private static extern IntPtr CreateJobObjectW(IntPtr attributes,string? name);
  [DllImport("kernel32.dll",SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetInformationJobObject(IntPtr job,int informationClass,ref ExtendedLimits information,int length);
  [DllImport("kernel32.dll",SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
  [DllImport("kernel32.dll",SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CloseHandle(IntPtr handle);

  // The process-wide job used by ResourceUsage.RegisterWorker.
  public static RuntimeJob Shared=>SharedJob.Value;

  public RuntimeJob()
  {
   handle=CreateJobObjectW(IntPtr.Zero,null);
   if(handle==IntPtr.Zero)return;
   var limits=new ExtendedLimits();
   limits.Basic.LimitFlags=KillOnJobClose;
   if(!SetInformationJobObject(handle,ExtendedLimitInformation,ref limits,Marshal.SizeOf<ExtendedLimits>()))
   {
    CloseHandle(handle);handle=IntPtr.Zero;
   }
  }

  public bool IsActive=>handle!=IntPtr.Zero;

  // True when the process now belongs to the job. False (never an exception)
  // when the job could not be created or the process already exited.
  public bool TryAssign(Process process)
  {
   if(handle==IntPtr.Zero)return false;
   try{return AssignProcessToJobObject(handle,process.Handle);}
   catch(InvalidOperationException){return false;}
   catch(System.ComponentModel.Win32Exception){return false;}
  }

  // Closing the handle ends every process still in the job.
  public void Dispose()
  {
   var current=handle;handle=IntPtr.Zero;
   if(current!=IntPtr.Zero)CloseHandle(current);
  }
 }
}

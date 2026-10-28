using System;
using System.Threading.Tasks;

namespace Desensitizer
{
 // Remembers the project write that runs off the interface thread, so closing the window (or leaving the project)
 // can wait for it instead of exiting while the file is still being written.
 public sealed class UiSaveTracker
 {
  private readonly object _gate=new object();
  private Task? _pending;

  public Task Track(Task write){lock(_gate)_pending=write;return write;}

  public bool IsPending{get{lock(_gate)return _pending!=null&&!_pending.IsCompleted;}}

  // Blocks until the tracked write ends or the time is up. Returns true when nothing is left running. failed is true when
  // the write ended with an error, so the caller marks the project as changed again and writes it itself.
  public bool WaitForPending(TimeSpan timeout,out bool failed)
  {
   Task? task;lock(_gate)task=_pending;
   failed=false;
   if(task==null)return true;
   try{if(!task.Wait(timeout))return false;}
   catch(AggregateException){failed=true;return true;}
   failed=task.IsFaulted||task.IsCanceled;
   lock(_gate){if(ReferenceEquals(_pending,task))_pending=null;}
   return true;
  }
 }
}

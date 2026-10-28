using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Desensitizer
{
 public sealed class EngineOutcome<T> where T:class
 {
  public int Index {get;init;}
  public string Name {get;init;}="";
  // True when the engine was not installed or otherwise not run.
  public bool Skipped {get;init;}
  public T? Result {get;init;}
  public Exception? Error {get;init;}
  public bool Succeeded=>!Skipped&&Error==null&&Result!=null;
 }

 // Runs independent analyzer engines with at most maxConcurrency at a time and
 // returns the outcomes in the original engine order, so the merged output is
 // identical to a sequential run. An engine failure is recorded, never thrown
 // (the caller marks the review incomplete, as before). Cancellation stops
 // starting new engines, cancels the token every running engine received (the
 // worker host kills the process on it), waits for all of them, then throws
 // OperationCanceledException.
 public static class PerfEngineRunner
 {
  public static IReadOnlyList<EngineOutcome<T>> RunAll<T>(IReadOnlyList<string> names,Func<int,bool> isAvailable,Func<int,CancellationToken,T> run,int maxConcurrency,CancellationToken cancellationToken,Action<int>? starting=null,Action<EngineOutcome<T>>? completed=null) where T:class
  {
   cancellationToken.ThrowIfCancellationRequested();
   var outcomes=new EngineOutcome<T>?[names.Count];
   var gate=new object();
   using var limit=new SemaphoreSlim(Math.Max(1,maxConcurrency));
   using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
   var tasks=new List<Task>();
   for(var i=0;i<names.Count;i++)
   {
    var index=i;
    if(!isAvailable(index))
    {
     var skipped=new EngineOutcome<T>{Index=index,Name=names[index],Skipped=true};
     outcomes[index]=skipped;
     lock(gate){completed?.Invoke(skipped);}
     continue;
    }
    try{limit.Wait(linked.Token);}
    catch(OperationCanceledException){break;}
    if(linked.IsCancellationRequested){limit.Release();break;}
    tasks.Add(Task.Factory.StartNew(()=>
    {
     try
     {
      lock(gate){if(linked.IsCancellationRequested)return;starting?.Invoke(index);}
      EngineOutcome<T> outcome;
      try{outcome=new EngineOutcome<T>{Index=index,Name=names[index],Result=run(index,linked.Token)};}
      catch(OperationCanceledException){return;}
      catch(Exception ex)
      {
       if(linked.IsCancellationRequested)return;
       outcome=new EngineOutcome<T>{Index=index,Name=names[index],Error=ex};
      }
      outcomes[index]=outcome;
      lock(gate){completed?.Invoke(outcome);}
     }
     finally{limit.Release();}
    },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default));
   }
   Task.WaitAll(tasks.ToArray());
   cancellationToken.ThrowIfCancellationRequested();
   var list=new List<EngineOutcome<T>>(names.Count);
   for(var i=0;i<names.Count;i++)list.Add(outcomes[i]??new EngineOutcome<T>{Index=i,Name=names[i],Error=new InvalidOperationException("The engine did not run.")});
   return list;
  }
 }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Desensitizer
{
 // Shared policy for a fatal, unexpected error. It never reads Exception.Message,
 // Exception.Data, the stack trace or any document text: the message shown to the
 // user is fixed, plus the exception type name (a code, never content). Nothing
 // is written to a log. The caller (App.xaml.cs) wires the .NET and WPF events.
 public sealed class RuntimeCrashGuard
 {
  public const string Message="The app hit an unexpected problem and has to close. Nothing was sent anywhere, and your original files were not changed. Open the app again to continue.";
  private readonly Action<string> show;
  private readonly Action<int> exit;
  private readonly Action stopWorkers;
  private int handling;

  public RuntimeCrashGuard(Action<string> show,Action<int> exit,Action? stopWorkers=null)
  {
   this.show=show;this.exit=exit;this.stopWorkers=stopWorkers??ResourceUsage.StopAllWorkers;
  }

  private Action? flush;
  public TimeSpan FlushTimeout{get;set;}=TimeSpan.FromSeconds(4);

  // A best-effort action that saves the open project before the app exits (UI-5). It runs on a worker thread with a
  // short time limit and can never throw into the handler.
  public void RegisterFlush(Action? action)=>flush=action;

  private void TryFlush()
  {
   var action=flush;
   if(action==null)return;
   try
   {
    var task=Task.Run(()=>{try{action();}catch{}});
    task.Wait(FlushTimeout);
   }
   catch{}
  }

  // A task fault nobody awaited is not a reason to close the app: it is observed and dropped. Nothing is logged.
  public void HandleUnobserved(Action setObserved){try{setObserved();}catch{}}

  // Safe text for an exception: its type name only.
  public static string Describe(Exception? exception)
  {
   if(exception==null)return "Unknown error";
   var inner=exception is AggregateException aggregate?aggregate.GetBaseException():exception;
   return inner.GetType().Name;
  }

  // Stops every worker, shows the scrubbed message once, then exits. Re-entrant
  // calls (a second failure while the first is handled) only stop workers.
  public void HandleFatal(Exception? exception)
  {
   try{stopWorkers();}catch{}
   if(Interlocked.Exchange(ref handling,1)!=0)return;
   TryFlush();
   try{show(Message+"\n\nError code: "+Describe(exception));}catch{}
   try{exit(1);}catch{}
  }
 }
}

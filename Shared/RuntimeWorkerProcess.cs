using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Desensitizer
{
 public sealed class WorkerOutput
 {
  public int ExitCode {get;init;}
  public string Output {get;init;}="";
  // Capped. Worker stderr can echo input fragments, so callers must never show it to the user.
  public string Error {get;init;}="";
 }

 // Runs one JSON-over-stdin worker. The request is written while stdout and
 // stderr are already being read, so a large document cannot deadlock against a
 // worker that starts answering (or logging) before it has read all of stdin.
 // The worker is tracked by ResourceUsage (and so by the job object) and is
 // killed on timeout or cancellation.
 public static class RuntimeWorkerProcess
 {
  // A single response may contain findings for a whole document batch. Sixteen
  // million UTF-16 characters allows substantial JSON headroom while bounding
  // retained stdout to 32 MiB of text. Excess output is an error, never truncation.
  public const int MaxOutputCharacters=16*1024*1024;
  private const int MaxErrorCharacters=65536;
  private const int MaxMessageCharacters=160;
  private const string OutputLimitMessage="The analyzer response exceeded the allowed size. The check is incomplete.";

  public static WorkerOutput Run(ProcessStartInfo info,string stdin,int timeoutMilliseconds,CancellationToken cancellationToken)
  {
   cancellationToken.ThrowIfCancellationRequested();
   info.UseShellExecute=false;info.CreateNoWindow=true;
   info.RedirectStandardInput=true;info.RedirectStandardOutput=true;info.RedirectStandardError=true;
   // JSON may contain either literal Unicode or escaped characters. The worker
   // scripts use the same explicit UTF-8 protocol, independent of Windows' code page.
   var encoding=new UTF8Encoding(false);
   info.StandardInputEncoding=encoding;info.StandardOutputEncoding=encoding;info.StandardErrorEncoding=encoding;
   using var process=new Process{StartInfo=info};
   process.Start();
   ResourceUsage.RegisterWorker(process);
   Task<string>? outputTask=null;Task<string>? errorTask=null;Task? inputTask=null;
   var outputExceeded=0;
   try
   {
    using var cancellation=cancellationToken.Register(()=>Kill(process));
    outputTask=ReadOutputAsync(process.StandardOutput,()=>
    {
     Interlocked.Exchange(ref outputExceeded,1);
     Kill(process);
    });
    errorTask=ReadCappedAsync(process.StandardError,MaxErrorCharacters);
    inputTask=Task.Run(()=>
    {
     try
     {
      process.StandardInput.Write(stdin);
      process.StandardInput.Close();
     }
     catch(IOException){}
     catch(ObjectDisposedException){}
    });
    if(!process.WaitForExit(timeoutMilliseconds))
    {
     Kill(process);
     throw new TimeoutException("The analyzer timed out.");
    }
    cancellationToken.ThrowIfCancellationRequested();
    if(Volatile.Read(ref outputExceeded)!=0)throw new InvalidDataException(OutputLimitMessage);
    // A killed worker closes its pipes, so these finish promptly. The bound only
    // protects against a grandchild that inherited a pipe handle.
    if(!Task.WaitAll(new Task[]{outputTask,errorTask,inputTask},TimeSpan.FromSeconds(15)))
     throw new InvalidOperationException("The analyzer output could not be read completely.");
    cancellationToken.ThrowIfCancellationRequested();
    if(Volatile.Read(ref outputExceeded)!=0)throw new InvalidDataException(OutputLimitMessage);
    return new WorkerOutput
    {
     ExitCode=process.ExitCode,
     Output=outputTask.Result,
     Error=errorTask.IsCompletedSuccessfully?errorTask.Result:""
    };
   }
   catch(AggregateException)
   {
    cancellationToken.ThrowIfCancellationRequested();
    if(Volatile.Read(ref outputExceeded)!=0)throw new InvalidDataException(OutputLimitMessage);
    throw new InvalidOperationException("The analyzer output could not be read.");
   }
   finally
   {
    Kill(process);
    // A timeout or cancellation leaves the readers running: whatever they end with is observed, so a fault in one
    // of them is never left unobserved (UI-5).
    Observe(outputTask);Observe(errorTask);Observe(inputTask);
    ResourceUsage.UnregisterWorker(process);
   }
  }

  // Marks a task's fault as observed without waiting for it.
  public static void Observe(Task? task)
  {
   if(task==null)return;
   task.ContinueWith(finished=>{_=finished.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
  }

  // A message that is safe to show: one short line, no control characters, never stderr.
  public static string SafeMessage(string? workerError,string fallback)
  {
   if(string.IsNullOrWhiteSpace(workerError))return fallback;
   var builder=new StringBuilder();
   foreach(var character in workerError)
   {
    if(builder.Length>=MaxMessageCharacters)break;
    builder.Append(char.IsControl(character)?' ':character);
   }
   return builder.ToString().Trim();
  }

  private static void Kill(Process process)
  {
   try{if(!process.HasExited)process.Kill(true);}
   catch(InvalidOperationException){}
   catch(System.ComponentModel.Win32Exception){}
  }

  private static async Task<string> ReadCappedAsync(StreamReader reader,int limit)
  {
   var builder=new StringBuilder();
   var buffer=new char[4096];
   try
   {
    int read;
    while((read=await reader.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false))>0)
    {
     if(builder.Length<limit)builder.Append(buffer,0,Math.Min(read,limit-builder.Length));
    }
   }
   catch(IOException){}
   catch(ObjectDisposedException){}
   return builder.ToString();
  }

  private static async Task<string> ReadOutputAsync(StreamReader reader,Action exceeded)
  {
   var builder=new StringBuilder();
   var buffer=new char[4096];
   int read;
   while((read=await reader.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false))>0)
   {
    if(read>MaxOutputCharacters-builder.Length)
    {
     exceeded();
     throw new InvalidDataException(OutputLimitMessage);
    }
    builder.Append(buffer,0,read);
   }
   return builder.ToString();
  }
 }
}

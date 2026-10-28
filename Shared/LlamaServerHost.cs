using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace Desensitizer
{
 // One bundled llama-server for one model. It listens only on loopback behind
 // a random per-start key, is tracked by ResourceUsage, and is always killed on
 // cancellation, Stop or Dispose. It may serve several review passes of one
 // operation so the model is not reloaded for every pass.
 public sealed class LlamaServerHost : IDisposable
 {
  public const string GpuLayersVariable="DESENSITIZER_LLM_NGL";
  private const int MaxUnexpectedExits=3;
  private readonly object gate=new object();
  private readonly string runtime;
  private readonly string modelPath;
  private readonly Func<int,string,ProcessStartInfo>? startInfoFactory;
  private readonly Action<HttpClient,Process,CancellationToken>? readiness;
  private readonly RuntimeIntegrity? integrity;
  private readonly Func<int,int,PortOwnership>? ownership;
  private int port;
  private volatile Process? process;
  private HttpClient? client;
  private System.Threading.Tasks.Task? stdout,stderr;
  private string? startFailure;
  private int unexpectedExits;
  private volatile bool killedByCancel;
  private bool disposed;

  // The two optional delegates are test seams. Production callers use the defaults.
  // integrity and ownership are test seams too. In production (no startInfoFactory)
  // the integrity manifest beside the app is enforced, and the port owner is read
  // from the OS TCP table before any request is sent.
  public LlamaServerHost(string runtime,string modelPath,Func<int,string,ProcessStartInfo>? startInfoFactory=null,Action<HttpClient,Process,CancellationToken>? readiness=null,RuntimeIntegrity? integrity=null,Func<int,int,PortOwnership>? ownership=null)
  {
   this.runtime=runtime;this.modelPath=modelPath;this.startInfoFactory=startInfoFactory;this.readiness=readiness;
   this.integrity=integrity??(startInfoFactory==null?RuntimeIntegrity.Default:null);
   this.ownership=ownership??(readiness==null?RuntimePortOwner.Check:null);
  }

  // The environment variable llama-server reads its API key from (verified in
  // the bundled build: "--api-key KEY ... (env: LLAMA_API_KEY)").
  public const string ApiKeyVariable="LLAMA_API_KEY";

  public int StartCount {get;private set;}
  public string ModelPath=>modelPath;
  public string RuntimePath=>runtime;
  public bool IsRunning{get{lock(gate){return process!=null&&!HasExited(process);}}}
  public int ProcessId{get{lock(gate){return process==null?0:process.Id;}}}

  // DESENSITIZER_LLM_NGL: number of model layers to offload to the GPU. Unset or
  // invalid keeps the CPU-only default.
  public static int? ParseGpuLayers(string? value)
  {
   if(string.IsNullOrWhiteSpace(value))return null;
   return int.TryParse(value.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var layers)&&layers<=999?layers:null;
  }

  // Arguments the app really starts the server with. The API key is NOT among
  // them: it is passed through the LLAMA_API_KEY environment variable of the
  // child only, so it does not appear in the command line that other processes
  // of the same user can read (Task Manager, WMI, process listings).
  // Residual risk: a process of the same user with enough rights can still read
  // the child's environment block. The key is random per start, bound to
  // 127.0.0.1, and never printed or logged.
  public static IReadOnlyList<string> BuildServerArguments(string modelPath,int port,int? gpuLayers,int? threads=null)
  {
   var arguments=new List<string>{"-m",modelPath,"--host","127.0.0.1","--port",port.ToString(CultureInfo.InvariantCulture),"--ctx-size","4096","--parallel","1","--reasoning","off","--no-webui","--cors-origins","http://127.0.0.1"};
   if(gpuLayers.HasValue){arguments.Add("-ngl");arguments.Add(gpuLayers.Value.ToString(CultureInfo.InvariantCulture));}
   // Appended after every existing argument. null keeps the legacy list (tests, BuildArguments).
   if(threads.HasValue&&threads.Value>0){arguments.Add("--threads");arguments.Add(threads.Value.ToString(CultureInfo.InvariantCulture));}
   return arguments;
  }

  // Threads the real server gets: the Threads setting, or logical cores minus 2 (at least 2).
  // DESENSITIZER_LLM_THREADS overrides both, only when DESENSITIZER_DEV=1.
  public static int DefaultThreads()=>PerfPolicy.ResolveLlamaThreads(Environment.ProcessorCount,PerfConfig.LlamaThreads,Environment.GetEnvironmentVariable);

  // The real start info: arguments without the key, and the key in the child's environment only.
  public static ProcessStartInfo BuildStartInfo(string runtime,string modelPath,int port,string key,int? gpuLayers,int? threads=null)
  {
   var info=new ProcessStartInfo(runtime){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
   foreach(var argument in BuildServerArguments(modelPath,port,gpuLayers,threads))info.ArgumentList.Add(argument);
   info.Environment[ApiKeyVariable]=key;
   return info;
  }

  // Legacy form that puts the key on the command line. Kept only so older
  // callers and tests keep compiling; Start does not use it.
  public static IReadOnlyList<string> BuildArguments(string modelPath,int port,string key,int? gpuLayers)
  {
   var arguments=BuildServerArguments(modelPath,port,null).ToList();
   arguments.Add("--api-key");arguments.Add(key);
   if(gpuLayers.HasValue){arguments.Add("-ngl");arguments.Add(gpuLayers.Value.ToString(CultureInfo.InvariantCulture));}
   return arguments;
  }

  // Returns a client for a running, ready server. Starts or restarts it when needed.
  public HttpClient EnsureReady(CancellationToken token)
  {
   lock(gate)
   {
    ObjectDisposedException.ThrowIf(disposed,this);
    token.ThrowIfCancellationRequested();
    if(startFailure!=null)throw new InvalidOperationException(startFailure);
    if(process!=null&&client!=null&&!HasExited(process))
    {
     // Re-confirm before every use that the listener is still our child.
     if(ownership==null||ownership(port,process.Id)==PortOwnership.Child)return client;
     StopProcess();
     startFailure=ForeignListenerMessage;
     throw new InvalidOperationException(startFailure);
    }
    if(process!=null)
    {
     var cancelled=killedByCancel;StopProcess();
     if(!cancelled&&++unexpectedExits>MaxUnexpectedExits)
     {
      startFailure="The bundled local AI stopped repeatedly and was not restarted again.";
      throw new InvalidOperationException(startFailure);
     }
    }
    Start(token);
    return client!;
   }
  }

  public IDisposable KillOnCancel(CancellationToken token)=>token.Register(()=>{killedByCancel=true;Kill();});

  public void Stop(){lock(gate){StopProcess();}}

  public void Dispose()
  {
   lock(gate){disposed=true;StopProcess();}
  }

  private void Start(CancellationToken token)
  {
   killedByCancel=false;
   port=FreeLoopbackPort();
   var key=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
   var started=new Process();
   try
   {
    if(startInfoFactory==null)
    {
     RequireInsideApp(runtime);RequireInsideApp(modelPath);
     if(integrity!=null){integrity.RequireWithRuntimeFiles(runtime);integrity.Require(modelPath);}
    }
    else integrity?.Require(runtime);
    var info=startInfoFactory!=null?startInfoFactory(port,key):BuildStartInfo(runtime,modelPath,port,key,ParseGpuLayers(Environment.GetEnvironmentVariable(GpuLayersVariable)),DefaultThreads());
    started.StartInfo=info;
    started.Start();StartCount++;
    process=started;ResourceUsage.RegisterWorker(started);
    if(info.RedirectStandardOutput)stdout=DrainOutputAsync(started.StandardOutput);
    if(info.RedirectStandardError)stderr=DrainOutputAsync(started.StandardError);
    client=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:"+port),Timeout=TimeSpan.FromMinutes(10)};
    client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);
    if(readiness!=null)readiness(client,started,token);else WaitUntilReady(client,started,token);
   }
   catch(Exception ex)
   {
    StopProcess();
    // A failed start is remembered so the remaining sections fail fast instead
    // of waiting for the readiness timeout again.
    if(ex is not OperationCanceledException)startFailure=ex is InvalidOperationException or TimeoutException?ex.Message:"The bundled local AI could not start.";
    throw;
   }
  }

  private void StopProcess()
  {
   var current=process;var http=client;
   process=null;client=null;
   try{http?.Dispose();}catch{}
   if(current==null)return;
   try{if(!current.HasExited)current.Kill(true);}catch{}
   try{current.WaitForExit(5000);}catch{}
   ResourceUsage.UnregisterWorker(current);
   _=stdout;_=stderr;
   try{current.Dispose();}catch{}
  }

  // Keep pipes drained so inference cannot block, without retaining every log
  // line from a long document in the app's memory. Output is not used as data.
  private static async System.Threading.Tasks.Task DrainOutputAsync(System.IO.StreamReader reader)
  {
   var buffer=new char[4096];
   try{while(await reader.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false)>0){}}
   catch(System.IO.IOException){}
   catch(ObjectDisposedException){}
  }

  private void Kill()
  {
   var current=process;
   try{if(current!=null&&!current.HasExited)current.Kill(true);}catch{}
  }

  private static bool HasExited(Process value){try{return value.HasExited;}catch(InvalidOperationException){return true;}}

  private const string ForeignListenerMessage="Another program is using the local AI port, so the review was stopped before any document text was sent. Close other programs and try again.";

  // Paths are checked, not trusted: a model or runtime outside the app folder is
  // refused unless the development flag (DESENSITIZER_DEV=1) is set.
  private static void RequireInsideApp(string path)
  {
   if(RuntimeOverrides.IsDevelopment)return;
   var root=Path.GetFullPath(AppContext.BaseDirectory);
   if(!root.EndsWith(Path.DirectorySeparatorChar))root+=Path.DirectorySeparatorChar;
   if(!Path.GetFullPath(path).StartsWith(root,StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The local AI program or model is outside the app folder, so it was not started.");
  }

  // Document text is only ever sent to a server after the OS reports that the
  // listener on the port is the child process this host started. Until then the
  // only thing that happens is polling the TCP table; no request leaves.
  private void WaitUntilReady(HttpClient client,Process process,CancellationToken token)
  {
   var deadline=DateTime.UtcNow.AddSeconds(90);
   while(DateTime.UtcNow<deadline)
   {
    token.ThrowIfCancellationRequested();
    if(process.HasExited)throw new InvalidOperationException("The bundled local AI could not start.");
    var owner=ownership==null?PortOwnership.Child:ownership(port,process.Id);
    if(owner==PortOwnership.Other)throw new InvalidOperationException(ForeignListenerMessage);
    if(owner==PortOwnership.Child)
    {
     try
     {
      using var response=client.GetAsync("/health",token).GetAwaiter().GetResult();
      if(response.IsSuccessStatusCode)
      {
       // Confirm again after the answer: the answerer must be our child.
       if(ownership==null||ownership(port,process.Id)==PortOwnership.Child)return;
       throw new InvalidOperationException(ForeignListenerMessage);
      }
     }
     catch(HttpRequestException){}
    }
    Thread.Sleep(250);
   }
   throw new TimeoutException("The bundled local AI did not become ready in time.");
  }

  private static int FreeLoopbackPort()
  {
   var listener=new TcpListener(IPAddress.Loopback,0);
   listener.Start();
   try{return ((IPEndPoint)listener.LocalEndpoint).Port;}
   finally{listener.Stop();}
  }
 }

 // Keeps recently used servers alive for one operation. The number kept
 // resident comes from the MaxResidentModels setting or total RAM (see
 // PerfPolicy.DecideMaxResident); DESENSITIZER_LLM_RESIDENT (1 to 3) overrides it.
 public sealed class LlamaServerPool : IDisposable
 {
  // The app can use at most this many different models in one operation.
  public const int MaxModels=3;
  private readonly Dictionary<string,LlamaServerHost> hosts=new Dictionary<string,LlamaServerHost>(StringComparer.Ordinal);
  private readonly List<string> recent=new List<string>();
  private readonly Func<string,string,string,LlamaServerHost>? factory;
  private bool disposed;

  public LlamaServerPool(int? maxResident=null,Func<string,string,string,LlamaServerHost>? factory=null)
  {
   // Order: explicit argument, DESENSITIZER_LLM_RESIDENT, the saved setting, then
   // a RAM-based default (1 on 16 GB or less, 2 from 24 GB).
   var configured=maxResident;
   if(configured==null&&int.TryParse(Environment.GetEnvironmentVariable("DESENSITIZER_LLM_RESIDENT"),out var fromEnvironment))configured=fromEnvironment;
   if(configured==null)configured=PerfPolicy.DecideMaxResident(ResourceUsage.TotalPhysicalBytes(),MaxModels,PerfConfig.MaxResidentModels);
   MaxResident=Math.Clamp(configured.Value,1,3);
   this.factory=factory;
  }

  public int MaxResident {get;}
  public IReadOnlyList<string> RunningKeys{get{lock(hosts){return hosts.Where(item=>item.Value.IsRunning).Select(item=>item.Key).ToList();}}}
  // Looks a server up without changing which servers are running.
  public LlamaServerHost? Get(string key){lock(hosts){return hosts.TryGetValue(key,out var host)?host:null;}}
  public bool IsRunning(string key){lock(hosts){return hosts.TryGetValue(key,out var host)&&host.IsRunning;}}

  public LlamaServerHost Acquire(string key,string runtime,string modelPath)
  {
   lock(hosts)
   {
    ObjectDisposedException.ThrowIf(disposed,this);
    if(!hosts.TryGetValue(key,out var host)||host.ModelPath!=modelPath||host.RuntimePath!=runtime)
    {
     host?.Dispose();
     host=factory!=null?factory(key,runtime,modelPath):new LlamaServerHost(runtime,modelPath);
     hosts[key]=host;
    }
    recent.Remove(key);recent.Add(key);
    // Stop the least recently used other servers so that, with this one, at
    // most MaxResident are running.
    foreach(var other in recent.Where(name=>name!=key).ToList())
    {
     if(hosts.Count(item=>item.Key!=key&&item.Value.IsRunning)<MaxResident)break;
     if(hosts.TryGetValue(other,out var candidate)&&candidate.IsRunning)candidate.Stop();
    }
    return host;
   }
  }

  public void StopAll(){lock(hosts){foreach(var host in hosts.Values)host.Stop();}}

  public void Dispose()
  {
   lock(hosts){disposed=true;foreach(var host in hosts.Values)host.Dispose();hosts.Clear();recent.Clear();}
  }
 }
}

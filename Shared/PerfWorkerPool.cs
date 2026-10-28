using System;
using System.Collections.Generic;

namespace Desensitizer
{
 // DESIGN NOTE (P5). The three Python analyzers (analyzer.py,
 // business_context_analyzer.py, privacy_filter_analyzer.py) each read ONE JSON
 // request with json.load(sys.stdin), which only returns at end of input, answer
 // and exit. They cannot serve a second request, so keeping them resident needs
 // a protocol change (for example one JSON object per line, answered by one line,
 // with an explicit shutdown message) in all three scripts plus the PyInstaller
 // rebuilds, and RuntimeWorkerProcess would need a persistent-session variant.
 // Until that is done this pool is NOT wired into PresidioDetector: it is the
 // tested host-side abstraction that wiring would use. Rules it enforces:
 //  - one worker per key, leased exclusively (never two requests at once);
 //  - a worker idle for longer than the timeout (default 5 minutes) is disposed
 //    by SweepIdle, and a worker that reports it is dead is replaced;
 //  - Dispose, and DisposeAll on cancellation, always dispose every worker (the
 //    worker's Dispose must kill the process and unregister it from
 //    ResourceUsage, so the job object and orphan sweep keep working).
 public sealed class PerfWorkerPool<TWorker> : IDisposable where TWorker:class,IDisposable
 {
  private sealed class Entry
  {
   public TWorker Worker=null!;
   public DateTime LastUsedUtc;
   public bool Leased;
  }

  private readonly object gate=new object();
  private readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>(StringComparer.Ordinal);
  private readonly Func<DateTime> clock;
  private readonly Func<TWorker,bool> isAlive;
  private bool disposed;

  public TimeSpan IdleTimeout {get;}
  public int CreatedCount {get;private set;}

  public PerfWorkerPool(TimeSpan? idleTimeout=null,Func<DateTime>? clock=null,Func<TWorker,bool>? isAlive=null)
  {
   IdleTimeout=idleTimeout??TimeSpan.FromMinutes(PerfConfig.WorkerIdleMinutes);
   this.clock=clock??(()=>DateTime.UtcNow);
   this.isAlive=isAlive??(_=>true);
  }

  public int ResidentCount{get{lock(gate){return entries.Count;}}}

  // Returns the resident worker for the key (creating or replacing it when
  // missing or dead) and marks it leased. Call Release when the request ends.
  public TWorker Lease(string key,Func<TWorker> create)
  {
   lock(gate)
   {
    ObjectDisposedException.ThrowIf(disposed,this);
    if(entries.TryGetValue(key,out var entry))
    {
     if(entry.Leased)throw new InvalidOperationException("The worker is already in use.");
     if(isAlive(entry.Worker)){entry.Leased=true;return entry.Worker;}
     Discard(key,entry);
    }
    var worker=create();
    CreatedCount++;
    entries[key]=new Entry{Worker=worker,LastUsedUtc=clock(),Leased=true};
    return worker;
   }
  }

  // failed=true disposes the worker (a worker that errored is never reused).
  public void Release(string key,bool failed=false)
  {
   lock(gate)
   {
    if(!entries.TryGetValue(key,out var entry))return;
    if(failed||disposed){Discard(key,entry);return;}
    entry.Leased=false;entry.LastUsedUtc=clock();
   }
  }

  // Disposes every unleased worker idle for longer than the timeout. Returns how many.
  public int SweepIdle()
  {
   lock(gate)
   {
    var now=clock();var removed=0;
    foreach(var pair in new List<KeyValuePair<string,Entry>>(entries))
    {
     if(pair.Value.Leased||now-pair.Value.LastUsedUtc<=IdleTimeout)continue;
     Discard(pair.Key,pair.Value);removed++;
    }
    return removed;
   }
  }

  public void DisposeAll()
  {
   lock(gate){foreach(var pair in new List<KeyValuePair<string,Entry>>(entries))Discard(pair.Key,pair.Value);}
  }

  public void Dispose()
  {
   lock(gate){disposed=true;}
   DisposeAll();
  }

  private void Discard(string key,Entry entry)
  {
   entries.Remove(key);
   try{entry.Worker.Dispose();}catch{}
  }
 }
}

// Deterministic performance-helper tests. Fakes only: no real model, no llama-server,
// no analyzer executables. Run: dotnet run --project tests/Perf/Perf.csproj
using Desensitizer;

internal static class Program
{
    private static int passed, failed;

    private static void Check(string name, bool ok)
    {
        if (ok) { passed++; Console.WriteLine("PASS " + name); }
        else { failed++; Console.WriteLine("FAIL: " + name); }
    }

    private static int Main()
    {
        ThreadTests();
        ArgumentTests();
        ResidentTests();
        RunnerTests();
        WorkerPoolTests();
        EstimateTests();
        MemoryLogTests();
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void ThreadTests()
    {
        Check("Auto threads are cores minus 2", PerfPolicy.DecideLlamaThreads(8) == 6 && PerfPolicy.DecideLlamaThreads(4) == 2);
        Check("Auto threads never go below 2", PerfPolicy.DecideLlamaThreads(2) == 2 && PerfPolicy.DecideLlamaThreads(1) == 2 && PerfPolicy.DecideLlamaThreads(0) == 2);
        Check("A configured thread count is clamped to 1..cores", PerfPolicy.DecideLlamaThreads(8, 3) == 3 && PerfPolicy.DecideLlamaThreads(8, 64) == 8 && PerfPolicy.DecideLlamaThreads(8, -5) == 6);
        Func<string, string?> dev = n => n == "DESENSITIZER_DEV" ? "1" : n == PerfPolicy.ThreadsVariable ? "5" : null;
        Func<string, string?> notDev = n => n == PerfPolicy.ThreadsVariable ? "5" : null;
        Check("The thread environment override is honoured only under the dev flag", PerfPolicy.ResolveLlamaThreads(8, 0, dev) == 5 && PerfPolicy.ResolveLlamaThreads(8, 0, notDev) == 6);
        Func<string, string?> junk = n => n == "DESENSITIZER_DEV" ? "1" : n == PerfPolicy.ThreadsVariable ? "abc" : null;
        Check("An invalid thread override is ignored", PerfPolicy.ResolveLlamaThreads(8, 4, junk) == 4);
        Check("The setting is used when there is no override", PerfPolicy.ResolveLlamaThreads(8, 4, notDev) == 4);
    }

    private static void ArgumentTests()
    {
        var legacy = LlamaServerHost.BuildServerArguments("m.gguf", 1234, null);
        var withThreads = LlamaServerHost.BuildServerArguments("m.gguf", 1234, 20, 6);
        Check("Without threads the argument list is the legacy list (no --threads)", !legacy.Contains("--threads") && legacy.SequenceEqual(new[] { "-m", "m.gguf", "--host", "127.0.0.1", "--port", "1234", "--ctx-size", "4096", "--parallel", "1", "--reasoning", "off", "--no-webui", "--cors-origins", "http://127.0.0.1" }));
        Check("Threads are appended and every existing argument is kept", withThreads[^2] == "--threads" && withThreads[^1] == "6" && legacy.All(withThreads.Contains) && withThreads.Contains("-ngl") && withThreads.Contains("4096") && withThreads.Contains("--parallel"));
        Check("No risky flags are added", !withThreads.Contains("--mlock") && !withThreads.Contains("--flash-attn") && !withThreads.Contains("-fa"));
        var info = LlamaServerHost.BuildStartInfo("llama-server.exe", "m.gguf", 1234, "KEY", null, 4);
        var list = info.ArgumentList.ToList();
        Check("The real start info carries --threads and still no key on the command line", list.Contains("--threads") && list[list.IndexOf("--threads") + 1] == "4" && !list.Any(a => a.Contains("KEY")));
        Check("The legacy BuildArguments form is unchanged", !LlamaServerHost.BuildArguments("m.gguf", 1234, "key", null).Contains("--threads"));
        Check("The default thread count is at least 2", LlamaServerHost.DefaultThreads() >= 2);
    }

    private static void ResidentTests()
    {
        const long G = PerfPolicy.GiB;
        Check("One resident model on 8 and 16 GB", PerfPolicy.DecideMaxResident(8 * G, 3) == 1 && PerfPolicy.DecideMaxResident(16 * G, 3) == 1);
        Check("Two resident models from 24 GB", PerfPolicy.DecideMaxResident(24 * G, 3) == 2 && PerfPolicy.DecideMaxResident(64 * G, 3) == 2);
        Check("Unknown RAM is treated as small", PerfPolicy.DecideMaxResident(0, 3) == 1 && PerfPolicy.DecideMaxResident(-1, 3) == 1);
        Check("Never more than the model count", PerfPolicy.DecideMaxResident(64 * G, 1) == 1 && PerfPolicy.DecideMaxResident(64 * G, 0) == 1);
        Check("A configured value is clamped to 1..3 and to the model count", PerfPolicy.DecideMaxResident(8 * G, 3, 3) == 3 && PerfPolicy.DecideMaxResident(8 * G, 3, 9) == 3 && PerfPolicy.DecideMaxResident(8 * G, 2, 3) == 2);
        Check("Detector concurrency: 2 on 16 GB, 1 on a small machine, clamped", PerfPolicy.DecideDetectorConcurrency(16 * G, 3) == 2 && PerfPolicy.DecideDetectorConcurrency(8 * G, 3) == 1 && PerfPolicy.DecideDetectorConcurrency(0, 3) == 1 && PerfPolicy.DecideDetectorConcurrency(16 * G, 3, 9) == 3 && PerfPolicy.DecideDetectorConcurrency(16 * G, 1) == 1);
        Check("Total RAM can be read on this machine", ResourceUsage.TotalPhysicalBytes() > 0);

        var previousEnvironment = Environment.GetEnvironmentVariable("DESENSITIZER_LLM_RESIDENT");
        Environment.SetEnvironmentVariable("DESENSITIZER_LLM_RESIDENT", null);
        try
        {
            PerfConfig.Apply(0, 3, 0);
            using var fromSetting = new LlamaServerPool();
            Check("The pool takes MaxResident from the saved setting", fromSetting.MaxResident == 3);
            Environment.SetEnvironmentVariable("DESENSITIZER_LLM_RESIDENT", "2");
            using var fromEnvironment = new LlamaServerPool();
            Check("The environment variable still wins over the setting", fromEnvironment.MaxResident == 2);
            using var explicitValue = new LlamaServerPool(1);
            Check("An explicit argument wins over everything", explicitValue.MaxResident == 1);
            Environment.SetEnvironmentVariable("DESENSITIZER_LLM_RESIDENT", null);
            PerfConfig.Apply(0, 0, 0);
            using var automatic = new LlamaServerPool();
            Check("Automatic MaxResident is within 1..3", automatic.MaxResident is >= 1 and <= 3);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DESENSITIZER_LLM_RESIDENT", previousEnvironment);
            PerfConfig.Apply(0, 0, 0);
        }
    }

    private static string[] Names => new[] { "Presidio", "GLiNER", "Privacy Filter" };

    private static void RunnerTests()
    {
        Func<int, CancellationToken, List<string>> fake = (i, t) => { Thread.Sleep(30 * (3 - i)); return new List<string> { "finding-from-" + Names[i] }; };
        var sequential = PerfEngineRunner.RunAll(Names, _ => true, fake, 1, default);
        var concurrent = PerfEngineRunner.RunAll(Names, _ => true, fake, 2, default);
        Check("Concurrent results equal the sequential results in engine order",
            sequential.Select(o => string.Join("|", o.Result!)).SequenceEqual(concurrent.Select(o => string.Join("|", o.Result!)))
            && concurrent.Select(o => o.Index).SequenceEqual(new[] { 0, 1, 2 }) && concurrent.All(o => o.Succeeded));

        int inFlight = 0, maxSeen = 0;
        var gate = new object();
        PerfEngineRunner.RunAll(Names, _ => true, (i, t) =>
        {
            lock (gate) { inFlight++; maxSeen = Math.Max(maxSeen, inFlight); }
            Thread.Sleep(150);
            lock (gate) inFlight--;
            return new List<string>();
        }, 2, default);
        Check("No more than the cap run at once", maxSeen == 2);
        inFlight = 0; maxSeen = 0;
        PerfEngineRunner.RunAll(Names, _ => true, (i, t) => { lock (gate) { inFlight++; maxSeen = Math.Max(maxSeen, inFlight); } Thread.Sleep(80); lock (gate) inFlight--; return new List<string>(); }, 1, default);
        Check("A cap of 1 is exactly sequential", maxSeen == 1);

        var failing = PerfEngineRunner.RunAll(Names, _ => true, (i, t) => i == 1 ? throw new InvalidOperationException("boom") : new List<string> { "ok" + i }, 2, default);
        Check("One failing engine is recorded as an error and the others still deliver", failing[1].Error != null && !failing[1].Succeeded && failing[0].Succeeded && failing[2].Succeeded && failing[1].Error!.Message == "boom");

        var skipped = PerfEngineRunner.RunAll(Names, i => i != 0, (i, t) => new List<string> { "x" }, 2, default);
        Check("A missing engine is reported as skipped, not as a result", skipped[0].Skipped && !skipped[0].Succeeded && skipped[1].Succeeded);

        var started = new CountdownEvent(2);
        var sawCancel = new List<int>();
        using var cts = new CancellationTokenSource();
        var raised = false;
        var thread = new Thread(() =>
        {
            try
            {
                PerfEngineRunner.RunAll(Names, _ => true, (i, t) =>
                {
                    started.Signal();
                    try { Task.Delay(Timeout.Infinite, t).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { lock (sawCancel) sawCancel.Add(i); throw; }
                    return new List<string>();
                }, 2, cts.Token);
            }
            catch (OperationCanceledException) { raised = true; }
        });
        thread.Start();
        started.Wait(5000);
        cts.Cancel();
        var finished = thread.Join(10000);
        Check("Cancellation reaches every running engine and the run throws OperationCanceledException", finished && raised && sawCancel.Count == 2);
        Check("The third engine never starts after cancellation", !sawCancel.Contains(2));

        var already = new CancellationTokenSource(); already.Cancel();
        var threwEarly = false;
        try { PerfEngineRunner.RunAll(Names, _ => true, (i, t) => new List<string>(), 2, already.Token); } catch (OperationCanceledException) { threwEarly = true; }
        Check("An already cancelled token starts nothing", threwEarly);

        var order = new List<string>();
        PerfEngineRunner.RunAll(Names, _ => true, (i, t) => { Thread.Sleep(20); return new List<string>(); }, 2, default, null, o => { lock (order) order.Add(o.Name); });
        Check("The completed callback runs once per engine", order.Count == 3 && order.Distinct().Count() == 3);
    }

    private sealed class FakeWorker : IDisposable
    {
        public bool Alive = true; public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static void WorkerPoolTests()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        using var pool = new PerfWorkerPool<FakeWorker>(TimeSpan.FromMinutes(5), () => now, w => w.Alive);
        var first = pool.Lease("Presidio", () => new FakeWorker());
        pool.Release("Presidio");
        var second = pool.Lease("Presidio", () => new FakeWorker());
        Check("A released worker is reused, not recreated", ReferenceEquals(first, second) && pool.CreatedCount == 1);
        var doubleLease = false;
        try { pool.Lease("Presidio", () => new FakeWorker()); } catch (InvalidOperationException) { doubleLease = true; }
        Check("A leased worker cannot be leased twice", doubleLease);
        pool.Release("Presidio");
        now = now.AddMinutes(4);
        Check("A worker idle for less than the timeout stays resident", pool.SweepIdle() == 0 && !first.Disposed && pool.ResidentCount == 1);
        now = now.AddMinutes(2);
        Check("A worker idle for more than 5 minutes is disposed", pool.SweepIdle() == 1 && first.Disposed && pool.ResidentCount == 0);
        var third = pool.Lease("Presidio", () => new FakeWorker());
        third.Alive = false; pool.Release("Presidio");
        var fourth = pool.Lease("Presidio", () => new FakeWorker());
        Check("A dead worker is replaced and disposed", !ReferenceEquals(third, fourth) && third.Disposed);
        pool.Release("Presidio", failed: true);
        Check("A failed worker is discarded rather than reused", fourth.Disposed && pool.ResidentCount == 0);
        var leased = pool.Lease("GLiNER", () => new FakeWorker());
        now = now.AddHours(1);
        Check("A leased worker is never swept", pool.SweepIdle() == 0 && !leased.Disposed);
        pool.Release("GLiNER");
        pool.DisposeAll();
        Check("DisposeAll disposes every worker", leased.Disposed && pool.ResidentCount == 0);
        Check("The default idle timeout is 5 minutes", new PerfWorkerPool<FakeWorker>().IdleTimeout == TimeSpan.FromMinutes(5));
    }

    private static void EstimateTests()
    {
        var tracker = new PerfTimingTracker();
        tracker.Add(48, 3);
        var measured = PerfEstimate.FromSections(Enumerable.Repeat(1500, 100).ToList(), 3, tracker);
        Check("100 sections x 3 reviewers at 16 s each reads 1 h 20 min", measured.Text == "about 100 sections x 3 reviewers, roughly 1 h 20 min" && measured.Measured);
        var fallback = PerfEstimate.FromSections(Enumerable.Repeat(1500, 100).ToList(), 3);
        Check("Without measurements the default is used and labelled as a first estimate", !fallback.Measured && fallback.Text.Contains("first estimate") && fallback.Seconds > 0);
        Check("A single section and reviewer uses singular words", PerfEstimate.FromSections(new[] { 800 }, 1, tracker).Text.StartsWith("about 1 section x 1 reviewer, roughly "));
        Check("Nothing to review is stated plainly", PerfEstimate.FromSections(new int[0], 3).Text == "Nothing to review.");
        Check("Longer sections cost more in the default estimate", PerfEstimate.FromSections(new[] { 6000 }, 1).Seconds > PerfEstimate.FromSections(new[] { 600 }, 1).Seconds);
        Check("Durations round sensibly", PerfEstimate.FormatDuration(20) == "under 1 min" && PerfEstimate.FormatDuration(150) == "3 min" && PerfEstimate.FormatDuration(25 * 60) == "25 min" && PerfEstimate.FormatDuration(60 * 60) == "1 h" && PerfEstimate.FormatDuration(80 * 60) == "1 h 20 min");
        Check("The text length form derives a section count", PerfEstimate.FromTextLength(150000, 3).Sections == 100 && PerfEstimate.FromTextLength(0, 3).Sections == 0);
        var rolling = new PerfTimingTracker(2);
        rolling.Add(10); rolling.Add(20); rolling.Add(30);
        Check("The rolling average keeps only the last window", rolling.Count == 2 && rolling.AverageSecondsPerPass == 25);
        rolling.Add(double.NaN); rolling.Add(-1); rolling.Add(5, 0);
        Check("Invalid timings are ignored", rolling.Count == 2);
        Check("Estimate text has no em dash", !measured.Text.Contains('—') && !fallback.Text.Contains('—'));
    }

    private static void MemoryLogTests()
    {
        var log = new PerfMemoryLog();
        Check("An empty log has no peak", log.Peak == null && log.Summary() == "No samples.");
        log.Add(0, 500L * 1048576, 1);
        log.Add(1, 3L * 1073741824, 4);
        log.Add(2, 2L * 1073741824, 3);
        log.Add(3, -1, 1);
        Check("The peak is the largest total and bad samples are dropped", log.Samples.Count == 3 && log.Peak!.ProcessCount == 4 && log.Peak.ElapsedSeconds == 1);
        var csv = log.ToCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Check("The CSV has the header and one line per sample, numbers only", csv[0] == PerfMemoryLog.CsvHeader && csv.Count == 4 && csv[2] == "1.0,3072.0,4");
        Check("The summary names the peak", log.Summary().StartsWith("Peak 3.00 GB across 4 processes"));
        var snapshot = ResourceUsage.Snapshot();
        Check("A live snapshot of this process is non-zero", snapshot.Bytes > 0 && snapshot.WorkerCount >= 0);
    }
}

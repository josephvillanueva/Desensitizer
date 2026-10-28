using Desensitizer;

// UI-4: closing waits for a background save that is still being written.
static class SaveSafetyTests
{
    private const string Pass = "Violet-Harbor-Lantern-42";

    public static void Run(CheckFn Check, string work)
    {
        var tracker = new UiSaveTracker();
        Check("save tracker: nothing pending at the start", !tracker.IsPending && tracker.WaitForPending(TimeSpan.FromMilliseconds(10), out var f0) && !f0);

        // A real write that is still running when the wait starts: the file must hold the content after the wait.
        var path = Path.Combine(work, "inflight.dsproj");
        var project = Project.New("In flight", Pass);
        project.Files.Add(new FileRecord { Id = "f1", SourcePath = "C:\\fictional\\a.txt", Display = "a.txt" });
        var json = ProjectStore.Serialize(project);
        var gate = new ManualResetEventSlim(false);
        tracker.Track(Task.Run(() => { gate.Wait(); Thread.Sleep(150); ProjectStore.SaveSerialized(path, Pass, json); }));
        Check("save tracker: a running write is reported as pending", tracker.IsPending);
        Check("save tracker: a short wait on a running write times out and says so", !tracker.WaitForPending(TimeSpan.FromMilliseconds(30), out _));
        gate.Set();
        var finished = tracker.WaitForPending(TimeSpan.FromSeconds(10), out var failed);
        Check("save tracker: the wait returns after the write ended", finished && !failed && !tracker.IsPending);
        Check("save tracker: the project file holds the last edits once the wait is over", File.Exists(path) && ProjectStore.Load(path, Pass).Files.Count == 1);

        tracker.Track(Task.Run(() => throw new IOException("disk full")));
        var done = tracker.WaitForPending(TimeSpan.FromSeconds(5), out var failedWrite);
        Check("save tracker: a failed write is reported so the caller writes again itself", done && failedWrite);

        // ---- UI-5: fatal handler flush, unobserved faults ------------------------------------------------------
        var order = new List<string>();
        var guard = new RuntimeCrashGuard(m => order.Add("show"), c => order.Add("exit"), () => order.Add("stop"));
        guard.RegisterFlush(() => order.Add("flush"));
        guard.HandleFatal(new InvalidOperationException("x"));
        Check("crash guard: the project flush runs after the workers stop and before the message and exit", order.SequenceEqual(new[] { "stop", "flush", "show", "exit" }), string.Join(",", order));

        var slow = new List<string>();
        var slowGuard = new RuntimeCrashGuard(m => slow.Add("show"), c => slow.Add("exit"), () => { }) { FlushTimeout = TimeSpan.FromMilliseconds(200) };
        slowGuard.RegisterFlush(() => Thread.Sleep(5000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        slowGuard.HandleFatal(new Exception("x"));
        Check("crash guard: a flush that hangs is abandoned after the short timeout and the app still exits", watch.Elapsed < TimeSpan.FromSeconds(3) && slow.SequenceEqual(new[] { "show", "exit" }), watch.Elapsed.ToString());
        var throwingFlush = new RuntimeCrashGuard(m => { }, c => { }, () => { });
        throwingFlush.RegisterFlush(() => throw new IOException("disk"));
        var escaped = false;
        try { throwingFlush.HandleFatal(new Exception("x")); } catch { escaped = true; }
        Check("crash guard: a flush that throws never escapes the handler", !escaped);

        var calm = new List<string>();
        var calmGuard = new RuntimeCrashGuard(m => calm.Add("show"), c => calm.Add("exit"), () => calm.Add("stop"));
        var observed = false;
        calmGuard.HandleUnobserved(() => observed = true);
        calmGuard.HandleUnobserved(() => throw new InvalidOperationException("bad"));
        Check("crash guard: an unobserved task fault is observed and is not fatal", observed && calm.Count == 0);

        var flushCalls = 0;
        Action registered = () => flushCalls++;
        UiCrashFlush.Register(registered); UiCrashFlush.Run(); UiCrashFlush.Unregister(registered); UiCrashFlush.Run();
        Check("crash flush: a registered action runs once and an unregistered one does not", flushCalls == 1);
        UiCrashFlush.Register(() => throw new InvalidOperationException("x"));
        var flushThrew = false;
        try { UiCrashFlush.Run(); } catch { flushThrew = true; }
        UiCrashFlush.Register(() => { });
        Check("crash flush: a failing action never throws out of Run", !flushThrew);

        var unobservedRaised = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> counter = (s, e) => Interlocked.Increment(ref unobservedRaised);
        TaskScheduler.UnobservedTaskException += counter;
        try
        {
            var faulted = Task.Run(() => throw new IOException("reader failed"));
            try { faulted.Wait(); } catch (AggregateException) { }
            var lost = Task.Run(() => throw new IOException("lost"));
            RuntimeWorkerProcess.Observe(lost);
            lost = null!;
            Thread.Sleep(200);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); GC.WaitForPendingFinalizers();
            Check("worker process: a faulted reader task that was observed raises no unobserved exception", Volatile.Read(ref unobservedRaised) == 0);
            var hang = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul");
            var timedOut = false;
            try { RuntimeWorkerProcess.Run(hang, "{}", 600, default); } catch (TimeoutException) { timedOut = true; }
            Thread.Sleep(300);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); GC.WaitForPendingFinalizers();
            Check("worker process: a timeout leaves no unobserved reader fault", timedOut && Volatile.Read(ref unobservedRaised) == 0);
        }
        finally { TaskScheduler.UnobservedTaskException -= counter; }
    }
}

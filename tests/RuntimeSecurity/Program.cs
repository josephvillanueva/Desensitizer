// Deterministic runtime-security tests. No real model, no llama-server: fake
// listeners, harmless child processes (ping), and temp folders only.
// Run: dotnet run --project tests/RuntimeSecurity/RuntimeSecurity.csproj
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Desensitizer;

internal static class Program
{
    private static int passed, failed;

    private static void Check(string name, bool ok)
    {
        if (ok) passed++; else { failed++; Console.WriteLine("FAIL: " + name); }
    }

    private static ProcessStartInfo Sleeper(string? exe = null) => new(exe ?? Path.Combine(Environment.SystemDirectory, "ping.exe"))
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        ArgumentList = { "-n", "600", "127.0.0.1" }
    };

    private static bool WaitFor(Func<bool> condition, int milliseconds = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end) { if (condition()) return true; Thread.Sleep(50); }
        return condition();
    }

    private static bool IsGone(int id)
    {
        try { using var p = Process.GetProcessById(id); return p.HasExited; } catch (ArgumentException) { return true; }
    }

    private static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "rt-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int Main()
    {
        try { return RunTests(); }
        catch (Exception error)
        {
            // A probe failure must produce a nonzero test result, not a native
            // crash dialog. Exception messages and data may contain test input.
            Console.Error.WriteLine("FAIL harness infrastructure: " + error.GetType().Name + ". No successful test result is claimed.");
            return 1;
        }
    }

    private static int RunTests()
    {
        PortOwnerTests();
        HostPortGuardTests();
        KeyHandlingTests();
        JobObjectTests();
        OrphanSweepTests();
        IntegrityTests();
        OverrideTests();
        WorkerProcessTests();
        CrashGuardTests();
        SharedJobKillTest();
        Console.WriteLine($"RuntimeSecurity: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ---- R1 ----
    private static void PortOwnerTests()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var self = Environment.ProcessId;
        Check("Listener table reports this process as the owner", RuntimePortOwner.ListenerProcessIds(port).Contains(self));
        Check("Expected owner is accepted", RuntimePortOwner.Check(port, self) == PortOwnership.Child);
        Check("A different expected process id is a foreign listener", RuntimePortOwner.Check(port, self + 100000) == PortOwnership.Other);
        listener.Stop();
        Check("A closed port has no owner", WaitFor(() => RuntimePortOwner.Check(port, self) == PortOwnership.Nobody));
    }

    private static void HostPortGuardTests()
    {
        // A foreign program owns the port: nothing may be sent to it.
        TcpListener? impostor = null;
        using (var host = new LlamaServerHost("fake-runtime", "fake-model", (port, key) => Sleeper(), null, null,
            (port, pid) =>
            {
                impostor ??= Listen(port);
                return RuntimePortOwner.Check(port, pid); // listener belongs to this test process, not the child
            }))
        {
            string failure = "";
            try { host.EnsureReady(default); } catch (InvalidOperationException ex) { failure = ex.Message; }
            Check("A listener owned by another process aborts the start with a clear error", failure.Contains("Another program"));
            Check("No connection was made to the impostor before the abort", impostor != null && !impostor.Pending());
            Check("The child that was started is stopped", !host.IsRunning);
            string second = "";
            try { host.EnsureReady(default); } catch (InvalidOperationException ex) { second = ex.Message; }
            Check("The failure is remembered (fail closed)", second == failure);
        }
        impostor?.Stop();

        // The real owner (child) answers only after it is verified.
        var polls = 0; var requests = 0; TcpListener? server = null; Thread? serve = null; var stop = false;
        using (var host = new LlamaServerHost("fake-runtime", "fake-model", (port, key) => Sleeper(), null, null,
            (port, pid) =>
            {
                polls++;
                if (polls < 4) return PortOwnership.Nobody;
                if (server == null)
                {
                    server = Listen(port);
                    serve = new Thread(() =>
                    {
                        while (!stop)
                        {
                            try
                            {
                                if (!server.Pending()) { Thread.Sleep(10); continue; }
                                using var c = server.AcceptTcpClient();
                                Interlocked.Increment(ref requests);
                                var s = c.GetStream(); var buf = new byte[4096]; _ = s.Read(buf, 0, buf.Length);
                                var body = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                                s.Write(body, 0, body.Length);
                            }
                            catch { }
                        }
                    }) { IsBackground = true };
                    serve.Start();
                }
                return PortOwnership.Child;
            }))
        {
            HttpClient? client = null;
            try { client = host.EnsureReady(default); } catch (Exception) { }
            Check("Readiness waits while nobody listens, then succeeds once the child owns the port", client != null && polls >= 4 && Volatile.Read(ref requests) >= 1);
            Check("No health request was sent before the owner was confirmed", polls >= 4);
        }
        stop = true; server?.Stop();

        // Ownership lost after start (reuse path).
        var owner = PortOwnership.Child; TcpListener? l2 = null;
        using (var host = new LlamaServerHost("fake-runtime", "fake-model", (port, key) => Sleeper(), (c, p, t) => { }, null, (port, pid) => owner))
        {
            // custom readiness skips WaitUntilReady but ownership is still re-checked on reuse
            host.EnsureReady(default);
            owner = PortOwnership.Other;
            string failure = "";
            try { host.EnsureReady(default); } catch (InvalidOperationException ex) { failure = ex.Message; }
            Check("A running server whose port changed hands is stopped and refused", failure.Contains("Another program") && !host.IsRunning);
        }
        l2?.Stop();
    }

    private static TcpListener Listen(int port)
    {
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        return l;
    }

    // ---- R2 ----
    private static void KeyHandlingTests()
    {
        var args = LlamaServerHost.BuildServerArguments("m.gguf", 1234, 20);
        Check("Server arguments carry no api key option", !args.Contains("--api-key") && !args.Contains("--api-key-file"));
        Check("Server arguments keep loopback, context and offload", args.Contains("127.0.0.1") && args.Contains("-ngl") && args.Contains("4096"));
        var info = LlamaServerHost.BuildStartInfo("llama-server.exe", "m.gguf", 1234, "SECRETKEY123", null);
        Check("The key is not anywhere on the command line", !info.ArgumentList.Any(a => a.Contains("SECRETKEY123")) && !info.Arguments.Contains("SECRETKEY123"));
        Check("The key travels in the child's LLAMA_API_KEY environment variable", info.Environment[LlamaServerHost.ApiKeyVariable] == "SECRETKEY123" && LlamaServerHost.ApiKeyVariable == "LLAMA_API_KEY");
    }

    // ---- R3 ----
    private static void JobObjectTests()
    {
        using var control = Process.Start(Sleeper())!;
        var job = new RuntimeJob();
        using var member = Process.Start(Sleeper())!;
        Check("A job object can be created", job.IsActive);
        Check("A child can be assigned to the job", job.TryAssign(member));
        Check("The member is alive while the job is open", !member.HasExited);
        job.Dispose();
        Check("Closing the job handle ends the member", member.WaitForExit(10000));
        Check("A process that was never assigned is unaffected", !control.HasExited);
        try { control.Kill(true); } catch { }
        control.Dispose();

        var dead = new RuntimeJob();
        using var gone = Process.Start(Sleeper())!;
        gone.Kill(true); gone.WaitForExit(5000);
        Check("Assigning an exited process fails without throwing", !dead.TryAssign(gone));
        dead.Dispose();
        Check("A disposed job refuses assignment", !dead.TryAssign(Process.GetCurrentProcess()));
    }

    private static Process? sharedMember;

    private static void SharedJobKillTest()
    {
        // Runs last: it disposes the process-wide job that ResourceUsage.RegisterWorker uses.
        Check("ResourceUsage.StopAllWorkers ends tracked workers", StopAllWorkersWorks());
        using var worker = Process.Start(Sleeper())!;
        sharedMember = worker;
        ResourceUsage.RegisterWorker(worker);
        RuntimeJob.Shared.Dispose();
        Check("Closing the shared job (app exit or crash) ends registered workers", worker.WaitForExit(10000));
    }

    private static bool StopAllWorkersWorks()
    {
        using var worker = Process.Start(Sleeper())!;
        ResourceUsage.RegisterWorker(worker);
        var tracked = ResourceUsage.IsTrackedWorker(worker.Id);
        ResourceUsage.StopAllWorkers();
        return tracked && worker.WaitForExit(10000) && !ResourceUsage.IsTrackedWorker(worker.Id);
    }

    private static int SpawnOrphan(string exe)
    {
        // cmd starts the program and exits at once, so the program's parent is gone.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("start"); start.ArgumentList.Add("\"\""); start.ArgumentList.Add("/b");
        start.ArgumentList.Add(exe); start.ArgumentList.Add("-n"); start.ArgumentList.Add("600"); start.ArgumentList.Add("127.0.0.1");
        var before = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Select(p => p.Id).ToHashSet();
        using (var cmd = Process.Start(start)!) cmd.WaitForExit(10000);
        var found = 0;
        WaitFor(() => { found = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Select(p => p.Id).FirstOrDefault(id => !before.Contains(id)); return found != 0; });
        return found;
    }

    private static void OrphanSweepTests()
    {
        var root = TempFolder();
        var other = TempFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "model-packs"));
            var worker = Path.Combine(root, "model-packs", "fakeworker-" + Guid.NewGuid().ToString("N")[..6] + ".exe");
            var top = Path.Combine(root, "fakeapp-" + Guid.NewGuid().ToString("N")[..6] + ".exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), worker);
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), top);

            var orphan = SpawnOrphan(worker);
            var topOrphan = SpawnOrphan(top);
            using var live = Process.Start(Sleeper(worker))!; // parent (this test) is alive
            Check("Test setup started an orphaned worker", orphan != 0 && !IsGone(orphan));

            Check("A sweep of an unrelated folder ends nothing", RuntimeOrphanSweep.Sweep(other).Count == 0 && !IsGone(orphan));
            var ended = RuntimeOrphanSweep.Sweep(root);
            Check("A worker under the app folder whose parent is gone is ended", ended.Contains(orphan) && WaitFor(() => IsGone(orphan)));
            Check("A worker with a live parent is left alone", !live.HasExited);
            Check("The app's own top-level executables are never swept by default", !ended.Contains(topOrphan) && !IsGone(topOrphan));

            ResourceUsage.RegisterWorker(live);
            Check("A tracked worker is never swept", !RuntimeOrphanSweep.Sweep(root).Contains(live.Id) && !live.HasExited);
            ResourceUsage.UnregisterWorker(live);
            RuntimeOrphanSweep.Sweep(root, false);
            Check("Sweeping top-level files only happens when explicitly requested", WaitFor(() => IsGone(topOrphan)));
            try { live.Kill(true); } catch { }
        }
        finally
        {
            foreach (var p in Process.GetProcesses()) { try { if (p.MainModule?.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true) p.Kill(true); } catch { } }
            Thread.Sleep(300);
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(other, true); } catch { }
        }
    }

    // ---- R4 ----
    private static void IntegrityTests()
    {
        var root = TempFolder();
        try
        {
            var dir = Path.Combine(root, "model-packs", "context-llm");
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, "llama-server.exe");
            var dll = Path.Combine(dir, "ggml.dll");
            var model = Path.Combine(dir, "model.gguf");
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
            File.WriteAllText(dll, "library"); File.WriteAllText(model, "model bytes");
            File.WriteAllText(Path.Combine(root, "App.exe"), "app");

            var none = new RuntimeIntegrity(root);
            Check("No manifest: allowed with a warning flag", none.Verify(exe).Status == IntegrityStatus.NoManifest && none.ManifestMissing && none.IntegrityWarning != null);

            var manifest = RuntimeIntegrity.BuildManifest(root);
            Check("Manifest lists workers, libraries and models but not the app's own files", manifest.Contains("llama-server.exe") && manifest.Contains("ggml.dll") && manifest.Contains("model.gguf") && !manifest.Contains("App.exe"));
            File.WriteAllText(Path.Combine(root, RuntimeIntegrity.ManifestFileName), manifest);
            var verifier = new RuntimeIntegrity(root);
            Check("Matching files verify", verifier.Verify(exe).Status == IntegrityStatus.Verified && verifier.Verify(model).Allowed && !verifier.ManifestMissing && verifier.IntegrityWarning == null);
            verifier.RequireWithRuntimeFiles(exe);
            Check("Streaming hash matches a direct SHA-256", RuntimeIntegrity.HashFile(model) == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant());

            File.WriteAllText(model, "tampered model");
            var tampered = new RuntimeIntegrity(root);
            Check("A changed model is refused", tampered.Verify(model).Status == IntegrityStatus.Mismatch);
            File.WriteAllText(model, "model bytes");
            File.WriteAllText(dll, "evil library");
            var dllTampered = new RuntimeIntegrity(root);
            string dllFailure = "";
            try { dllTampered.RequireWithRuntimeFiles(exe); } catch (InvalidOperationException ex) { dllFailure = ex.Message; }
            Check("A changed runtime library beside the executable blocks the start", dllFailure.Contains("ggml.dll"));
            File.WriteAllText(dll, "library");

            var extra = Path.Combine(root, "model-packs", "unlisted.exe");
            File.WriteAllText(extra, "x");
            Check("With a manifest, an unlisted file is refused", new RuntimeIntegrity(root).Verify(extra).Status == IntegrityStatus.NotListed);
            Check("A file outside the app folder is refused when a manifest exists", new RuntimeIntegrity(root).Verify(Path.Combine(Environment.SystemDirectory, "ping.exe")).Status == IntegrityStatus.NotListed);
            File.Delete(exe);
            Check("A missing listed file is refused", new RuntimeIntegrity(root).Verify(exe).Status == IntegrityStatus.Missing);
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);

            // A hash cache must notice a changed file.
            var cached = new RuntimeIntegrity(root);
            Check("First verify passes", cached.Verify(model).Status == IntegrityStatus.Verified);
            File.WriteAllText(model, "changed later, longer");
            Check("A cached result does not hide a later change", cached.Verify(model).Status == IntegrityStatus.Mismatch);
            File.WriteAllText(model, "model bytes");

            File.WriteAllText(Path.Combine(root, RuntimeIntegrity.ManifestFileName), "{ not json");
            Check("A damaged manifest refuses everything (fail closed)", new RuntimeIntegrity(root).Verify(exe).Status == IntegrityStatus.Unreadable);
            File.WriteAllText(Path.Combine(root, RuntimeIntegrity.ManifestFileName), "{\"version\":1,\"files\":{\"model-packs/context-llm/llama-server.exe\":\"abc\"}}");
            Check("A manifest with a malformed hash refuses everything", new RuntimeIntegrity(root).Verify(exe).Status == IntegrityStatus.Unreadable);

            // The host refuses to start a worker whose hash mismatches, and never launches it.
            File.WriteAllText(Path.Combine(root, RuntimeIntegrity.ManifestFileName), manifest);
            File.WriteAllText(exe, "not the original");
            var started = false;
            using (var host = new LlamaServerHost(exe, model, (port, key) => { started = true; return Sleeper(); }, (c, p, t) => { }, new RuntimeIntegrity(root)))
            {
                string failure = "";
                try { host.EnsureReady(default); } catch (InvalidOperationException ex) { failure = ex.Message; }
                Check("The host refuses a worker whose hash mismatches and starts nothing", failure.Contains("integrity manifest") && !started && host.StartCount == 0);
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        // Production host refuses a runtime outside the app folder (path override backstop).
        using var outside = new LlamaServerHost(Path.Combine(Path.GetTempPath(), "elsewhere", "llama-server.exe"), Path.Combine(Path.GetTempPath(), "m.gguf"));
        string refusal = "";
        try { outside.EnsureReady(default); } catch (InvalidOperationException ex) { refusal = ex.Message; }
        Check("A runtime outside the app folder is refused without the dev flag", refusal.Contains("outside the app folder") && outside.StartCount == 0);
    }

    private static void OverrideTests()
    {
        var env = new Dictionary<string, string?> { ["DESENSITIZER_GLINER_PATH"] = "C:\\evil\\x.exe" };
        Check("Overrides are ignored without the dev flag", RuntimeOverrides.Get("DESENSITIZER_GLINER_PATH", n => env.GetValueOrDefault(n)) == null);
        env["DESENSITIZER_DEV"] = "true";
        Check("Only the exact value 1 enables the dev flag", RuntimeOverrides.Get("DESENSITIZER_GLINER_PATH", n => env.GetValueOrDefault(n)) == null);
        env["DESENSITIZER_DEV"] = "1";
        Check("Overrides are honoured with DESENSITIZER_DEV=1", RuntimeOverrides.Get("DESENSITIZER_GLINER_PATH", n => env.GetValueOrDefault(n)) == "C:\\evil\\x.exe");
        Check("An empty override is ignored in dev mode too", RuntimeOverrides.Get("MISSING", n => env.GetValueOrDefault(n)) == null);
    }

    // ---- R6 ----
    private static void WorkerProcessTests()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        // Worker answers with about 6 MB of stdout BEFORE it reads stdin. Writing a large request first
        // and reading afterwards would deadlock on the full pipe.
        var script = "$line='x'*99; for($i=0;$i -lt 60000;$i++){[Console]::Out.WriteLine($line)}; [Console]::Out.Flush(); $null=[Console]::In.ReadToEnd(); [Console]::Error.Write('done')";
        var info = new ProcessStartInfo(powershell) { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script } };
        var stdin = new string('y', 8 * 1024 * 1024);
        var timer = Stopwatch.StartNew();
        WorkerOutput? result = null;
        var task = Task.Run(() => result = RuntimeWorkerProcess.Run(info, stdin, 90000, default));
        var finished = task.Wait(100000);
        Check("A worker that writes a lot before reading a large request does not deadlock", finished && result != null && result.ExitCode == 0 && result.Output.Length >= 5_000_000 && result.Error == "done");

        var hang = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe")) { ArgumentList = { "-n", "600", "127.0.0.1" } };
        var slow = Stopwatch.StartNew();
        string timeout = "";
        try { RuntimeWorkerProcess.Run(hang, "{}", 800, default); } catch (TimeoutException ex) { timeout = ex.Message; }
        Check("A worker that never finishes is killed at the timeout", timeout.Length > 0 && slow.ElapsedMilliseconds < 20000);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(500);
        var cancelled = false;
        try { RuntimeWorkerProcess.Run(hang, "{}", 60000, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check("Cancellation kills the worker and cancels the call", cancelled);

        var noisy = new ProcessStartInfo(powershell) { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.Write('s'*200000); [Console]::Out.Write('{}')" } };
        var capped = RuntimeWorkerProcess.Run(noisy, "{}", 60000, default);
        Check("Worker stderr is capped", capped.Error.Length <= 65536 && capped.Output == "{}");

        WorkerOutputLimitTests(powershell);

        var echoScript = "[Console]::InputEncoding=New-Object Text.UTF8Encoding($false); [Console]::OutputEncoding=New-Object Text.UTF8Encoding($false); $text=[Console]::In.ReadToEnd(); [Console]::Out.Write($text); [Console]::Error.Write($text)";
        foreach (var unicodeRequest in new[] { "{\"text\":\"Léa Peña, 株式会社, Δοκιμή, 😀\"}", "{\"text\":\"L\\u00e9a Pe\\u00f1a\"}" })
        {
            var echoInfo = new ProcessStartInfo(powershell) { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", echoScript } };
            var echoed = RuntimeWorkerProcess.Run(echoInfo, unicodeRequest, 60000, default);
            Check("Worker UTF-8 round trip preserves literal or escaped JSON", echoed.ExitCode == 0 && echoed.Output == unicodeRequest);
            Check("Worker stderr uses the same UTF-8 protocol", echoed.Error == unicodeRequest);
            Check("Worker stdin uses UTF-8 without a byte order mark", echoInfo.StandardInputEncoding?.GetPreamble().Length == 0 && !echoed.Output.StartsWith('\uFEFF'));
        }

        var tooLong = RuntimeWorkerProcess.SafeMessage("first\nsecond " + new string('z', 500), "fallback");
        Check("Worker error text is truncated to one clean line", tooLong.Length <= 160 && !tooLong.Contains('\n'));
        Check("An empty worker error uses the fixed fallback and never stderr", RuntimeWorkerProcess.SafeMessage("  ", "fallback") == "fallback");
    }

    private static void WorkerOutputLimitTests(string powershell)
    {
        ProcessStartInfo Probe(string script) => new(powershell)
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script }
        };

        var exactScript = "$chunk='x'*65536; for($i=0;$i -lt " + (RuntimeWorkerProcess.MaxOutputCharacters / 65536) + ";$i++){[Console]::Out.Write($chunk)}";
        var exact = RuntimeWorkerProcess.Run(Probe(exactScript), "{}", 60000, default);
        Check("A finite response exactly at the stdout limit is returned completely", exact.ExitCode == 0
            && exact.Output.Length == RuntimeWorkerProcess.MaxOutputCharacters && exact.Output.All(c => c == 'x'));

        var folder = TempFolder();
        try
        {
            var pidPath = Path.Combine(folder, "oversized-worker.pid");
            const string secret = "FictionalSensitiveWorkerPayload";
            var largeScript = "[IO.File]::WriteAllText('" + pidPath.Replace("'", "''") + "',[string]$PID); "
                + "[Console]::Out.Write('" + secret + "'); [Console]::Error.Write('" + secret + "'); "
                + "$chunk='x'*65536; for($i=0;$i -lt " + (RuntimeWorkerProcess.MaxOutputCharacters / 65536 + 2) + ";$i++){[Console]::Out.Write($chunk)}; [Console]::Out.Flush(); Start-Sleep -Seconds 60";
            var timer = Stopwatch.StartNew();
            WorkerOutput? returned = null;
            Exception? failure = null;
            try { returned = RuntimeWorkerProcess.Run(Probe(largeScript), secret, 60000, default); }
            catch (InvalidDataException ex) { failure = ex; }
            Check("Oversized stdout fails closed instead of returning a truncated success", returned == null
                && failure is InvalidDataException && failure.Message == "The analyzer response exceeded the allowed size. The check is incomplete.");
            Check("Oversized stdout terminates a worker promptly instead of waiting for its timeout", failure != null && timer.ElapsedMilliseconds < 20000);
            Check("Oversized stdout failures contain no worker data or nested exception", failure != null
                && failure.Data.Count == 0 && failure.InnerException == null && !failure.ToString().Contains(secret));
            var pid = File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath), out var id) ? id : 0;
            Check("The oversized-output worker is gone after the failed call", pid > 0 && WaitFor(() => IsGone(pid)));

            var partialTimer = Stopwatch.StartNew();
            var timedOut = false;
            try { RuntimeWorkerProcess.Run(Probe("[Console]::Out.Write('{}'); [Console]::Out.Flush(); Start-Sleep -Seconds 60"), "{}", 1500, default); }
            catch (TimeoutException) { timedOut = true; }
            Check("Finite valid-looking stdout cannot turn a still-running timed-out worker into success", timedOut && partialTimer.ElapsedMilliseconds < 20000);

            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(1500);
            var cancelTimer = Stopwatch.StartNew();
            var cancelled = false;
            try { RuntimeWorkerProcess.Run(Probe("[Console]::Out.Write('{}'); [Console]::Out.Flush(); Start-Sleep -Seconds 60"), "{}", 60000, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check("Cancellation still wins over a finite partial stdout response", cancelled && cancelTimer.ElapsedMilliseconds < 20000);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    // ---- R5 ----
    private static void CrashGuardTests()
    {
        var secret = "Maya Lin account 4111-2222-3333";
        var exception = new InvalidOperationException("failed on " + secret);
        exception.Data["document"] = secret;
        var shown = new List<string>(); var exits = new List<int>(); var stops = 0;
        var guard = new RuntimeCrashGuard(shown.Add, exits.Add, () => stops++);
        guard.HandleFatal(exception);
        Check("The crash message never contains the exception message or its data", shown.Count == 1 && !shown[0].Contains("Maya") && !shown[0].Contains("4111") && !shown[0].Contains(secret));
        Check("The crash message names only the exception type", shown[0].Contains("InvalidOperationException"));
        Check("Workers are stopped and the app exits with an error code", stops == 1 && exits.SequenceEqual(new[] { 1 }));
        guard.HandleFatal(new Exception("second"));
        Check("A second failure only stops workers again (no second dialog, no second exit)", shown.Count == 1 && exits.Count == 1 && stops == 2);
        Check("Aggregate exceptions are described by their base exception", RuntimeCrashGuard.Describe(new AggregateException(new TimeoutException(secret))) == "TimeoutException");
        Check("A null exception is described without throwing", RuntimeCrashGuard.Describe(null) == "Unknown error");

        var throwing = new RuntimeCrashGuard(_ => throw new InvalidOperationException("dialog failed"), _ => { }, () => throw new InvalidOperationException("stop failed"));
        var survived = true;
        try { throwing.HandleFatal(exception); } catch { survived = false; }
        Check("A failure inside the handler cannot escape it", survived);
        Check("The fixed message uses no em dash", !RuntimeCrashGuard.Message.Contains('\u2014'));
    }
}

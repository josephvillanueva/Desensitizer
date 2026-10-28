using System.Diagnostics;
using Desensitizer;

namespace RedTeam;

public enum Outcome { Clean, Blocked, Leak, Crash, Hang }

// Manual: analyst-added mapping, always applied. Visible: detected (non-manual) mapping and a plain visible copy of every value in the body,
// which is how the app produces mappings. Detected: detected mapping for a value that is NOT visible anywhere (hidden-only; the app would not create it, so this is defence in depth).
public enum Mode { Manual, Visible, Detected }

// A known leak stays visible as "LEAK RT-xxx" and makes the full suite exit nonzero.
public sealed record Known(string Id, string Severity, string Weakness, string Fix);

public sealed class Attack
{
    public string Name = "";
    public string Group = "";
    public string Description = "";
    public string Ext = ".docx";
    public Func<byte[]> Build = () => Array.Empty<byte>();
    // The mappings that exist for the project. Default: all three sentinels.
    public string[] Values = Sentinel.Full;
    public Mode[] Modes = { Mode.Manual, Mode.Visible, Mode.Detected };
    // Same attack with a plain visible copy of every value added to the body. Null when the format has no such variant.
    public Func<byte[]>? BuildVisible;
    public Known? Known;
    // Text sources only: pass the raw bytes the file holds (encodings).
    public bool AllowUnapplied;
}

public sealed record Result(Attack Attack, Mode Mode, Outcome Outcome, string Detail, List<Finding> Findings);

public static class Harness
{
    static int counter;
    public static bool Trace;
    public static string LastTrace = "";

    public static List<Mapping> Mappings(IEnumerable<string> values, bool manual)
    {
        var list = new List<Mapping>();
        var n = 0;
        foreach (var v in values)
        {
            n++;
            var category = v == Sentinel.Person ? "PERSON" : v == Sentinel.Company ? "ORGANIZATION" : "IDENTIFIER";
            list.Add(new Mapping
            {
                OriginalValue = v, NormalizedValue = Project.Normalize(v), Category = category,
                Token = "[" + category + "_" + n.ToString("000") + "]", Approved = true, IsManual = manual, Confidence = 1,
            });
        }
        return list;
    }

    public static string WorkRoot
    {
        get
        {
            var root = Path.Combine(Path.GetTempPath(), "RedTeam-" + Environment.ProcessId);
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static Result Run(Attack attack, Mode mode)
    {
        var dir = Path.Combine(WorkRoot, "a" + Interlocked.Increment(ref counter));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source" + attack.Ext);
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);
        var output = Path.Combine(outDir, "protected" + attack.Ext);
        try
        {
            byte[] data;
            try { data = (mode == Mode.Visible ? attack.BuildVisible! : attack.Build)(); }
            catch (Exception ex) { return new Result(attack, mode, Outcome.Crash, "BUILD ERROR in the test itself: " + ex.GetType().Name + " " + ex.Message, new()); }
            File.WriteAllBytes(source, data);
            var before = File.ReadAllBytes(source);
            var mappings = Mappings(attack.Values, mode == Mode.Manual);
            Exception? error = null;
            LastTrace = "";
            var task = Task.Run(() =>
            {
                if (Trace) AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
                try { DocumentProcessor.Desensitize(source, output, mappings, null, null, attack.AllowUnapplied); }
                catch (Exception ex) { error = ex; }
                finally { AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance; }
            });
            if (!task.Wait(TimeSpan.FromSeconds(60))) return new Result(attack, mode, Outcome.Hang, "export did not finish in 60 s", new());
            var findings = new List<Finding>();
            var leftovers = Directory.GetFiles(outDir).Concat(Directory.GetFiles(dir).Where(f => f != source)).ToList();
            if (error != null)
            {
                // A blocked export must leave nothing behind, and its message must not quote the sentinel.
                var text = error.ToString() + string.Join(" ", error.Data.Values.Cast<object?>().Select(v => v?.ToString()));
                if (Scanner.TextHas(text)) findings.Add(new Finding("exception-message", "text", "sentinel-in-exception", true));
                foreach (var f in leftovers) findings.Add(new Finding("left-behind:" + Path.GetFileName(f), "file", "output-left-after-block", false));
                var kind = error is InvalidDataException || error is IOException || error is ArgumentException || error is UnauthorizedAccessException || error is OperationCanceledException || error is ObjectDisposedException || error is FileNotFoundException;
                if (findings.Count > 0) return new Result(attack, mode, Outcome.Leak, error.GetType().Name + ": " + Trim(error.Message), findings);
                return new Result(attack, mode, kind ? Outcome.Blocked : Outcome.Crash, error.GetType().Name + ": " + Trim(error.Message), findings);
            }
            if (!File.Exists(output)) return new Result(attack, mode, Outcome.Crash, "no exception and no output file", findings);
            // The real export (Shared/UiMainWindow.cs RunFinalSafetyAnalysisAsync) also runs the fragment guard on the protected copy and stops
            // with a user-readable message when an approved name survives only as a shortened form. The copy is never released then.
            var stop = FragmentGate(output, mappings);
            if (stop != null)
            {
                try { File.Delete(output); } catch { }
                if (Scanner.TextHas(stop)) findings.Add(new Finding("gate-message", "text", "sentinel-in-gate-message", true));
                foreach (var f in Directory.GetFiles(outDir).Concat(Directory.GetFiles(dir).Where(f => f != source))) findings.Add(new Finding("left-behind:" + Path.GetFileName(f), "file", "output-left-after-block", false));
                return findings.Count > 0 ? new Result(attack, mode, Outcome.Leak, Summarize(findings), findings) : new Result(attack, mode, Outcome.Blocked, "FinalSafety: " + Trim(stop), findings);
            }
            findings.AddRange(Scanner.ScanFile(output));
            foreach (var f in leftovers.Where(f => f != output)) findings.Add(new Finding("left-behind:" + Path.GetFileName(f), "file", "temp-left-after-success", false));
            if (!File.ReadAllBytes(source).SequenceEqual(before)) findings.Add(new Finding("source", "file", "source-modified", true));
            return findings.Count > 0 ? new Result(attack, mode, Outcome.Leak, Summarize(findings), findings) : new Result(attack, mode, Outcome.Clean, "", findings);
        }
        catch(Exception failure)
        {
            return new Result(attack,mode,Outcome.Crash,"Harness infrastructure failed: "+failure.GetType().Name,new());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // Null when the gate passes. Same inputs as the UI: the exported text plus the hidden channels of the copy, and every project mapping.
    static string? FragmentGate(string output, List<Mapping> mappings)
    {
        try
        {
            var text = DocumentProcessor.ReadText(output);
            IReadOnlyList<string> hidden = Array.Empty<string>();
            try { hidden = DocumentProcessor.ReadHiddenChannels(output); } catch { }
            var gate = FragmentGuard.Gate(UiHiddenText.Append(text, hidden), mappings, "redteam-file");
            return gate.Blocked ? gate.Message : null;
        }
        catch { return null; }
    }

    static void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (e.Exception is NullReferenceException or ArgumentOutOfRangeException or InvalidCastException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException && LastTrace.Length == 0)
            LastTrace = e.Exception.GetType().Name + ": " + e.Exception.Message + Environment.NewLine + string.Join(Environment.NewLine, (e.Exception.StackTrace ?? "").Split('\n').Take(8));
    }

    static string Trim(string s) => s.Length > 160 ? s.Substring(0, 160) + "..." : s;

    static string Summarize(List<Finding> findings) =>
        string.Join("; ", findings.GroupBy(f => f.Where + "|" + f.Needle).Take(6).Select(g => g.First().Where + " [" + g.First().Variant + "] " + g.First().Needle));
}


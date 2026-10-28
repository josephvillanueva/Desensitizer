using System.Diagnostics;
using System.Text;
using RedTeam;

try { return Run(args); }
catch (Exception failure)
{
    Console.Error.WriteLine("FAIL harness infrastructure: " + failure.GetType().Name + ". No successful test result is claimed.");
    return 1;
}

static int Run(string[] args)
{

// Red team suite: hostile fictional documents against the real export gate.
// Exit code 0 only when every attack is blocked or clean. Known leaks remain failures.
// Exit code 1 when an unexpected leak, crash or hang appears, or when the scanner self-test fails.
// Usage: RedTeam [--only <name prefix>] [--regression] [--tsv <path>]
// --regression is the full-value DOCX regression scope, not full-suite certification.
string? only = null; string? tsv = null; bool regression = false; bool fullValue = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--only" && i + 1 < args.Length) only = args[++i];
    if (args[i] == "--tsv" && i + 1 < args.Length) tsv = args[++i];
    if (args[i] == "--trace") Harness.Trace = true;
    if (args[i] == "--regression") regression = true;
    if (args[i] == "--full-value") fullValue = true;
}
var timer = Stopwatch.StartNew();
var attacks = Catalog.All().Where(a => only == null || a.Name.StartsWith(only, StringComparison.OrdinalIgnoreCase)).ToList();
if (regression || fullValue)
{
    attacks = attacks.Where(a => (fullValue || a.Name.StartsWith("docx/", StringComparison.Ordinal))).ToList();
    Console.WriteLine("SCOPE " + (fullValue ? "full-value attacks across all fixture formats" : "full-value DOCX regressions") + " and scanner self-tests. This is fixture coverage, not real-document accuracy certification.");
}
if (attacks.Count == 0) { Console.Error.WriteLine("FAIL no attacks selected."); return 1; }
var names = attacks.GroupBy(a => a.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
if (names.Count > 0) { Console.WriteLine("FAIL duplicate attack names: " + string.Join(", ", names)); return 1; }

var bad = 0; var clean = 0; var blocked = 0; var known = 0; var fixedKnown = 0;
var rows = new List<string>();
var leakIds = new SortedSet<string>();
foreach (var attack in attacks)
{
    var results = attack.Modes.Where(m => m != Mode.Visible || attack.BuildVisible != null).Select(m => Harness.Run(attack, m)).ToList();
    if (Harness.Trace && Harness.LastTrace.Length > 0) Console.WriteLine("TRACE " + attack.Name + " :: " + Harness.LastTrace);
    var worst = results.OrderByDescending(r => r.Outcome).First();
    var modeNote = string.Join("/", results.Select(r => r.Mode.ToString()[0] + ":" + r.Outcome));
    foreach (var r in results)
        rows.Add(string.Join("\t", attack.Name, attack.Group, attack.Description.Replace('\t', ' '), r.Mode.ToString().ToLowerInvariant(), r.Outcome, r.Detail.Replace('\t', ' ').Replace('\n', ' '), attack.Known?.Id ?? "", attack.Known?.Severity ?? ""));
    if (worst.Outcome is Outcome.Clean or Outcome.Blocked)
    {
        if (attack.Known != null) { fixedKnown++; Console.WriteLine("FIXED " + attack.Known.Id + " " + attack.Name + " no longer leaks (" + modeNote + "). Remove the Known entry."); }
        if (results.All(r => r.Outcome == Outcome.Clean)) clean++; else blocked++;
        Console.WriteLine("PASS " + attack.Name + " " + modeNote);
        continue;
    }
    if (attack.Known != null && worst.Outcome == Outcome.Leak)
    {
        known++;
        leakIds.Add(attack.Known.Id);
        Console.WriteLine("LEAK " + attack.Known.Id + " " + attack.Name + " severity=" + attack.Known.Severity + " " + modeNote + " :: " + worst.Detail);
        continue;
    }
    bad++;
    Console.WriteLine("FAIL " + worst.Outcome.ToString().ToUpperInvariant() + " " + attack.Name + " " + modeNote + " :: " + worst.Detail);
}

// The scanner must see what it is built to see, or every pass above means nothing.
var selfTest = SelfTest.Run();
foreach (var line in selfTest.Lines) Console.WriteLine(line);
if (!selfTest.Passed) bad++;

if (tsv != null) File.WriteAllLines(tsv, rows, new UTF8Encoding(false));
try { Directory.Delete(Harness.WorkRoot, true); } catch { }
Console.WriteLine($"{attacks.Count} attacks: {clean} clean, {blocked} blocked, {known} known leaks ({string.Join(", ", leakIds)}), {fixedKnown} fixed, {bad} unexpected, {timer.Elapsed.TotalSeconds:0.0}s");
Console.WriteLine($"{attacks.Count - bad - known} passed, {bad} failed");
return bad == 0 && known == 0 ? 0 : 1;
}


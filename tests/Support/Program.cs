using System.Reflection;
using Desensitizer;

// Tests for the privacy-safe support info (item D2). Every secret below is fictional.
var failures = 0;
var total = 0;
void Check(string name, bool passed, string? detail = null)
{
    total++;
    if (!passed) failures++;
    Console.WriteLine((passed ? "PASS " : "FAIL ") + name + (!passed && detail != null ? "  [" + detail + "]" : ""));
}

const string Secret = "ZEBRA-SECRET-Mary-Santos-4417";
string[] sentinels =
{
    Secret,
    @"C:\Users\ZebraUser\Documents\Acme Merger Draft.docx",
    "Acme Holdings Ltd passphrase Violet-Harbor-Lantern-42",
    "System.InvalidOperationException: could not read Mary Santos payroll.xlsx",
    "mary.santos@acme.example",
};

// The fields that exist today. Adding a property to a Support* input class means editing this list (and
// SupportBundle.AllowedFields) after deciding the new field can never carry document content.
var expectedFields = new[]
{
    "SupportInput.AppName", "SupportInput.AppVersion", "SupportInput.BuildCommit", "SupportInput.OsVersion",
    "SupportInput.TotalRamBytes", "SupportInput.CpuCores",
    "SupportInput.LlamaThreadsSetting", "SupportInput.MaxResidentModelsSetting", "SupportInput.DetectorConcurrencySetting", "SupportInput.WorkerIdleMinutesSetting",
    "SupportInput.LlamaThreadsEffective", "SupportInput.MaxResidentModelsEffective", "SupportInput.DetectorConcurrencyEffective",
    "SupportInput.IntegrityManifestFound", "SupportInput.Engines", "SupportInput.Phases", "SupportInput.CategoryCounts",
    "SupportInput.SectionCount", "SupportInput.FailedSectionCount",
    "SupportEngine.Name", "SupportEngine.Ran", "SupportEngine.ExitCode",
    "SupportPhase.Name", "SupportPhase.Seconds",
};

var reflected = new[] { typeof(SupportInput), typeof(SupportEngine), typeof(SupportPhase) }
    .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => t.Name + "." + p.Name)).OrderBy(x => x, StringComparer.Ordinal).ToList();
var unlisted = reflected.Except(expectedFields).ToList();
var missing = expectedFields.Except(reflected).ToList();
Check("allow-list: every field of the support input classes is on the allow-list (new: " + string.Join(", ", unlisted) + ")", unlisted.Count == 0);
Check("allow-list: no listed field has been removed without updating the list", missing.Count == 0, string.Join(", ", missing));
Check("allow-list: SupportBundle.AllowedFields equals the reviewed list", SupportBundle.AllowedFields.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expectedFields.OrderBy(x => x, StringComparer.Ordinal)));
var allowedTypes = new HashSet<Type> { typeof(string), typeof(int), typeof(long), typeof(bool), typeof(double), typeof(int?), typeof(List<SupportEngine>), typeof(List<SupportPhase>), typeof(Dictionary<string, int>) };
var oddTypes = new[] { typeof(SupportInput), typeof(SupportEngine), typeof(SupportPhase) }.SelectMany(t => t.GetProperties()).Where(p => !allowedTypes.Contains(p.PropertyType)).Select(p => p.Name + ":" + p.PropertyType.Name).ToList();
Check("allow-list: no field can hold an exception, object or free structure", oddTypes.Count == 0, string.Join(", ", oddTypes));

var baseline = SupportBundle.Build(new SupportInput());
// Fill every string with each sentinel (properties, nested lists and dictionary keys) and read the report.
foreach (var sentinel in sentinels)
{
    var input = new SupportInput();
    foreach (var property in typeof(SupportInput).GetProperties().Where(p => p.PropertyType == typeof(string))) property.SetValue(input, sentinel);
    input.Engines.Add(new SupportEngine { Name = sentinel, Ran = true, ExitCode = 0 });
    input.Phases.Add(new SupportPhase { Name = sentinel, Seconds = 1.5 });
    input.CategoryCounts[sentinel] = 3;
    input.CategoryCounts["person"] = 2;
    var report = SupportBundle.Build(input, "someone", "somecomputer");
    var fragments = sentinel.Split(new[] { ' ', '\\', ':', '@' }, StringSplitOptions.RemoveEmptyEntries).Where(f => f.Length >= 4 && !baseline.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    Check("sentinel: report contains none of the secret text (" + sentinel.Substring(0, Math.Min(18, sentinel.Length)) + "...)", !report.Contains(sentinel) && fragments.All(f => !report.Contains(f, StringComparison.OrdinalIgnoreCase)), report);
    Check("sentinel: report still counts the known category", report.Contains("OTHER 3") && report.Contains("PERSON 2"));
}

// Everything at once, and a "sentinel" passed in as a name that is a real engine name.
{
    var input = new SupportInput { AppName = "AI Desensitizer", AppVersion = "0.2.12", BuildCommit = "abc1234", OsVersion = "10.0.26300.0", TotalRamBytes = 17179869184, CpuCores = 8, LlamaThreadsSetting = 0, LlamaThreadsEffective = 6, MaxResidentModelsEffective = 1, DetectorConcurrencyEffective = 2, WorkerIdleMinutesSetting = 5, IntegrityManifestFound = false, SectionCount = 12, FailedSectionCount = 1 };
    input.Engines.Add(new SupportEngine { Name = "GLiNER", Ran = true, ExitCode = 0 });
    input.Engines.Add(new SupportEngine { Name = "Presidio", Ran = false });
    input.Phases.Add(new SupportPhase { Name = "scan", Seconds = 12.34 });
    input.CategoryCounts["ORGANIZATION"] = 4;
    var report = SupportBundle.Build(input, "nobody", "nowhere");
    Check("report: shows app name and version", report.Contains("App: AI Desensitizer 0.2.12"));
    Check("report: shows build commit, Windows version and memory", report.Contains("Build commit: abc1234") && report.Contains("Windows version: 10.0.26300.0") && report.Contains("Total memory: 16.0 GB"));
    Check("report: shows cores and performance settings with Automatic meaning", report.Contains("Logical CPU cores: 8") && report.Contains("AI threads: 0, in use 6") && report.Contains("0 means Automatic"));
    Check("report: shows integrity manifest status", report.Contains("Integrity manifest: not found"));
    Check("report: lists engines with names and exit codes only", report.Contains("GLiNER: ran, exit code 0") && report.Contains("Presidio: did not run, exit code not reported"));
    Check("report: shows phase timings, category counts and section counts", report.Contains("scan: 12.3") && report.Contains("ORGANIZATION 4") && report.Contains("Sections: 12 checked, 1 failed"));
    Check("report: contains no em dash, en dash or non-ASCII character", report.All(c => c < 128));
    Check("report: begins with the privacy statement", report.StartsWith(SupportBundle.Header));
}

// Defaults, nulls and nonsense numbers do not throw and stay inside bounds.
{
    var empty = SupportBundle.Build(new SupportInput());
    Check("report: an empty input still produces a report", empty.Contains("App: Unknown unknown") && empty.Contains("none recorded yet"));
    var odd = new SupportInput { Engines = null!, Phases = null!, CategoryCounts = null!, CpuCores = -5, TotalRamBytes = -1, SectionCount = int.MaxValue };
    string oddReport;
    try { oddReport = SupportBundle.Build(odd); } catch (Exception ex) { oddReport = "THROW " + ex.GetType().Name; }
    Check("report: null lists and out-of-range numbers do not throw", !oddReport.StartsWith("THROW"), oddReport);
    var nan = new SupportInput(); nan.Phases.Add(new SupportPhase { Name = "scan", Seconds = double.NaN }); nan.Phases.Add(new SupportPhase { Name = "deep", Seconds = double.PositiveInfinity });
    Check("report: NaN and infinite timings are shown as n/a", SupportBundle.Build(nan).Split("n/a").Length == 3);
    Check("report: a null input object does not throw", SupportBundle.Build(null!).Contains("Support info"));
}

// The user and computer name are scrubbed even if one gets through by mistake.
{
    var scrubbed = SupportBundle.ScrubIdentity(@"path C:\Users\zebrauser\x and ZEBRAPC", "ZebraUser", "ZebraPC");
    Check("scrub: user name and computer name are replaced", scrubbed == @"path C:\Users\<user>\x and <computer>", scrubbed);
    Check("scrub: a one-letter name is ignored rather than mangling words", SupportBundle.ScrubIdentity("banana", "a", "b") == "banana");
    Check("scrub: whole words only", SupportBundle.ScrubIdentity("administrator", "admin", "") == "administrator");
    var viaBuild = SupportBundle.Build(new SupportInput { AppName = "Desensitizer" }, "Desensitizer", "none-pc");
    Check("scrub: Build applies it to the finished text", viaBuild.Contains("<user>"));
}

// Telemetry keeps numbers and fixed names only.
{
    SupportTelemetry.Reset();
    SupportTelemetry.RecordPhase("scan", 4.5);
    SupportTelemetry.RecordPhase("scan", 6.5);
    SupportTelemetry.RecordPhase("deep", double.NaN);
    SupportTelemetry.RecordPhase("deep", -3);
    SupportTelemetry.RecordEngines(new[] { "Presidio" }, new[] { "Presidio", "GLiNER" });
    SupportTelemetry.RecordSections(9, 2);
    var input = new SupportInput();
    SupportTelemetry.CopyInto(input);
    Check("telemetry: the latest value per phase is kept and invalid numbers are ignored", input.Phases.Count == 1 && input.Phases[0].Seconds == 6.5);
    Check("telemetry: engines record which ran", input.Engines.Count == 2 && input.Engines.Single(e => e.Name == "Presidio").Ran && !input.Engines.Single(e => e.Name == "GLiNER").Ran);
    Check("telemetry: section counts are kept", input.SectionCount == 9 && input.FailedSectionCount == 2);
    for (var i = 0; i < 40; i++) SupportTelemetry.RecordPhase("phase" + i, i);
    var capped = new SupportInput(); SupportTelemetry.CopyInto(capped);
    Check("telemetry: the phase list is bounded", capped.Phases.Count <= 12);
    var withSecret = new SupportInput(); SupportTelemetry.Reset(); SupportTelemetry.RecordPhase(Secret, 1); SupportTelemetry.RecordEngines(new[] { Secret }); SupportTelemetry.CopyInto(withSecret);
    Check("telemetry: even a secret recorded by mistake never reaches the report", !SupportBundle.Build(withSecret).Contains("ZEBRA"));
    SupportTelemetry.Reset();
    var reset = new SupportInput(); SupportTelemetry.CopyInto(reset);
    Check("telemetry: Reset clears everything", reset.Phases.Count == 0 && reset.Engines.Count == 0 && reset.SectionCount == 0);
}

Console.WriteLine($"{total - failures} passed, {failures} failed");
return failures == 0 ? 0 : 1;

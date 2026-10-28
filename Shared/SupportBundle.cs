using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Privacy-safe "support info" (item D2). The report is built ONLY from the typed fields below, and every
// field passes through a sanitizer that accepts a fixed vocabulary or a strict number format, so a document
// value, file name, path, project name, passphrase or exception message can never reach the text, even when
// one is passed in by mistake. A test (tests/Support) fills every field with secrets and checks the output,
// and fails when a field is added that is not on the allow-list in SupportBundle.AllowedFields.
public sealed class SupportEngine
{
    // One of the known engine names (anything else is reported as "Other").
    public string Name { get; set; } = "";
    public bool Ran { get; set; }
    // Null when the exit code was not reported.
    public int? ExitCode { get; set; }
}

public sealed class SupportPhase
{
    // "scan", "deep" or "export" (anything else is reported as "other").
    public string Name { get; set; } = "";
    public double Seconds { get; set; }
}

public sealed class SupportInput
{
    public string AppName { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string BuildCommit { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public long TotalRamBytes { get; set; }
    public int CpuCores { get; set; }
    // As saved in Settings (0 = Automatic) and as actually in use.
    public int LlamaThreadsSetting { get; set; }
    public int MaxResidentModelsSetting { get; set; }
    public int DetectorConcurrencySetting { get; set; }
    public int WorkerIdleMinutesSetting { get; set; }
    public int LlamaThreadsEffective { get; set; }
    public int MaxResidentModelsEffective { get; set; }
    public int DetectorConcurrencyEffective { get; set; }
    public bool IntegrityManifestFound { get; set; }
    public List<SupportEngine> Engines { get; set; } = new();
    public List<SupportPhase> Phases { get; set; } = new();
    // Number of findings per category name (names outside the known list are counted as "OTHER").
    public Dictionary<string, int> CategoryCounts { get; set; } = new();
    public int SectionCount { get; set; }
    public int FailedSectionCount { get; set; }
}

public static class SupportBundle
{
    // Every field the report may read. Adding a property to a Support* input class without adding it here (and to
    // the expected list in tests/Support) fails the build gate on purpose: decide first that it is safe.
    public static readonly IReadOnlyList<string> AllowedFields = new[]
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

    private static readonly string[] KnownApps = { "Desensitizer", "AI Desensitizer" };
    private static readonly string[] KnownEngines = { "Presidio", "GLiNER", "Privacy Filter", "Qwen", "NuExtract", "Gemma", "Built-in rules" };
    private static readonly string[] KnownPhases = { "scan", "deep", "export" };
    private static readonly string[] KnownCategories =
    {
        "PERSON", "EMAIL", "PHONE", "ADDRESS", "URL", "IDENTIFIER", "ACCOUNT", "DATE", "ORGANIZATION", "VENDOR", "DEPARTMENT",
        "PROJECT", "SYSTEM", "PRODUCT", "LOCATION", "CUSTOM", "OTHER",
    };
    private static readonly Regex VersionPattern = new(@"^\d{1,4}(\.\d{1,5}){1,3}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex CommitPattern = new(@"^[0-9a-f]{7,40}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public const string Header = "Support info (counts, versions and timings only; no document text, names, file names or paths)";

    public static string Build(SupportInput input, string? userName = null, string? machineName = null)
    {
        input ??= new SupportInput();
        var text = new StringBuilder();
        void Line(string value) => text.Append(value).Append("\r\n");
        Line(Header);
        Line("App: " + Pick(input.AppName, KnownApps, "Unknown") + " " + Version(input.AppVersion));
        Line("Build commit: " + (CommitPattern.IsMatch(input.BuildCommit ?? "") ? input.BuildCommit : "not available"));
        Line("Windows version: " + Version(input.OsVersion));
        Line("Total memory: " + (input.TotalRamBytes > 0 ? (input.TotalRamBytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : "unknown"));
        Line("Logical CPU cores: " + Count(input.CpuCores));
        Line("Performance settings (saved value, then value in use; 0 means Automatic):");
        Line("  AI threads: " + Count(input.LlamaThreadsSetting) + ", in use " + Count(input.LlamaThreadsEffective));
        Line("  AI models kept loaded: " + Count(input.MaxResidentModelsSetting) + ", in use " + Count(input.MaxResidentModelsEffective));
        Line("  Detector engines at once: " + Count(input.DetectorConcurrencySetting) + ", in use " + Count(input.DetectorConcurrencyEffective));
        Line("  Idle minutes before a worker closes: " + Count(input.WorkerIdleMinutesSetting));
        Line("Integrity manifest: " + (input.IntegrityManifestFound ? "found" : "not found (local programs were not checked against known hashes)"));
        var engines = (input.Engines ?? new()).Where(e => e != null).Take(12).ToList();
        if (engines.Count == 0) Line("Engines: none recorded yet");
        else
        {
            Line("Engines:");
            foreach (var engine in engines)
                Line("  " + Pick(engine.Name, KnownEngines, "Other") + ": " + (engine.Ran ? "ran" : "did not run") + ", exit code " + (engine.ExitCode.HasValue ? engine.ExitCode.Value.ToString(CultureInfo.InvariantCulture) : "not reported"));
        }
        var phases = (input.Phases ?? new()).Where(p => p != null).Take(12).ToList();
        if (phases.Count == 0) Line("Last check timings: none recorded yet");
        else
        {
            Line("Last check timings (seconds):");
            foreach (var phase in phases) Line("  " + Pick(phase.Name, KnownPhases, "other") + ": " + Seconds(phase.Seconds));
        }
        var categories = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in input.CategoryCounts ?? new())
        {
            var key = Pick((pair.Key ?? "").ToUpperInvariant(), KnownCategories, "OTHER");
            categories[key] = categories.GetValueOrDefault(key) + Math.Clamp(pair.Value, 0, 1_000_000);
        }
        Line(categories.Count == 0 ? "Findings per category: none recorded yet" : "Findings per category: " + string.Join(", ", categories.Select(p => p.Key + " " + p.Value.ToString(CultureInfo.InvariantCulture))));
        Line("Sections: " + Count(input.SectionCount) + " checked, " + Count(input.FailedSectionCount) + " failed");
        return ScrubIdentity(text.ToString(), userName ?? Environment.UserName, machineName ?? Environment.MachineName);
    }

    // Last line of defence: if the user or computer name somehow appears, it is replaced.
    public static string ScrubIdentity(string text, string? userName, string? machineName)
    {
        foreach (var (name, replacement) in new[] { (userName, "<user>"), (machineName, "<computer>") })
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2) continue;
            try { text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name.Trim()) + @"(?![\p{L}\p{N}])", replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (RegexMatchTimeoutException) { }
        }
        return text;
    }

    private static string Pick(string? value, string[] allowed, string otherwise) => allowed.FirstOrDefault(a => string.Equals(a, (value ?? "").Trim(), StringComparison.Ordinal)) ?? otherwise;

    private static string Version(string? value) => VersionPattern.IsMatch((value ?? "").Trim()) ? value!.Trim() : "unknown";

    private static string Count(int value) => Math.Clamp(value, 0, 1_000_000).ToString(CultureInfo.InvariantCulture);

    private static string Seconds(double value) => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? "n/a" : Math.Min(value, 1_000_000).ToString("0.0", CultureInfo.InvariantCulture);
}

// What the app remembers about its last check, as numbers and fixed names only (never text from a document).
public static class SupportTelemetry
{
    private static readonly object Gate = new();
    private static readonly List<SupportPhase> Phases = new();
    private static readonly List<SupportEngine> Engines = new();
    private static int sectionCount;
    private static int failedSectionCount;

    public static void RecordPhase(string kind, double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return;
        lock (Gate)
        {
            Phases.RemoveAll(p => p.Name == kind);
            Phases.Add(new SupportPhase { Name = kind, Seconds = seconds });
            while (Phases.Count > 12) Phases.RemoveAt(0);
        }
    }

    // names: the engines that finished. exitCode is not reported by the detectors today, so it stays null.
    public static void RecordEngines(IEnumerable<string>? ranNames, IEnumerable<string>? allNames = null)
    {
        lock (Gate)
        {
            Engines.Clear();
            var ran = new HashSet<string>(ranNames ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (var name in (allNames ?? ran).Concat(ran).Distinct().Take(12)) Engines.Add(new SupportEngine { Name = name, Ran = ran.Contains(name) });
        }
    }

    public static void RecordSections(int sections, int failed)
    {
        lock (Gate) { sectionCount = Math.Max(0, sections); failedSectionCount = Math.Max(0, failed); }
    }

    public static void Reset()
    {
        lock (Gate) { Phases.Clear(); Engines.Clear(); sectionCount = 0; failedSectionCount = 0; }
    }

    public static void CopyInto(SupportInput input)
    {
        lock (Gate)
        {
            input.Phases = Phases.Select(p => new SupportPhase { Name = p.Name, Seconds = p.Seconds }).ToList();
            input.Engines = Engines.Select(e => new SupportEngine { Name = e.Name, Ran = e.Ran, ExitCode = e.ExitCode }).ToList();
            input.SectionCount = sectionCount;
            input.FailedSectionCount = failedSectionCount;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Desensitizer;

// Reads the live machine and app state into a SupportInput. Everything read here is a number, a version or a
// fixed name; the sanitizers in SupportBundle make sure of that again. No project text, no paths.
public static class SupportCollector
{
    // project: only the mapping categories are counted, never the values.
    public static SupportInput Collect(Project? project, string appName, int modelCount)
    {
        var input = new SupportInput { AppName = appName };
        try { input.AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? ""; } catch (Exception) { }
        try
        {
            var informational = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = informational.IndexOf('+');
            if (plus >= 0) input.BuildCommit = informational.Substring(plus + 1);
        }
        catch (Exception) { }
        try { input.OsVersion = Environment.OSVersion.Version.ToString(); } catch (Exception) { }
        long ram = 0;
        try { ram = ResourceUsage.TotalPhysicalBytes(); } catch (Exception) { }
        input.TotalRamBytes = ram;
        input.CpuCores = Environment.ProcessorCount;
        input.LlamaThreadsSetting = PerfConfig.LlamaThreads;
        input.MaxResidentModelsSetting = PerfConfig.MaxResidentModels;
        input.DetectorConcurrencySetting = PerfConfig.DetectorConcurrency;
        input.WorkerIdleMinutesSetting = PerfConfig.WorkerIdleMinutes;
        try { input.LlamaThreadsEffective = LlamaServerHost.DefaultThreads(); } catch (Exception) { }
        input.MaxResidentModelsEffective = PerfPolicy.DecideMaxResident(ram, Math.Max(1, modelCount), PerfConfig.MaxResidentModels);
        input.DetectorConcurrencyEffective = PerfPolicy.DecideDetectorConcurrency(ram, 3, PerfConfig.DetectorConcurrency);
        try { input.IntegrityManifestFound = !RuntimeIntegrity.Default.ManifestMissing; } catch (Exception) { }
        SupportTelemetry.CopyInto(input);
        if (project != null)
        {
            try
            {
                foreach (var group in project.Mappings.GroupBy(m => (m.Category ?? "").ToUpperInvariant()))
                    input.CategoryCounts[group.Key] = group.Count();
            }
            catch (Exception) { }
        }
        return input;
    }

    public static string CollectText(Project? project, string appName, int modelCount) => SupportBundle.Build(Collect(project, appName, modelCount));
}

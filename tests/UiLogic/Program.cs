using System.Diagnostics;
using Desensitizer;

// Deterministic tests for the interface logic that was moved out of the windows (stage S2).
// No window is created and no real project, catalog or document is touched. All data is fictional.

if(args.Length==2&&args[0]=="--lock-probe")
{
    using var probe=UiProjectLock.TryAcquire(args[1],out _);
    return probe==null?37:0;
}

var failures = 0;
var total = 0;
void Check(string name, bool passed, string? detail = null)
{
    total++;
    if (!passed) failures++;
    Console.WriteLine((passed ? "PASS " : "FAIL ") + name + (!passed && detail != null ? "  [" + detail + "]" : ""));
}

var work = Path.Combine(Path.GetTempPath(), "UiLogicTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
ProjectCatalog.UseRoot(Path.Combine(work, "app-data"));
var timer = Stopwatch.StartNew();
try
{
    MessageTests.Run(Check, work);
    DecisionTests.Run(Check, work);
    ProgressTests.Run(Check);
    CheckpointTests.Run(Check, work);
    CheckpointRobustnessTests.Run(Check, work);
    AtomicOutputTests.Run(Check, work);
    RehydratePreviewTests.Run(Check, work);
    ContextKeepTests.Run(Check, work);
    WiringTests.Run(Check, work);
    ExportAndSaveTests.Run(Check, work);
    ExportPlanTests.Run(Check, work);
    SaveSafetyTests.Run(Check, work);
    ProjectIntegrityTests.Run(Check, work);
    ProjectLockTests.Run(Check, work);
    Ui2Tests.Run(Check, work);
    TutorialTests.Run(Check, work);
    IssueTests.Run(Check);
    SmokeTests.Run(Check, work);
}
catch (Exception ex)
{
    Check("Test run finished without an unexpected exception (" + ex.GetType().Name + ": " + ex.Message + ")", false);
    Console.WriteLine(ex);
}
finally
{
    ProjectCatalog.UseRoot(null);
    ReviewAllowList.Unbind();
    ProjectScope.Unbind();
    try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}
Console.WriteLine($"{total - failures} passed, {failures} failed, {timer.Elapsed.TotalSeconds:0.0}s");
return failures == 0 ? 0 : 1;
delegate void CheckFn(string name, bool passed, string? detail = null);

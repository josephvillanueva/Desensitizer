using System.Diagnostics;
using Desensitizer;

// Deterministic tests for the document format registry and the .md, .csv, .xlsx and .pptx formats (stage S4).
// Every file is built in test code from fictional values. No real document, project or model is touched.

var failures = 0;
var total = 0;
void Check(string name, bool passed, string? detail = null)
{
    total++;
    if (!passed) failures++;
    Console.WriteLine((passed ? "PASS " : "FAIL ") + name + (!passed && detail != null ? "  [" + detail + "]" : ""));
}

var work = Path.Combine(Path.GetTempPath(), "FormatsTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
var timer = Stopwatch.StartNew();
try
{
    RegistryTests.Run(Check, work);
    TextFormatTests.Run(Check, work);
    XlsxTests.Run(Check, work);
    PptxTests.Run(Check, work);
    HiddenResidueTests.Run(Check, work);
}
catch (Exception ex)
{
    Check("Test run finished without an unexpected exception (" + ex.GetType().Name + ": " + ex.Message + ")", false);
    Console.WriteLine(ex);
}
finally
{
    try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}
Console.WriteLine($"{total - failures} passed, {failures} failed, {timer.Elapsed.TotalSeconds:0.0}s");
return failures == 0 ? 0 : 1;
delegate void CheckFn(string name, bool passed, string? detail = null);

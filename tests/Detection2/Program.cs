using Desensitizer;

// Deterministic tests for the stage S9 detection work: per-document company alias consolidation
// (acronym introduction, legal-suffix variants, unique-stem short forms, never-merge rules) and
// the D10 groundwork (cases that only a model can solve). Fictional values only, no model, no worker.

var failures = 0;
var total = 0;
void Check(string name, bool passed, string? detail = null)
{
    total++;
    if (!passed) failures++;
    Console.WriteLine((passed ? "PASS " : "FAIL ") + name + (!passed && detail != null ? "  [" + detail + "]" : ""));
}

CompanyTests.Run(Check);
PersonProtectionTests.Run(Check);
FragmentGuardTests.Run(Check);
CompanyProtectionTests.Run(Check);
IntroductionProvenanceTests.Run(Check);
ModelNeededTests.Run(Check);
ReviewNoiseTests.Run(Check);
Console.WriteLine($"{total - failures} passed, {failures} failed");
return failures == 0 ? 0 : 1;

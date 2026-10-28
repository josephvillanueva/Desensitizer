using Desensitizer;

static class IssueTests
{
    public static void Run(CheckFn check)
    {
        foreach(var kind in Enum.GetValues<IssueKind>())
        {
            var issue=UiIssues.For(kind);
            check("issue: "+kind+" has a specific explanation and next action",issue.Kind==kind&&issue.Title.Length>8&&issue.Explanation.Length>20&&issue.NextStep.Length>25&&issue.Button.Length>5);
            check("issue: "+kind+" copy follows writing rule",!(issue.Title+issue.Explanation+issue.NextStep+issue.Button).Contains('\u2014'));
        }
        check("issue: zero Review does not bypass incomplete checks",UiIssues.For(IssueKind.DeepRequired).Explanation.Contains("Zero Review"));
        check("issue: context mismatch leads to support, not an endless retry",UiIssues.Failure(IssueKind.DeepIncomplete,warning:"The review section does not match its table context.").Action==IssueAction.Support);
        check("issue: missing source leads to an accessible copy",UiIssues.Failure(IssueKind.ScanIncomplete,new FileNotFoundException("private sentinel")).Kind==IssueKind.MissingFile);
        check("issue: missing output is not mislabeled as a source error",UiIssues.Failure(IssueKind.ExportFailed,new DirectoryNotFoundException("private sentinel")).Kind==IssueKind.ExportFailed);
        check("issue: missing model leads to installation recovery",UiIssues.Failure(IssueKind.DeepIncomplete,warning:"Model file not found").Kind==IssueKind.ModelUnavailable);
        check("issue: arbitrary diagnostics are never copied into popup",!string.Join(" ",UiIssues.Failure(IssueKind.ExportFailed,new Exception("private sentinel"))).Contains("private sentinel"));
        check("issue: residual findings open Review, not auto Replace",UiIssues.For(IssueKind.ResidualFindings).Action==IssueAction.ReviewFindings);
        check("issue: no replacements opens mappings, not auto Replace",UiIssues.For(IssueKind.NoReplacements).Action==IssueAction.Review);
        check("issue: recovery never advises disabling safety checks",UiIssues.For(IssueKind.ModelUnavailable).NextStep.Contains("Never disable safety checks"));
        check("issue: ready export help does not invent residual findings",UiIssues.For(IssueKind.ReadyToExport).Action==IssueAction.Export&&!UiIssues.For(IssueKind.ReadyToExport).Explanation.Contains("No file"));
        foreach(var channel in new[]{"encoded or hidden package content", "hidden or encoded package content", "a package part name"})
        {
            var hidden=UiIssues.Failure(IssueKind.ExportFailed,new InvalidDataException("Export blocked: an identifying value remains in "+channel+". private sentinel"));
            check("issue: hidden content goes to support instead of retry",hidden.Kind==IssueKind.HiddenContent&&hidden.Action==IssueAction.Support);
            check("issue: hidden content explanation never repeats private diagnostics",!string.Join(" ",hidden).Contains("private sentinel"));
        }
        check("issue: reconstructing formulas do not trigger a blind retry",UiIssues.Failure(IssueKind.ExportFailed,new InvalidDataException("Export blocked: a formula can reconstruct an identifying value. private sentinel")).Action==IssueAction.Support);
        check("issue: split headers do not trigger a blind retry",UiIssues.Failure(IssueKind.ExportFailed,new InvalidDataException("Export blocked: an identifying value remains across header or footer formatting.")).Action==IssueAction.Support);
    }
}

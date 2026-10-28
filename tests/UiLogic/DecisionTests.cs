using System.Windows.Input;
using Desensitizer;

static class DecisionTests
{
    public static void Run(CheckFn Check, string work)
    {
        // ---- the upgrade gate ------------------------------------------------------------------
        var gate = new UiUpgradeGate();
        Check("gate: a current-format project saves without a question", gate.Decide(ProjectFormat.Dsp2, false) == UiSaveAction.Save && gate.Decide(ProjectFormat.Dsp2, true) == UiSaveAction.Save);
        Check("gate: an older project asks before the first save", gate.Decide(ProjectFormat.Dsp1, false) == UiSaveAction.AskUpgrade);
        gate.Decline();
        Check("gate: after Cancel an automatic save is skipped quietly (the project stays unsaved)", gate.Decide(ProjectFormat.Dsp1, false) == UiSaveAction.SkipQuietly);
        Check("gate: after Cancel an explicit Save asks again", gate.Decide(ProjectFormat.Dsp1, true) == UiSaveAction.AskUpgrade);
        gate.Approve();
        Check("gate: after OK every save proceeds", gate.Decide(ProjectFormat.Dsp1, false) == UiSaveAction.Save && gate.Decide(ProjectFormat.Dsp1, true) == UiSaveAction.Save && !gate.Declined);
        gate.Reset();
        Check("gate: reset forgets the answers for the next project", gate.Decide(ProjectFormat.Dsp1, false) == UiSaveAction.AskUpgrade && !gate.Approved);
        Check("gate: an unknown format is never treated as an upgrade", gate.Decide(ProjectFormat.Unknown, false) == UiSaveAction.Save);

        // ---- keep as-is and remember ------------------------------------------------------------
        var list = new ReviewAllowList(null);
        var mappings = new[]
        {
            new Mapping { OriginalValue = "Boarding Pass", Category = "PRODUCT" },
            new Mapping { OriginalValue = "Orion Portal", Category = "SYSTEM" },
            new Mapping { OriginalValue = "Mary Santos", Category = "PERSON" },
            new Mapping { OriginalValue = "Acme Holdings", Category = "ORGANIZATION" },
            new Mapping { OriginalValue = "Build 4410", Category = "PROJECT" }
        };
        var outcome = UiKeepRemember.Apply(mappings, list);
        Check("remember: project, system and product labels are added", outcome.Remembered == 2 && list.Contains("Boarding Pass", "PRODUCT") && list.Contains("Orion Portal", "SYSTEM"));
        Check("remember: people and companies are never remembered", !list.Contains("Mary Santos") && !list.Contains("Acme Holdings") && outcome.Skipped == 3);
        Check("remember: a label with digits is refused", !list.Contains("Build 4410"));
        Check("remember: CanRemember mirrors the project rules", UiKeepRemember.CanRemember("Boarding Pass", "product") && !UiKeepRemember.CanRemember("Mary Santos", "PERSON") && !UiKeepRemember.CanRemember("x", "PRODUCT") && !UiKeepRemember.CanRemember("Boarding Pass", null));
        Check("remember: the reason names the category rule", UiKeepRemember.WhyNot("Mary Santos", "PERSON").Contains("never remembered"));
        var describe = UiKeepRemember.Describe(outcome, "5 mappings set to Keep as-is.");
        Check("remember: the status says what was and was not remembered", describe.Contains("2 labels were remembered") && describe.Contains("3 could not be remembered"));
        Check("remember: the status stays short when everything was remembered", UiKeepRemember.Describe(new UiKeepRemember.Outcome { Remembered = 1 }, "1 mapping set to Keep as-is.") == "1 mapping set to Keep as-is. 1 label was remembered for this project.");

        // Through the project preferences: remembered per project and removable.
        var preferences = new ProjectReviewPreferences();
        var bound = ReviewAllowList.Bind(preferences);
        UiKeepRemember.Apply(new[] { mappings[0] }, ReviewAllowList.Current);
        Check("remember: the bound list writes into the open project's preferences", preferences.KeepAsIsSnapshot().Count == 1 && bound.IsProjectBound);
        ReviewAllowList.Unbind();
        Check("remember: after Unbind no label applies", !ReviewAllowList.Current.Contains("Boarding Pass", "PRODUCT"));

        // ---- neutral labels -----------------------------------------------------------------------
        var others = new[] { "Project 1", "Quarterly review" };
        Check("label: a plain label is accepted", UiProjectLabel.Validate("Team workspace", others) == null);
        Check("label: empty is refused", UiProjectLabel.Validate("   ", others) != null);
        Check("label: too long is refused", UiProjectLabel.Validate(new string('a', 41), others) != null && UiProjectLabel.Validate(new string('a', 40), others) == null);
        Check("label: a duplicate is refused ignoring case", UiProjectLabel.Validate("quarterly REVIEW", others) != null);
        Check("label: control characters are refused", UiProjectLabel.Validate("a\tb", others) != null);
        Check("label: the warning says it is stored without encryption", UiMessages.NeutralLabelWarning.Contains("without encryption"));

        // ---- keys while busy (item U7) ---------------------------------------------------------------
        Check("busy keys: Tab and arrows still work", UiBusyInput.IsAllowed(Key.Tab, ModifierKeys.None, false) && UiBusyInput.IsAllowed(Key.Down, ModifierKeys.None, false) && UiBusyInput.IsAllowed(Key.PageDown, ModifierKeys.None, false));
        Check("busy keys: Alt and the system menu still work", UiBusyInput.IsAllowed(Key.System, ModifierKeys.Alt, false) && UiBusyInput.IsAllowed(Key.F4, ModifierKeys.Alt, false));
        Check("busy keys: copy still works", UiBusyInput.IsAllowed(Key.C, ModifierKeys.Control, false));
        Check("busy keys: typing, Delete and Enter are blocked", !UiBusyInput.IsAllowed(Key.A, ModifierKeys.None, false) && !UiBusyInput.IsAllowed(Key.Delete, ModifierKeys.None, false) && !UiBusyInput.IsAllowed(Key.Enter, ModifierKeys.None, false) && !UiBusyInput.IsAllowed(Key.V, ModifierKeys.Control, false));
        Check("busy keys: Enter and Space work on the Stop button only", UiBusyInput.IsAllowed(Key.Enter, ModifierKeys.None, true) && UiBusyInput.IsAllowed(Key.Space, ModifierKeys.None, true) && UiBusyInput.BlocksActivation(Key.Enter, false) && !UiBusyInput.BlocksActivation(Key.Enter, true) && !UiBusyInput.BlocksActivation(Key.Tab, false));

        // ---- save debounce (item U4) ---------------------------------------------------------------
        var scheduler = new UiSaveScheduler(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        var t0 = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        Check("debounce: nothing to save when nothing changed", !scheduler.IsDirty && !scheduler.ShouldSave(t0));
        scheduler.MarkDirty(t0);
        Check("debounce: a fresh change waits for the quiet period", !scheduler.ShouldSave(t0.AddSeconds(1)));
        Check("debounce: saves after the quiet period", scheduler.ShouldSave(t0.AddSeconds(2)));
        scheduler.MarkDirty(t0.AddSeconds(1.5));
        Check("debounce: another change restarts the quiet period", !scheduler.ShouldSave(t0.AddSeconds(3)) && scheduler.ShouldSave(t0.AddSeconds(3.6)));
        scheduler.MarkSaved();
        var busy = new UiSaveScheduler(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        for (var i = 0; i <= 9; i++) busy.MarkDirty(t0.AddSeconds(i));
        Check("debounce: constant changes do not save before the maximum wait", !busy.ShouldSave(t0.AddSeconds(9.5)));
        Check("debounce: constant changes still save at the maximum wait", busy.ShouldSave(t0.AddSeconds(10)));
        scheduler.MarkDirty(t0);
        scheduler.MarkSaved();
        Check("debounce: saving clears the pending change", !scheduler.IsDirty && !scheduler.ShouldSave(t0.AddMinutes(5)));
    }
}

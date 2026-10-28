using System;
using System.Collections.Generic;
using System.Linq;

namespace Desensitizer;

// The "About N sections x R reviewers, roughly X" line shown before and during a deep check or export (item P6
// wired into the window). One estimator lives for the whole session, so its PerfTimingTracker keeps what earlier
// checks measured and the second estimate no longer says "may differ". Only numbers are kept, never any text.
public sealed class UiScanEstimator
{
    private readonly Func<DateTime> clock;
    private int reviewers = 1;
    private int sectionCount;
    private string? lastReviewer;
    private int lastSection;
    private DateTime lastAdvance;
    private bool active;

    public UiScanEstimator(PerfTimingTracker? tracker = null, Func<DateTime>? clock = null)
    {
        Tracker = tracker ?? new PerfTimingTracker();
        this.clock = clock ?? (() => DateTime.UtcNow);
    }

    public PerfTimingTracker Tracker { get; }
    public string Text { get; private set; } = "";

    // The reviewer sections of a check: the same split the local AI reviewers use.
    public static IReadOnlyList<int> SectionSizes(string? text) => string.IsNullOrWhiteSpace(text) ? Array.Empty<int>() : DeepReview.Sections(text, 1000, 150).Select(s => s.Length).ToList();

    // Called before a check starts (and again when its text is known).
    public string Begin(IReadOnlyList<int> sectionSizes, int reviewerCount)
    {
        reviewers = Math.Max(1, reviewerCount);
        sectionCount = sectionSizes.Count;
        lastReviewer = null; lastSection = 0; active = true;
        Text = PerfEstimate.FromSections(sectionSizes, reviewers, Tracker).Text;
        return Text;
    }

    public void End() { active = false; lastReviewer = null; lastSection = 0; }

    // A progress report from an engine. Returns the new text when a reviewer section finished and was timed, else null.
    // The first section of each reviewer is not timed: it includes loading the model and would make every estimate too slow.
    public string? Report(string? name, string? detail)
    {
        if (!active) return null;
        if (!UiProgressTracker.TryParseReviewer(name, detail, out var reviewer, out var section, out _) || section <= 0) return null;
        var now = clock();
        if (!string.Equals(reviewer, lastReviewer, StringComparison.OrdinalIgnoreCase)) { lastReviewer = reviewer; lastSection = section; lastAdvance = now; return null; }
        string? updated = null;
        if (section > lastSection)
        {
            // The section that just finished is the one before the reported one. The first section of a reviewer is skipped (model load).
            if (lastSection >= 2) { Tracker.Add((now - lastAdvance).TotalSeconds, section - lastSection); updated = Recompute(); }
            lastSection = section; lastAdvance = now;
        }
        else if (section < lastSection) { lastSection = section; lastAdvance = now; }
        return updated;
    }

    private string Recompute()
    {
        Text = PerfEstimate.FromSections(Enumerable.Repeat(1000, Math.Max(0, sectionCount)).ToList(), reviewers, Tracker).Text;
        return Text;
    }

    // The sentence for the window ("About 14 sections x 3 reviewers, roughly 20 min (first estimate, may differ)").
    public static string ForDisplay(string text) => text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text.Substring(1);
}

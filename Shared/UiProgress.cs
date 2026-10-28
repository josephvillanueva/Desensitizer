using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer;

public enum UiRowState { Waiting, Running, Done }

// One line in the progress overlay: a reviewer and where it is ("section 5 of 20").
public sealed class UiProgressRow : INotifyPropertyChanged
{
    private UiRowState state = UiRowState.Waiting;
    private int section;
    private int sectionCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public UiProgressRow(string key, string title) { Key = key; Title = title; }

    public string Key { get; }
    public string Title { get; }
    public UiRowState State { get => state; set { if (state == value) return; state = value; Raise(nameof(State)); Raise(nameof(StateText)); Raise(nameof(Percent)); Raise(nameof(IsRunning)); } }
    public int Section { get => section; set { if (section == value) return; section = value; Raise(nameof(Section)); Raise(nameof(StateText)); Raise(nameof(Percent)); } }
    public int SectionCount { get => sectionCount; set { if (sectionCount == value) return; sectionCount = value; Raise(nameof(SectionCount)); Raise(nameof(StateText)); Raise(nameof(Percent)); } }
    public bool IsRunning => state == UiRowState.Running;

    // 0 to 100 for a progress bar.
    public double Percent => state == UiRowState.Done ? 100 : state == UiRowState.Waiting || sectionCount <= 0 ? 0 : Math.Clamp(100.0 * Math.Max(0, section - 1) / sectionCount, 0, 100);

    public string StateText => state switch
    {
        UiRowState.Done => "Done",
        UiRowState.Waiting => "Waiting",
        _ => sectionCount > 0 ? "Section " + section + " of " + sectionCount : "Working"
    };

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Turns the engines' progress reports into per-reviewer rows, an elapsed time, a rolling average estimate
// of the time left and a "loading the model" notice. It never touches the window, so it is tested directly.
public sealed class UiProgressTracker
{
    private const int SampleWindow = 10;
    private const int MinimumSamples = 3;
    private static readonly TimeSpan ModelLoadNoticeDelay = TimeSpan.FromSeconds(4);
    private static readonly Regex ReviewerName = new(@"^AI reviewer (?<n>\d+) of (?<of>\d+): (?<name>.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex SectionDetail = new(@"[Ss]ection (?<n>\d+) of (?<of>\d+)", RegexOptions.CultureInvariant);

    private readonly Func<DateTime> clock;
    private readonly List<string> expected;
    private readonly Queue<double> samples = new();
    private DateTime started;
    private DateTime lastReport;
    private DateTime lastAdvance;
    private int lastStep;
    private int lastTotal;
    private bool running;
    private UiProgressRow? current;

    public ObservableCollection<UiProgressRow> Rows { get; } = new();
    public int Step => lastStep;
    public int Total => lastTotal;

    // expectedReviewers: names shown as "Waiting" before they start (the AI app lists all three).
    public UiProgressTracker(Func<DateTime>? clock = null, IEnumerable<string>? expectedReviewers = null)
    {
        this.clock = clock ?? (() => DateTime.UtcNow);
        expected = (expectedReviewers ?? Array.Empty<string>()).ToList();
    }

    public void Start()
    {
        started = lastReport = lastAdvance = clock();
        lastStep = 0; lastTotal = 0; running = true; current = null;
        samples.Clear();
        Rows.Clear();
        foreach (var name in expected) Rows.Add(new UiProgressRow(name, name));
    }

    public void Finish()
    {
        running = false;
        foreach (var row in Rows.Where(r => r.State == UiRowState.Running)) row.State = UiRowState.Done;
        current = null;
    }

    // Recognizes a reviewer report. "AI reviewer 2 of 3: NuExtract" and "Local AI review" are reviewers; detectors are not.
    public static bool TryParseReviewer(string? name, string? detail, out string reviewer, out int section, out int sectionCount)
    {
        reviewer = ""; section = 0; sectionCount = 0;
        if (string.IsNullOrEmpty(name)) return false;
        var named = ReviewerName.Match(name);
        if (named.Success) reviewer = named.Groups["name"].Value.Trim();
        else if (name == "Local AI review") reviewer = "Qwen";
        else return false;
        var section1 = SectionDetail.Match(detail ?? "");
        if (section1.Success)
        {
            section = int.Parse(section1.Groups["n"].Value);
            sectionCount = int.Parse(section1.Groups["of"].Value);
        }
        return true;
    }

    public void Report(string? name, int step, int total, string? detail)
    {
        if (!running) return;
        var now = clock();
        lastReport = now;
        if (total != lastTotal)
        {
            // A new stage with a different size runs at a different speed: do not mix the samples.
            samples.Clear();
            lastStep = Math.Max(0, Math.Min(step, total));
            lastTotal = total;
            lastAdvance = now;
        }
        else if (step < lastStep)
        {
            lastStep = step;
            lastAdvance = now;
        }
        else if (step > lastStep)
        {
            var units = step - lastStep;
            samples.Enqueue((now - lastAdvance).TotalSeconds / units);
            while (samples.Count > SampleWindow) samples.Dequeue();
            lastStep = step;
            lastAdvance = now;
        }
        if (TryParseReviewer(name, detail, out var reviewer, out var section, out var sectionCount))
        {
            var row = Rows.FirstOrDefault(r => string.Equals(r.Key, reviewer, StringComparison.OrdinalIgnoreCase));
            if (row == null) { row = new UiProgressRow(reviewer, reviewer); Rows.Add(row); }
            if (!ReferenceEquals(current, row))
            {
                foreach (var other in Rows.Where(r => !ReferenceEquals(r, row) && r.State == UiRowState.Running)) other.State = UiRowState.Done;
                current = row;
            }
            row.State = UiRowState.Running;
            if (sectionCount > 0) { row.SectionCount = sectionCount; row.Section = section; }
        }
    }

    public TimeSpan Elapsed => started == default ? TimeSpan.Zero : (running ? clock() : lastReport) - started;

    public string ElapsedText => "Elapsed " + FormatElapsed(Elapsed);

    // Null until enough sections have finished to make a fair estimate.
    public string? EtaText
    {
        get
        {
            if (!running || samples.Count < MinimumSamples || lastTotal <= lastStep) return null;
            var seconds = samples.Average() * (lastTotal - lastStep);
            return FormatEta(TimeSpan.FromSeconds(seconds));
        }
    }

    // "Elapsed 2:13  |  About 4 minutes left" style line for the overlay.
    public string TimeText
    {
        get
        {
            var eta = EtaText;
            return eta == null ? ElapsedText : ElapsedText + "  |  " + eta;
        }
    }

    // A reviewer is on its first section and nothing has moved for a few seconds: the model is probably loading.
    public string? ModelLoadNotice
    {
        get
        {
            if (!running || current == null || current.State != UiRowState.Running || current.Section > 1) return null;
            if (clock() - lastAdvance < ModelLoadNoticeDelay) return null;
            return "Loading the " + current.Title + " model on this computer. The first section is slow while it loads, and later sections are faster.";
        }
    }

    public static string FormatElapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1 ? ((int)span.TotalHours) + ":" + span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00")
                                    : span.Minutes + ":" + span.Seconds.ToString("00");
    }

    public static string FormatEta(TimeSpan remaining)
    {
        if (remaining.TotalSeconds < 45) return "Less than a minute left";
        if (remaining.TotalMinutes < 1.5) return "About 1 minute left";
        if (remaining.TotalMinutes < 60) return "About " + (int)Math.Round(remaining.TotalMinutes) + " minutes left";
        var hours = (int)remaining.TotalHours;
        var minutes = (int)Math.Round(remaining.TotalMinutes - hours * 60);
        if (minutes == 60) { hours++; minutes = 0; }
        return "About " + hours + " h " + minutes + " min left";
    }
}

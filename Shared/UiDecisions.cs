using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace Desensitizer;

// Small decisions the windows make, kept here so they can be tested without a window.

public enum UiSaveAction
{
    // Write the project now.
    Save,
    // The project is in an older format: show the upgrade notice and ask before the first save.
    AskUpgrade,
    // The user declined the upgrade and this save was not requested by them: do nothing quietly.
    SkipQuietly
}

// Tracks the answer to the one-time upgrade question for the open project (item P1).
public sealed class UiUpgradeGate
{
    private bool approved;
    private bool declined;

    public bool Approved => approved;
    public bool Declined => declined;

    public void Reset() { approved = false; declined = false; }
    public void Approve() { approved = true; declined = false; }
    public void Decline() { declined = true; }

    // userRequested: the user pressed Save, renamed the project or changed its passphrase. Automatic saves are not.
    public UiSaveAction Decide(ProjectFormat format, bool userRequested)
    {
        if (format != ProjectFormat.Dsp1 || approved) return UiSaveAction.Save;
        if (userRequested || !declined) return UiSaveAction.AskUpgrade;
        return UiSaveAction.SkipQuietly;
    }
}

// Remembering a label as "keep as-is" for the project (items P3/U6). Only labels the project preferences accept.
public static class UiKeepRemember
{
    public sealed class Outcome
    {
        public int Remembered { get; set; }
        public List<string> NotRemembered { get; } = new();
        public int Skipped => NotRemembered.Count;
    }

    public static bool CanRemember(string? value, string? category) =>
        !string.IsNullOrWhiteSpace(category)
        && ProjectReviewPreferences.KeepAsIsCategories.Contains(category!.Trim().ToUpperInvariant())
        && ProjectReviewPreferences.IsAcceptableLabel(value);

    public static string WhyNot(string? value, string? category)
    {
        if (string.IsNullOrWhiteSpace(category) || !ProjectReviewPreferences.KeepAsIsCategories.Contains(category.Trim().ToUpperInvariant()))
            return "Only project, system and product labels can be remembered. People, companies, contacts, places and identifiers are never remembered by spelling.";
        return "This value cannot be remembered as a label (it is too short or long, or contains digits, an address or a token).";
    }

    // Adds each rememberable mapping to the list. Mappings that are not rememberable are counted, not changed.
    public static Outcome Apply(IEnumerable<Mapping> mappings, ReviewAllowList list)
    {
        var outcome = new Outcome();
        foreach (var mapping in mappings)
        {
            if (CanRemember(mapping.OriginalValue, mapping.Category) && list.Add(mapping.OriginalValue, new[] { mapping.Category.ToUpperInvariant() }, null)) outcome.Remembered++;
            else outcome.NotRemembered.Add(mapping.Category);
        }
        return outcome;
    }

    public static string Describe(Outcome outcome, string decisionText)
    {
        var text = decisionText;
        if (outcome.Remembered > 0) text += " " + outcome.Remembered + " label" + (outcome.Remembered == 1 ? " was" : "s were") + " remembered for this project.";
        if (outcome.Skipped > 0) text += " " + outcome.Skipped + " could not be remembered (only project, system and product labels can be); " + (outcome.Skipped == 1 ? "it was" : "they were") + " kept for this document only.";
        return text;
    }
}

// The neutral label shown in the project list before a project is unlocked.
public static class UiProjectLabel
{
    public const int MaxLength = 40;

    // Returns null when acceptable, otherwise a sentence for the user.
    public static string? Validate(string? label, IEnumerable<string> otherLabels)
    {
        var text = (label ?? "").Trim();
        if (text.Length == 0) return "Enter a label.";
        if (text.Length > MaxLength) return "Use " + MaxLength + " characters or fewer.";
        if (text.Any(char.IsControl)) return "The label cannot contain control characters.";
        if (otherLabels.Any(other => string.Equals((other ?? "").Trim(), text, StringComparison.OrdinalIgnoreCase))) return "Another project already uses this label.";
        return null;
    }
}

// Which keys stay usable while a check is running (item U7). Everything that would start work or edit
// data stays blocked. Navigation, copying and the system menu keep working.
public static class UiBusyInput
{
    public static bool IsAllowed(Key key, ModifierKeys modifiers, bool stopButtonHasFocus)
    {
        if (key == Key.System) return true; // Alt based system menu, Alt+F4 goes through Closing.
        if (stopButtonHasFocus && (key == Key.Enter || key == Key.Space)) return true;
        if (modifiers.HasFlag(ModifierKeys.Alt)) return true;
        if (modifiers == ModifierKeys.Control && (key == Key.C || key == Key.Insert)) return true;
        return key is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End
            or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LWin or Key.RWin or Key.F1;
    }

    // Plain activation keys must not trigger a button other than Stop while busy.
    public static bool BlocksActivation(Key key, bool stopButtonHasFocus) =>
        (key == Key.Enter || key == Key.Space) && !stopButtonHasFocus;
}

// Image placeholder settings (item U5): turns the saved settings into the engine's options and reports a problem
// in plain words before anything is exported.
public static class UiImageSettings
{
    public static DocumentProcessingOptions Build(AppSettings settings) => Build(settings.ImagePlaceholder, settings.ImagePlaceholderIncludeSize, settings.ImagePlaceholderIncludeAltText);

    // Throws ArgumentException with a readable message when the options are not valid.
    public static DocumentProcessingOptions Build(string? placeholder, bool includeSize, bool includeAltText)
    {
        var text = string.IsNullOrWhiteSpace(placeholder) ? null : placeholder.Trim();
        var options = new DocumentProcessingOptions { ImagePlaceholder = text, IncludeImageSize = includeSize, IncludeImageAltText = includeAltText };
        options.Validate();
        return options;
    }

    // null when the settings are acceptable, otherwise the sentence to show.
    public static string? Problem(string? placeholder, bool includeSize, bool includeAltText)
    {
        try { Build(placeholder, includeSize, includeAltText); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }

    public static string Describe(AppSettings settings)
    {
        var options = Build(settings);
        if (!options.PlaceholdersEnabled) return "Pictures are removed without a placeholder.";
        return "Each picture is replaced by " + options.ImagePlaceholder + (options.IncludeImageSize ? " with its size" : "") + (options.IncludeImageAltText ? " and its description" : "") + ".";
    }
}

public enum UiShortcut { None, OpenProject, NewProject, ScanSelected, Replace, Keep, Review }

// Keyboard shortcuts of the main window (item U9). They only fire when nothing is running and the user is
// not typing in a text box, so ordinary typing is never taken over.
public static class UiShortcuts
{
    public static UiShortcut Decide(Key key, ModifierKeys modifiers, bool busy, bool hasProject, bool mappingGridFocused, bool typingInText)
    {
        if (busy || typingInText) return UiShortcut.None;
        if (modifiers == ModifierKeys.Control)
        {
            if (key == Key.O) return UiShortcut.OpenProject;
            if (key == Key.N && !hasProject) return UiShortcut.NewProject;
            return UiShortcut.None;
        }
        if (modifiers != ModifierKeys.None) return UiShortcut.None;
        if (key == Key.F5 && hasProject) return UiShortcut.ScanSelected;
        if (mappingGridFocused)
        {
            if (key == Key.R) return UiShortcut.Replace;
            if (key == Key.K) return UiShortcut.Keep;
            if (key == Key.V) return UiShortcut.Review;
        }
        return UiShortcut.None;
    }
}

// Decides when a changed project is written (item U4): at most once per interval while changes keep
// arriving, and at the latest maxWait after the first unsaved change.
public sealed class UiSaveScheduler
{
    private readonly TimeSpan quiet;
    private readonly TimeSpan maxWait;
    private DateTime? firstDirty;
    private DateTime lastDirty;

    public UiSaveScheduler(TimeSpan quiet, TimeSpan maxWait)
    {
        this.quiet = quiet;
        this.maxWait = maxWait < quiet ? quiet : maxWait;
    }

    public bool IsDirty => firstDirty.HasValue;

    public void MarkDirty(DateTime now)
    {
        firstDirty ??= now;
        lastDirty = now;
    }

    public bool ShouldSave(DateTime now) =>
        firstDirty.HasValue && (now - lastDirty >= quiet || now - firstDirty.Value >= maxWait);

    public void MarkSaved() { firstDirty = null; }
}

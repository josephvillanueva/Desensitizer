using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Message text and small decisions for the interface, kept free of window code so they can be tested.
// No text here uses a dash as punctuation.
public static class UiMessages
{
    // Shown, with an explicit OK, before the first save turns an older project into the stronger format.
    public const string UpgradeNotice =
        "This project will be upgraded to a stronger format. Older app versions will not be able to open it. " +
        "A backup copy (.bak) is kept until the upgrade is verified.";

    public const string UpgradeDeclinedStatus =
        "The project was opened without upgrading. Changes are not saved until you choose Save project and accept the upgrade.";

    public const string PassphraseRuleSummary =
        "Use at least 12 characters. Mix at least two kinds of characters (letters, digits, symbols or spaces) or use 20 or more characters, " +
        "avoid repeated or sequential characters, and do not use the project name.";

    public const string NeutralLabelWarning =
        "This label is stored without encryption on this computer so the project list can show it before you unlock a project. " +
        "Do not use client names or other identifying words.";

    // One line per problem, for a dialog that lists everything that has to change.
    public static IReadOnlyList<string> PassphraseProblemLines(PassphraseCheck check)
    {
        if (check == null || check.IsAcceptable) return Array.Empty<string>();
        return check.Problems.Select(ProblemLine).ToList();
    }

    public static string ProblemLine(PassphraseProblem problem) => problem switch
    {
        PassphraseProblem.TooShort => "Use at least " + PassphrasePolicy.MinimumLength + " characters.",
        PassphraseProblem.TooFewDistinctCharacters => "Use more different characters.",
        PassphraseProblem.TooSimple => "Mix at least two kinds of characters (letters, digits, symbols or spaces), or use 20 or more characters.",
        PassphraseProblem.CommonPassphrase => "This passphrase is too common. Choose something less predictable.",
        PassphraseProblem.RepeatedOrSequential => "Avoid repeated or sequential characters such as aaaa or 1234.",
        PassphraseProblem.ContainsProjectName => "Do not build the passphrase from the project name.",
        _ => "Choose a stronger passphrase."
    };

    // The text of the dialog shown when a new passphrase is refused. Lists all problems at once.
    public static string PassphraseRefusal(PassphraseCheck check)
    {
        var lines = PassphraseProblemLines(check);
        if (lines.Count == 0) return "";
        return "This passphrase is not strong enough:\n\n" + string.Join("\n", lines.Select(l => "• " + l)) +
               "\n\nThe passphrase cannot be recovered if it is lost, so choose one you can keep safe.";
    }

    // Validates the fields of the create dialog. Returns null when everything is fine, otherwise the text to show.
    public static string? ValidateNewProject(string name, string passphrase, string confirmation)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a project name.";
        var check = PassphrasePolicy.Check(passphrase, name.Trim());
        if (!check.IsAcceptable) return PassphraseRefusal(check);
        if (!string.Equals(passphrase, confirmation, StringComparison.Ordinal)) return "The two passphrases do not match.";
        return null;
    }

    // Validates the fields of the change dialog.
    public static string? ValidatePassphraseChange(string current, string next, string confirmation, string projectName)
    {
        if (current.Length == 0) return "Enter the current passphrase.";
        var check = PassphrasePolicy.Check(next, projectName);
        if (!check.IsAcceptable) return PassphraseRefusal(check);
        if (!string.Equals(next, confirmation, StringComparison.Ordinal)) return "The new passphrases do not match.";
        if (string.Equals(next, current, StringComparison.Ordinal)) return "Choose a different new passphrase.";
        return null;
    }

    public static string LegacyImportPrompt(int count) =>
        "An earlier version kept a shared list of " + count + " label" + (count == 1 ? "" : "s") +
        " that the app should leave as-is. Lists are now saved inside each project.\n\n" +
        "Import " + (count == 1 ? "it" : "them") + " into this project? Only project, system and product labels are kept, and you can remove any of them later in Review lists.\n\n" +
        "Choose No to skip. You will not be asked again for this project.";

    public static string LegacyImportResult(int imported, int offered) =>
        imported == offered ? imported + " label" + (imported == 1 ? "" : "s") + " imported into this project."
                            : imported + " of " + offered + " labels imported. The others were not acceptable as labels.";

    // The tracked changes and comments banner. Empty when the file has no review markup.
    public static string ReviewMarkupBanner(ReviewMarkupSummary? summary)
    {
        if (summary == null || !summary.HasReviewMarkup) return "";
        var changes = summary.Insertions + summary.Deletions + summary.Moves + summary.ParagraphMarkChanges + summary.FormattingChanges + summary.TableCellChanges;
        var comments = summary.CommentsRemoved;
        return "This file contains " + changes + " tracked change" + (changes == 1 ? "" : "s") + " and " + comments + " comment" + (comments == 1 ? "" : "s") + ". " +
               summary.Describe();
    }

    // Shown after an export is stopped because an approved replacement could not be applied (item G3).
    public sealed class UnappliedReplacements
    {
        public int Count { get; init; }
        // Category and count only, such as "ORGANIZATION: 2, PERSON: 1". Never a value from the document.
        public string Categories { get; init; } = "";
    }

    private static readonly Regex UnappliedPattern = new(
        @"^Export blocked: (?<count>\d+) approved replacements? \((?<cats>[^)]*)\) would not be applied",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    // Recognizes the exception DocumentProcessor.Desensitize throws when an approved replacement was dropped.
    public static bool TryParseUnapplied(Exception? error, out UnappliedReplacements result)
    {
        result = new UnappliedReplacements();
        if (error is not InvalidDataException) return false;
        var match = UnappliedPattern.Match(error.Message ?? "");
        if (!match.Success) return false;
        result = new UnappliedReplacements { Count = int.Parse(match.Groups["count"].Value), Categories = match.Groups["cats"].Value };
        return true;
    }

    public static string UnappliedExplanation(UnappliedReplacements found) =>
        found.Count + " approved replacement" + (found.Count == 1 ? " was" : "s were") + " not applied (" + found.Categories + "). " +
        "The privacy policy treats " + (found.Count == 1 ? "it" : "them") + " as a common word or label, yet " + (found.Count == 1 ? "it appears" : "they appear") + " in this document.\n\n" +
        "Fix the list: go back to the mapping review and remove the entry, or add it as a manual replacement.\n" +
        "Export anyway: create the copy without applying " + (found.Count == 1 ? "it" : "them") + ". The value stays visible in the exported file.";

    public static string RehydrateSummary(int unknownTokens, string fileName) =>
        unknownTokens == 0
            ? "Rehydrated copy exported: " + fileName
            : "Exported with " + unknownTokens + " unrecognized token" + (unknownTokens == 1 ? "" : "s") + " left unchanged: " + fileName;

    public static string RehydrateDetail(int unknownTokens) =>
        unknownTokens == 0
            ? "Recognized project tokens were restored. Your rehydrated copy is ready."
            : "The copy was created, but " + unknownTokens + " unrecognized token" + (unknownTokens == 1 ? " was" : "s were") +
              " left unchanged. They may belong to another project or to text the AI added. Review the file before using it.";

    // Older projects saved file status text with a dash as punctuation. Show it with a colon instead.
    public static string PlainStatus(string? status) =>
        (status ?? "").Replace(" " + (char)0x2014 + " ", ": ").Replace(" " + (char)0x2013 + " ", ": ");

    // Added to the status after a check when generic lists hid organization or vendor names. Empty when none were hidden.
    public static string SuppressedNotice(int count) =>
        count <= 0 ? "" : " " + count + " organization or vendor name" + (count == 1 ? " was" : "s were") + " hidden by generic lists. Open Review lists to check " + (count == 1 ? "it." : "them.");

    public static string SuppressedOrganizationsIntro =>
        "These organization and vendor names were hidden from the findings because a generic term or built-in list treated them as ordinary words. " +
        "Check that none of them is a real organization. Remove the generic term in Review lists to see it again.";
}

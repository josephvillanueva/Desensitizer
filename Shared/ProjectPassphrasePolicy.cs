using System;
using System.Collections.Generic;
using System.Linq;

namespace Desensitizer;

// Rules for a NEW project passphrase (creation and change). They are never applied when
// opening an existing project, so older 8-character passphrases keep working.
public enum PassphraseProblem
{
    TooShort,
    TooFewDistinctCharacters,
    TooSimple,
    CommonPassphrase,
    RepeatedOrSequential,
    ContainsProjectName
}

// A readable rejection the interface can show as-is (Message) or turn into a checklist (Problems).
// It derives from ArgumentException so older callers that already catch that type keep working.
public sealed class PassphrasePolicyException : ArgumentException
{
    public IReadOnlyList<PassphraseProblem> Problems { get; }
    public PassphrasePolicyException(IReadOnlyList<PassphraseProblem> problems, string message) : base(message, "passphrase")
    {
        Problems = problems;
    }
}

public sealed class PassphraseCheck
{
    public bool IsAcceptable => Problems.Count == 0;
    public IReadOnlyList<PassphraseProblem> Problems { get; }
    public string Message { get; }
    internal PassphraseCheck(IReadOnlyList<PassphraseProblem> problems, string message)
    {
        Problems = problems;
        Message = message;
    }
}

public static class PassphrasePolicy
{
    public const int MinimumLength = 12;
    private const int MinimumDistinctCharacters = 6;

    // Stored lower case without separators. Intentionally short: the length and variety rules do
    // the real work, this only stops the most predictable choices that happen to pass them.
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "passwordpassword", "password1234", "password12345", "password123456", "passw0rd1234", "letmeinletmein",
        "qwertyuiop12", "qwertyuiop123", "qwertyuiopasdf", "1q2w3e4r5t6y", "iloveyouiloveyou", "administrator1",
        "administrator12", "welcome12345", "welcome123456", "changeme1234", "changeme12345", "desensitizer",
        "desensitizer1", "desensitizer12", "desensitizer123", "desensitizer1234"
    };

    // Returns every problem found, so the interface can show them together.
    public static PassphraseCheck Check(string? passphrase, string? projectName = null)
    {
        var problems = new List<PassphraseProblem>();
        var value = passphrase ?? "";
        if (value.Length < MinimumLength) problems.Add(PassphraseProblem.TooShort);
        var distinct = value.Distinct().Count();
        if (value.Length > 0 && distinct < MinimumDistinctCharacters) problems.Add(PassphraseProblem.TooFewDistinctCharacters);
        var classes = new[] { value.Any(char.IsLower), value.Any(char.IsUpper), value.Any(char.IsDigit), value.Any(c => !char.IsLetterOrDigit(c)) }.Count(x => x);
        if (value.Length >= MinimumLength && classes < 2 && value.Length < 20) problems.Add(PassphraseProblem.TooSimple);
        var folded = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (Common.Contains(folded)) problems.Add(PassphraseProblem.CommonPassphrase);
        if (value.Length >= MinimumLength && IsRepeatedOrSequential(folded)) problems.Add(PassphraseProblem.RepeatedOrSequential);
        if (!string.IsNullOrWhiteSpace(projectName) && projectName!.Trim().Length >= 4
            && folded.Contains(new string(projectName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()) && folded.Length < projectName.Length * 2)
            problems.Add(PassphraseProblem.ContainsProjectName);
        problems = problems.Distinct().ToList();
        return new PassphraseCheck(problems, problems.Count == 0 ? "" : Describe(problems));
    }

    // Throws PassphrasePolicyException with a readable message when the passphrase is not acceptable.
    public static void Validate(string? passphrase, string? projectName = null)
    {
        var check = Check(passphrase, projectName);
        if (!check.IsAcceptable) throw new PassphrasePolicyException(check.Problems, check.Message);
    }

    public static string Describe(IReadOnlyCollection<PassphraseProblem> problems)
    {
        var parts = new List<string>();
        foreach (var problem in problems)
            parts.Add(problem switch
            {
                PassphraseProblem.TooShort => "Use at least " + MinimumLength + " characters.",
                PassphraseProblem.TooFewDistinctCharacters => "Use more different characters.",
                PassphraseProblem.TooSimple => "Mix at least two kinds of characters (letters, digits, symbols or spaces), or use 20 or more characters.",
                PassphraseProblem.CommonPassphrase => "This passphrase is too common. Choose something less predictable.",
                PassphraseProblem.RepeatedOrSequential => "Avoid repeated or sequential characters such as aaaa or 1234.",
                PassphraseProblem.ContainsProjectName => "Do not build the passphrase from the project name.",
                _ => "Choose a stronger passphrase."
            });
        return "This passphrase is not strong enough. " + string.Join(" ", parts);
    }

    // A run of one character, a short repeating unit, or a mostly ascending or descending sequence.
    private static bool IsRepeatedOrSequential(string folded)
    {
        if (folded.Length < 8) return false;
        for (var unit = 1; unit <= 4; unit++)
        {
            var repeats = true;
            for (var i = unit; i < folded.Length && repeats; i++) repeats = folded[i] == folded[i - unit];
            if (repeats) return true;
        }
        var steps = 0;
        for (var i = 1; i < folded.Length; i++)
            if (Math.Abs(folded[i] - folded[i - 1]) == 1) steps++;
        return steps >= folded.Length - 2;
    }
}

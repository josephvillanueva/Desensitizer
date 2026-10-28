using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SyntheticBenchmark;

// Ground truth for one generated document. Every value is fictional.
public sealed class SensitiveSpan
{
    public string Value { get; set; } = "";
    public string Category { get; set; } = "";
    // Alias groups share one entity id (all forms of one person, company or system).
    public string EntityId { get; set; } = "";
    // canonical, reversed, initial, dropMiddle, username, email, phone, employeeId, accountNumber,
    // full, noSuffix, short, acronym, street.
    public string AliasKind { get; set; } = "";
    public int Occurrences { get; set; }
}

public sealed class PreserveSpan
{
    public string Value { get; set; } = "";
    // tech, version, outline, threshold, role, heading, doctype, date, reqid, quantity, label.
    public string Kind { get; set; } = "";
}

public sealed class DocTruth
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public int Seed { get; set; }
    public int EntityCount { get; set; }
    public List<SensitiveSpan> Sensitive { get; set; } = new();
    public List<PreserveSpan> Preserve { get; set; } = new();
    // Neither required nor penalized (for example public city names).
    public List<string> Neutral { get; set; } = new();
}

public sealed class TruthSet
{
    public string Scope { get; set; } = "Synthetic ground truth. All values are fictional. Not a measure of real-world accuracy.";
    public string GeneratorVersion { get; set; } = Generator.Version;
    public List<DocTruth> Documents { get; set; } = new();
}

public sealed class Finding
{
    public string Value { get; set; } = "";
    public string Category { get; set; } = "";
    // Optional replacement token chosen by the engine; enables token-consistency scoring.
    public string? Token { get; set; }
}

public sealed class DocFindings
{
    public string Id { get; set; } = "";
    public List<Finding> Findings { get; set; } = new();
}

public sealed class FindingsSet
{
    public List<DocFindings> Documents { get; set; } = new();
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)!;
}

public static class Text
{
    // Same normalization the apps use for identity keys: collapse whitespace, upper-case.
    public static string Norm(string value) =>
        string.Join(" ", (value ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    // Category equivalence classes used for strict scoring.
    public static string Label(string category)
    {
        var c = (category ?? "").Trim().ToUpperInvariant();
        return c switch
        {
            "PROJECT" or "SYSTEM" or "PRODUCT" => "PROJECT",
            "ORGANIZATION" or "VENDOR" or "DEPARTMENT" => "ORGANIZATION",
            _ => c,
        };
    }

    public static bool ContainsWord(string haystackNorm, string needleNorm)
    {
        if (needleNorm.Length == 0) return false;
        return Regex.IsMatch(haystackNorm, "(?<![A-Z0-9])" + Regex.Escape(needleNorm) + "(?![A-Z0-9])", RegexOptions.CultureInvariant);
    }
}

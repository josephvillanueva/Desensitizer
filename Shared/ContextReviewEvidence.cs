using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Desensitizer;

public static class ContextReviewEvidence
{
    public static string Signature(Candidate candidate, string? fileId)
    {
        var sources = string.Join(";", (candidate.DetectionSources ?? new()).OrderBy(s => s, StringComparer.Ordinal));
        var evidence = string.Join("|", fileId ?? "", Project.Normalize(candidate.Value), candidate.Category.ToUpperInvariant(), candidate.Explanation.Trim(), sources);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
    }
}

using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RedTeam;

// Fictional sentinel values. Nothing here is real data.
public static class Sentinel
{
    public const string Person = "Zelda Quimby-Orsino";
    public const string Company = "Vantablack Holdings Zeta";
    public const string Id = "ZQ-5150-0099";
    public static readonly string[] Full = { Person, Company, Id };

    // Folded forms (letters and digits only, lower case). Full values first, then fragments.
    public static readonly (string Needle, string Label, bool IsFull)[] Folded =
    {
        ("zeldaquimbyorsino", "person", true), ("vantablackholdingszeta", "company", true), ("zq51500099", "id", true),
        ("zelda", "person-fragment:Zelda", false), ("quimby", "person-fragment:Quimby", false), ("orsino", "person-fragment:Orsino", false),
        ("vantablack", "company-fragment:Vantablack", false), ("51500099", "id-fragment:5150-0099", false),
    };
}

public sealed record Finding(string Where, string Variant, string Needle, bool IsFull);

// Looks for a sentinel anywhere in an output: every zip entry decompressed (raw bytes, UTF-8, UTF-16 both ways),
// entity decoded, tag stripped, percent decoded, base64 and hex decoded, folded for Unicode tricks,
// plus entry names, the zip comment, central directory extra fields (via a scan of the raw container).
public static class Scanner
{
    static readonly Dictionary<char, char> Homoglyph = new()
    {
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's', ['ԁ'] = 'd', ['ɡ'] = 'g', ['ո'] = 'n',
        ['Ζ'] = 'z', ['ζ'] = 'z', ['Ε'] = 'e', ['Λ'] = 'l', ['Ο'] = 'o', ['ο'] = 'o', ['Α'] = 'a', ['α'] = 'a', ['ɑ'] = 'a', ['ℓ'] = 'l', ['ӏ'] = 'l', ['І'] = 'i', ['Ѵ'] = 'v', ['ѵ'] = 'v',
        ['Ԛ'] = 'q', ['ԛ'] = 'q', ['В'] = 'b', ['Т'] = 't', ['Н'] = 'h', ['К'] = 'k', ['М'] = 'm', ['Ү'] = 'y', ['Х'] = 'x', ['С'] = 'c', ['Р'] = 'p', ['А'] = 'a', ['Е'] = 'e', ['О'] = 'o', ['ꓜ'] = 'z',
    };

    public static string Fold(string text)
    {
        string norm;
        try { norm = text.Normalize(NormalizationForm.FormKD); }
        catch (ArgumentException)
        {
            // Binary data decoded as text can hold lone surrogates. Drop them and try again.
            var clean = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { clean.Append(text[i]).Append(text[i + 1]); i++; }
                else if (!char.IsSurrogate(text[i])) clean.Append(text[i]);
            }
            // Windows normalization can also reject binary-decoded noncharacters.
            // The fallback remains conservative ASCII/homoglyph scanning, rather
            // than allowing a scanner failure to escape the test process.
            try { norm = clean.ToString().Normalize(NormalizationForm.FormKD); }
            catch (ArgumentException) { norm = clean.ToString(); }
        }
        var sb = new StringBuilder(norm.Length);
        foreach (var ch0 in norm)
        {
            var cat = char.GetUnicodeCategory(ch0);
            if (cat is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Control) continue;
            var ch = Homoglyph.TryGetValue(ch0, out var h) ? h : ch0;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    static IEnumerable<(string Variant, string Text)> Variants(string text)
    {
        yield return ("text", text);
        var decoded = text;
        for (var i = 0; i < 3; i++) { var next = WebUtility.HtmlDecode(decoded); if (next == decoded) break; decoded = next; }
        if (decoded != text) yield return ("entity-decoded", decoded);
        var stripped = Regex.Replace(decoded, @"<!\[CDATA\[|\]\]>", "");
        stripped = Regex.Replace(stripped, @"<[^>]*>", "");
        yield return ("tag-stripped", stripped);
        string? pct = null;
        try { pct = Uri.UnescapeDataString(decoded.Replace('+', ' ')); } catch { }
        if (pct != null && pct != decoded) yield return ("percent-decoded", pct);
        foreach (Match m in Regex.Matches(decoded, @"[A-Za-z0-9+/_\-]{16,}={0,2}"))
        {
            var s = m.Value.Replace('-', '+').Replace('_', '/');
            s = s.PadRight((s.Length + 3) / 4 * 4, '=');
            byte[]? bytes = null;
            try { bytes = Convert.FromBase64String(s); } catch { }
            if (bytes == null) continue;
            yield return ("base64:utf8", Encoding.UTF8.GetString(bytes));
            yield return ("base64:utf16", Encoding.Unicode.GetString(bytes));
        }
        foreach (Match m in Regex.Matches(decoded, @"(?:[0-9A-Fa-f]{2}){8,}"))
        {
            var hex = m.Value;
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            yield return ("hex:utf8", Encoding.UTF8.GetString(bytes));
            yield return ("hex:utf16", Encoding.Unicode.GetString(bytes));
        }
        foreach (Match m in Regex.Matches(decoded, @"(?:\\u[0-9A-Fa-f]{4}|\\x[0-9A-Fa-f]{2}){4,}"))
        {
            var s = Regex.Replace(m.Value, @"\\u([0-9A-Fa-f]{4})|\\x([0-9A-Fa-f]{2})", x => ((char)Convert.ToInt32(x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value, 16)).ToString());
            yield return ("escape-decoded", s);
        }
        // Reversed text.
        var rev = new string(stripped.Reverse().ToArray());
        yield return ("reversed", rev);
    }

    static IEnumerable<(string Variant, string Text)> Decodings(byte[] data)
    {
        yield return ("utf8", Encoding.UTF8.GetString(data));
        if (data.Length >= 2)
        {
            yield return ("utf16le", Encoding.Unicode.GetString(data));
            yield return ("utf16be", Encoding.BigEndianUnicode.GetString(data));
        }
        yield return ("latin1", Encoding.Latin1.GetString(data));
    }

    // Raw byte patterns of the plain sentinels in UTF-8 and both UTF-16 forms (case folded for ASCII).
    static bool RawContains(byte[] data, byte[] pattern)
    {
        if (pattern.Length == 0 || data.Length < pattern.Length) return false;
        return data.AsSpan().IndexOf(pattern) >= 0;
    }

    public static List<Finding> ScanBytes(string where, byte[] data)
    {
        var found = new List<Finding>();
        void Add(Finding f) { if (!found.Any(x => x.Where == f.Where && x.Needle == f.Needle && x.IsFull == f.IsFull)) found.Add(f); }
        foreach (var s in Sentinel.Full)
            foreach (var (enc, label) in new[] { (Encoding.UTF8, "raw-utf8"), (Encoding.Unicode, "raw-utf16le"), (Encoding.BigEndianUnicode, "raw-utf16be") })
                foreach (var variant in new[] { s, s.ToLowerInvariant(), s.ToUpperInvariant() })
                    if (RawContains(data, enc.GetBytes(variant))) Add(new Finding(where, label, s, true));
        foreach (var (decVariant, text) in Decodings(data))
            foreach (var (variant, t) in Variants(text))
            {
                var folded = Fold(t);
                foreach (var (needle, label, isFull) in Sentinel.Folded)
                    if (folded.Contains(needle, StringComparison.Ordinal)) Add(new Finding(where, decVariant + "/" + variant, label, isFull));
            }
        return found;
    }

    // The entire output file: the container bytes (names, comment, extra fields), then every entry decompressed.
    public static List<Finding> ScanFile(string path)
    {
        var data = File.ReadAllBytes(path);
        var found = new List<Finding>();
        found.AddRange(ScanBytes("container", data));
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                found.AddRange(ScanBytes("entry-name:" + Safe(entry.FullName), Encoding.UTF8.GetBytes(entry.FullName)).Select(f => f with { Where = "entry-name" }));
                try
                {
                    using var s = entry.Open();
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    found.AddRange(ScanBytes(entry.FullName, ms.ToArray()));
                    // Nested zip inside an entry.
                    var bytes = ms.ToArray();
                    if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K')
                        try
                        {
                            using var inner = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                            foreach (var ie in inner.Entries)
                            {
                                using var istream = ie.Open();
                                var ims = new MemoryStream(); istream.CopyTo(ims);
                                found.AddRange(ScanBytes(entry.FullName + "!" + ie.FullName, ims.ToArray()));
                            }
                        }
                        catch (InvalidDataException) { }
                }
                catch (InvalidDataException) { found.Add(new Finding(entry.FullName, "unreadable", "unreadable-entry", false)); }
            }
        }
        catch (InvalidDataException) { /* not a zip: plain text format, already scanned as container */ }
        return found;
    }

    static string Safe(string s) => s.Length > 60 ? s.Substring(0, 60) : s;

    public static bool TextHas(string text) => ScanBytes("text", Encoding.UTF8.GetBytes(text)).Count > 0;
}


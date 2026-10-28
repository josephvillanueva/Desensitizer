using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Desensitizer;

// Limits and rules that keep a hostile or damaged .docx from exhausting memory,
// and that decide which parts of a package may be written to an output file.
// Messages here are shown to users, so they never contain document text.
public static class PackageSafety
{
    public const int MaxEntries = 4000;
    public const long MaxEntryBytes = 128L * 1024 * 1024;
    public const long MaxTotalBytes = 512L * 1024 * 1024;
    // Highest accepted uncompressed to compressed ratio for an entry larger than RatioFloorBytes.
    public const int MaxCompressionRatio = 300;
    public const long RatioFloorBytes = 1024 * 1024;
    // Fixed timestamp written to every output entry, so file times cannot reveal when the source was edited.
    public static readonly DateTimeOffset NormalizedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Checks the central directory before any entry is read.
    public static void ValidateArchive(ZipArchive archive)
    {
        var entries = archive.Entries;
        if (entries.Count > MaxEntries)
            throw new InvalidDataException("This document has too many internal parts (" + entries.Count + ", limit " + MaxEntries + "). It cannot be processed safely.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in entries)
        {
            var name = entry.FullName;
            if (name.Length == 0 || name.Length > 512 || name.StartsWith("/", StringComparison.Ordinal) || name.Contains('\\') || name.Split('/').Any(segment => segment == ".."))
                throw new InvalidDataException("This document contains an internal part with an unsafe name. It cannot be processed safely.");
            if (!seen.Add(name))
                throw new InvalidDataException("This document contains two internal parts with the same name. It is damaged or was crafted to hide content, so it cannot be processed.");
            if (entry.Length > MaxEntryBytes)
                throw new InvalidDataException("An internal part of this document is larger than " + MaxEntryBytes / (1024 * 1024) + " MB when unpacked. It cannot be processed safely.");
            if (entry.Length > RatioFloorBytes && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxCompressionRatio)
                throw new InvalidDataException("An internal part of this document is compressed far more than normal, which can be a decompression bomb. It cannot be processed safely.");
            total += entry.Length;
            if (total > MaxTotalBytes)
                throw new InvalidDataException("This document is larger than " + MaxTotalBytes / (1024 * 1024) + " MB when unpacked. It cannot be processed safely.");
        }
    }

    // Reads an entry without trusting the size it declares: more bytes than declared is refused.
    public static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        var declared = entry.Length;
        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)Math.Min(declared, int.MaxValue));
        var chunk = new byte[81920];
        long read = 0;
        int count;
        while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            read += count;
            if (read > declared || read > MaxEntryBytes)
                throw new InvalidDataException("An internal part of this document is larger than it declares. It is damaged or was crafted to be unsafe.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    public static string ReadText(ZipArchiveEntry entry)
    {
        var bytes = ReadBytes(entry);
        using var reader = new StreamReader(new MemoryStream(bytes, false), Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    // XML parse errors normally quote the offending text. Report the part and position only.
    public static XDocument Parse(string part, string xml, bool preserveWhitespace)
    {
        try { return XDocument.Parse(xml, preserveWhitespace ? LoadOptions.PreserveWhitespace : LoadOptions.None); }
        catch (XmlException ex) { throw new InvalidDataException("The part " + part + " is not valid XML (line " + ex.LineNumber + ", position " + ex.LinePosition + "). The document is damaged."); }
    }

    private static readonly byte[] FingerprintKey = RandomNumberGenerator.GetBytes(32);

    // Short, non-reversible tag for a value, stable only within this process. For
    // log lines that must tell values apart without containing them.
    public static string Fingerprint(string value)
    {
        var hash = HMACSHA256.HashData(FingerprintKey, Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ output allow-list

    // Parts an output package may contain. Everything else (printer settings,
    // thumbnails, fonts, charts, diagrams, glossary, custom XML, unknown parts)
    // is dropped, because the residue audit can only vouch for these.
    public static bool IsAllowedOutputPart(string name)
    {
        if (name == "[Content_Types].xml" || name == "_rels/.rels" || name == "docProps/core.xml") return true;
        if (!name.StartsWith("word/", StringComparison.OrdinalIgnoreCase)) return false;
        if (DocumentPackageContext.IsPrivatePart(name) || DocumentPackageContext.IsEmbeddedPart(name)) return false;
        var rest = name.Substring(5);
        if (rest.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase)) return rest.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) && rest.IndexOf('/', 6) < 0;
        if (rest.StartsWith("theme/", StringComparison.OrdinalIgnoreCase)) return rest.IndexOf('/', 6) < 0 && rest.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
        if (rest.IndexOf('/') >= 0 || !rest.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = rest.Substring(0, rest.Length - 4);
        if (stem is "document" or "styles" or "stylesWithEffects" or "settings" or "webSettings" or "fontTable" or "numbering" or "footnotes" or "endnotes") return true;
        return IsNumbered(stem, "header") || IsNumbered(stem, "footer");
    }

    private static bool IsNumbered(string stem, string prefix) =>
        stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && stem.Skip(prefix.Length).All(char.IsDigit);

    // Rewrites a package so it holds only allowed parts: unlisted parts are
    // dropped, relationships and content-type entries that point at dropped
    // parts are removed, every entry gets the same timestamp and the archive
    // carries no comment. Streams entries into a new archive, so nothing of the
    // old container (comment, extra fields, ordering tricks) survives.
    public static byte[] Finalize(byte[] package)
    {
        using var source = new ZipArchive(new MemoryStream(package, false), ZipArchiveMode.Read);
        ValidateArchive(source);
        var kept = source.Entries.Where(e => IsAllowedOutputPart(e.FullName)).ToList();
        var present = new HashSet<string>(kept.Select(e => e.FullName), StringComparer.OrdinalIgnoreCase);
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            // [Content_Types].xml first, as Word writes it.
            foreach (var entry in kept.OrderBy(e => e.FullName == "[Content_Types].xml" ? 0 : 1))
            {
                var data = ReadBytes(entry);
                if (entry.FullName == "[Content_Types].xml") data = PruneContentTypes(data, present);
                else if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) data = PruneRelationships(entry.FullName, data, present);
                var created = archive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                created.LastWriteTime = NormalizedTimestamp;
                using var stream = created.Open();
                stream.Write(data, 0, data.Length);
            }
        }
        return output.ToArray();
    }

    private static byte[] PruneContentTypes(byte[] data, HashSet<string> present)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart((char)0xFEFF), LoadOptions.PreserveWhitespace);
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "Override").ToList())
        {
            var part = element.Attribute("PartName")?.Value;
            if (part != null && !present.Contains(part.TrimStart('/'))) element.Remove();
        }
        return Serialize(document);
    }

    private static byte[] PruneRelationships(string relsName, byte[] data, HashSet<string> present)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart((char)0xFEFF), LoadOptions.PreserveWhitespace);
        // _rels/.rels is relative to the package root, word/_rels/x.rels to word/.
        var baseFolder = relsName == "_rels/.rels" ? "" : relsName.Substring(0, relsName.IndexOf("_rels/", StringComparison.OrdinalIgnoreCase));
        foreach (var rel in document.Descendants().Where(e => e.Name.LocalName == "Relationship").ToList())
        {
            var external = string.Equals(rel.Attribute("TargetMode")?.Value, "External", StringComparison.OrdinalIgnoreCase);
            var target = rel.Attribute("Target")?.Value;
            if (external) { rel.Remove(); continue; }
            if (target == null) continue;
            var resolved = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : Resolve(baseFolder, target);
            if (!present.Contains(resolved)) rel.Remove();
        }
        return Serialize(document);
    }

    private static string Resolve(string baseFolder, string target)
    {
        var parts = new List<string>(baseFolder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in target.Split('/'))
        {
            if (segment == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (segment != "." && segment.Length > 0) parts.Add(segment);
        }
        return string.Join("/", parts);
    }

    private static byte[] Serialize(XDocument document) =>
        new UTF8Encoding(false).GetBytes((document.Declaration != null ? document.Declaration + "\n" : "") + document.ToString(SaveOptions.DisableFormatting));
}

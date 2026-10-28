using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Desensitizer;

// A read-only view of an Open XML package (.xlsx, .pptx) that applies the same
// archive limits as the Word path (PackageSafety), and a writer that builds a
// completely new archive from the parts the caller chooses. Nothing of the
// source container (comment, extra fields, unlisted parts, ordering) is copied.
internal sealed class OoxmlPackage : IDisposable
{
    public const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    public static readonly XNamespace R = RelNs;
    public static readonly XNamespace Pr = PackageRelNs;
    public static readonly XNamespace Ct = ContentTypesNs;

    public sealed record Rel(string Id, string Type, string Target, bool External, string Resolved)
    {
        public bool IsType(string suffix) => Type.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase);
    }

    private readonly ZipArchive archive;
    private readonly Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> bytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? defaults;
    private Dictionary<string, string>? overrides;

    private OoxmlPackage(ZipArchive archive)
    {
        this.archive = archive;
        foreach (var entry in archive.Entries)
            if (!entry.FullName.EndsWith("/", StringComparison.Ordinal)) entries[entry.FullName] = entry;
    }

    public static OoxmlPackage Open(string path, string friendlyKind)
    {
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw NotValid(friendlyKind); }
        try { PackageSafety.ValidateArchive(archive); return new OoxmlPackage(archive); }
        catch { archive.Dispose(); throw; }
    }

    public static OoxmlPackage OpenBytes(byte[] data)
    {
        var archive = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        try { PackageSafety.ValidateArchive(archive); return new OoxmlPackage(archive); }
        catch { archive.Dispose(); throw; }
    }

    public static InvalidDataException NotValid(string friendlyKind) =>
        new("This file is not a valid " + friendlyKind + ". It may be password protected, damaged or not that kind of file.");

    public IEnumerable<string> Names => entries.Keys;
    public bool Has(string name) => entries.ContainsKey(name);

    public byte[] Bytes(string name)
    {
        if (bytes.TryGetValue(name, out var cached)) return cached;
        if (!entries.TryGetValue(name, out var entry)) throw new InvalidDataException("The package has no part named " + name + ". The file is damaged.");
        var data = PackageSafety.ReadBytes(entry);
        bytes[name] = data;
        return data;
    }

    // The part as parsed, whitespace kept. Shared instance: clone before changing.
    public XDocument Xml(string name)
    {
        if (documents.TryGetValue(name, out var cached)) return cached;
        var text = new UTF8Encoding(false).GetString(Bytes(name)).TrimStart('﻿');
        var document = PackageSafety.Parse(name, text, true);
        documents[name] = document;
        return document;
    }

    public static string RelsNameFor(string part)
    {
        if (part.Length == 0) return "_rels/.rels";
        var slash = part.LastIndexOf('/');
        return (slash < 0 ? "" : part.Substring(0, slash + 1)) + "_rels/" + part.Substring(slash + 1) + ".rels";
    }

    public static string Folder(string part)
    {
        var slash = part.LastIndexOf('/');
        return slash < 0 ? "" : part.Substring(0, slash);
    }

    public static string Resolve(string baseFolder, string target)
    {
        if (target.StartsWith("/", StringComparison.Ordinal)) target = target.TrimStart('/');
        else target = baseFolder.Length == 0 ? target : baseFolder + "/" + target;
        var parts = new List<string>();
        foreach (var segment in target.Split('/'))
        {
            if (segment == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (segment != "." && segment.Length > 0) parts.Add(Uri.UnescapeDataString(segment));
        }
        return string.Join("/", parts);
    }

    // The relationships of a part ("" for the package itself). Empty when the part has none.
    public List<Rel> Rels(string part)
    {
        var name = RelsNameFor(part);
        var list = new List<Rel>();
        if (!Has(name)) return list;
        var folder = Folder(part);
        foreach (var element in Xml(name).Descendants().Where(e => e.Name.LocalName == "Relationship"))
        {
            var external = string.Equals((string?)element.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);
            var target = (string?)element.Attribute("Target") ?? "";
            list.Add(new Rel((string?)element.Attribute("Id") ?? "", (string?)element.Attribute("Type") ?? "", target, external, external ? target : Resolve(folder, target)));
        }
        return list;
    }

    public string? ContentType(string part)
    {
        if (overrides == null)
        {
            defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Has("[Content_Types].xml"))
                foreach (var e in Xml("[Content_Types].xml").Descendants())
                {
                    if (e.Name.LocalName == "Default" && e.Attribute("Extension") != null) defaults[(string)e.Attribute("Extension")!] = (string?)e.Attribute("ContentType") ?? "";
                    else if (e.Name.LocalName == "Override" && e.Attribute("PartName") != null) overrides[((string)e.Attribute("PartName")!).TrimStart('/')] = (string?)e.Attribute("ContentType") ?? "";
                }
        }
        if (overrides.TryGetValue(part, out var type)) return type;
        var dot = part.LastIndexOf('.');
        return dot >= 0 && defaults!.TryGetValue(part.Substring(dot + 1), out var fallback) ? fallback : null;
    }

    // Every part, with its declared content type, that this package ships under macro or active content rules.
    public bool HasMacroContent() =>
        Names.Any(n => n.IndexOf("vbaProject", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("activeX/", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("macrosheets/", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("dialogsheets/", StringComparison.OrdinalIgnoreCase) >= 0)
        || Names.Any(n => (ContentType(n) ?? "").IndexOf("macroEnabled", StringComparison.OrdinalIgnoreCase) >= 0 || (ContentType(n) ?? "").IndexOf("vbaProject", StringComparison.OrdinalIgnoreCase) >= 0);

    public void Dispose() => archive.Dispose();

    // ------------------------------------------------------------------ writing

    public static byte[] Serialize(XDocument document)
    {
        var declaration = document.Declaration ?? new XDeclaration("1.0", "UTF-8", "yes");
        return new UTF8Encoding(false).GetBytes(declaration + "\r\n" + document.ToString(SaveOptions.DisableFormatting));
    }

    public static XDocument Relationships(IEnumerable<(string Id, string Type, string Target)> rels) =>
        new(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(Pr + "Relationships", rels.Select(r => new XElement(Pr + "Relationship", new XAttribute("Id", r.Id), new XAttribute("Type", r.Type), new XAttribute("Target", r.Target)))));

    // Builds a fresh archive: [Content_Types].xml first, every entry with the same fixed timestamp, no archive comment.
    public static byte[] Build(IReadOnlyList<(string Name, byte[] Data, string? ContentType)> parts, IReadOnlyDictionary<string, string> defaultTypes)
    {
        var types = new XElement(Ct + "Types");
        foreach (var d in defaultTypes) types.Add(new XElement(Ct + "Default", new XAttribute("Extension", d.Key), new XAttribute("ContentType", d.Value)));
        foreach (var p in parts)
        {
            if (p.ContentType == null) continue;
            var dot = p.Name.LastIndexOf('.');
            var ext = dot >= 0 ? p.Name.Substring(dot + 1) : "";
            if (defaultTypes.TryGetValue(ext, out var known) && known == p.ContentType) continue;
            types.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/" + p.Name), new XAttribute("ContentType", p.ContentType)));
        }
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Write(string name, byte[] data)
            {
                if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vml", StringComparison.OrdinalIgnoreCase))
                {
                    var document = PackageSafety.Parse(name, new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF'), true);
                    document.DescendantNodes().Where(n => n is XComment or XProcessingInstruction).Remove();
                    data = Serialize(document);
                }
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = PackageSafety.NormalizedTimestamp;
                using var target = entry.Open();
                target.Write(data, 0, data.Length);
            }
            Write("[Content_Types].xml", Serialize(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), types)));
            foreach (var p in parts) Write(p.Name, p.Data);
        }
        return stream.ToArray();
    }

    public static readonly IReadOnlyDictionary<string, string> StandardDefaults = new Dictionary<string, string>
    {
        ["rels"] = "application/vnd.openxmlformats-package.relationships+xml",
        ["xml"] = "application/xml",
    };

    // ------------------------------------------------------------------ package level checks shared by the formats

    // Fails closed unless the package is internally consistent: every relationship points at a part that is
    // present, none is external, and every relationship id used in an XML part resolves in that part's relationships.
    public static void AssertSelfContained(OoxmlPackage package)
    {
        foreach (var name in package.Names.Where(n => n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            var part = name == "_rels/.rels" ? "" : Regex.Replace(name, @"(^|/)_rels/([^/]+)\.rels$", "$1$2");
            foreach (var rel in package.Rels(part))
            {
                if (rel.External) throw new InvalidDataException("The output still contains an external link. Export was blocked.");
                if (!package.Has(rel.Resolved)) throw new InvalidDataException("The output has a relationship to a part that is missing (" + name + "). Export was blocked.");
            }
        }
        foreach (var name in package.Names.Where(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && n != "[Content_Types].xml" && !n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            var ids = package.Rels(name).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var attribute in package.Xml(name).Descendants().Attributes().Where(a => a.Name.Namespace == R))
                if (!ids.Contains(attribute.Value)) throw new InvalidDataException("The output has a reference that points nowhere (" + name + "). Export was blocked.");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Desensitizer;

// One open .docx plus everything parsed from it during a single operation.
// Each part is read from the zip once and parsed once per whitespace mode, and
// the final-view (tracked changes resolved) copy is derived from the parsed
// part rather than parsed again. Create one with DocumentProcessor.OpenContext,
// pass it to ReadText, ReadTableContext, InspectReviewMarkup and Desensitize,
// then dispose it. The context holds the file open for reading until disposed.
// It is not thread safe, and cached documents it returns must not be modified.
public sealed class DocumentPackageContext : IDisposable
{
    private readonly ZipArchive archive;
    private readonly Dictionary<string, ZipArchiveEntry> entries;
    private readonly Dictionary<string, string> texts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Name, bool Preserve), XDocument> documents = new();
    private readonly Dictionary<string, XDocument> finalViews = new(StringComparer.Ordinal);
    private string? extractedText;
    private bool disposed;

    private DocumentPackageContext(string path, ZipArchive archive)
    {
        Path = path;
        this.archive = archive;
        entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries) entries[entry.FullName] = entry;
        EntryNames = archive.Entries.Select(e => e.FullName).ToArray();
    }

    public static DocumentPackageContext Open(string path)
    {
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new InvalidDataException("This file is not a valid Word document (.docx). It may be damaged or not a Word file."); }
        try { PackageSafety.ValidateArchive(archive); return new DocumentPackageContext(path, archive); }
        catch { archive.Dispose(); throw; }
    }

    // A package that exists only in memory, used for the protected copy before
    // anything is written to disk.
    internal static DocumentPackageContext OpenBytes(byte[] data)
    {
        var archive = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        try { PackageSafety.ValidateArchive(archive); return new DocumentPackageContext("", archive); }
        catch { archive.Dispose(); throw; }
    }

    public string Path { get; }
    // Entry names in archive order.
    public IReadOnlyList<string> EntryNames { get; }
    // Counters for tests and diagnostics: zip entries read and XML documents parsed.
    public int EntriesRead { get; private set; }
    public int DocumentsParsed { get; private set; }
    // Set once the macro and tracked table-cell checks passed for this file.
    internal bool Validated { get; set; }

    // word/*.xml parts, including parts that are removed from output.
    public IEnumerable<string> WordXmlParts => EntryNames.Where(IsWordXmlPart);
    // word/*.xml parts that are read, mapped and exported.
    public IEnumerable<string> VisibleWordXmlParts => EntryNames.Where(name => IsWordXmlPart(name) && !IsPrivatePart(name));

    internal static bool IsWordXmlPart(string name) =>
        name.StartsWith("word/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    internal static bool IsEmbeddedPart(string name) => name.IndexOf("embeddings/", StringComparison.OrdinalIgnoreCase) >= 0;
    internal static bool IsPrivatePart(string name) =>
        name.IndexOf("customXml/", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("word/comments", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("word/people.xml", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("word/glossary/", StringComparison.OrdinalIgnoreCase) >= 0;

    public string ReadEntryText(string name)
    {
        EnsureOpen();
        if (texts.TryGetValue(name, out var cached)) return cached;
        if (!entries.TryGetValue(name, out var entry)) throw new FileNotFoundException("The Word package has no part named " + name + ".");
        var text = PackageSafety.ReadText(entry);
        EntriesRead++;
        texts[name] = text;
        return text;
    }

    // The raw bytes of an entry, read with the size limits of PackageSafety. Not cached.
    internal byte[] ReadEntryBytes(string name)
    {
        EnsureOpen();
        if (!entries.TryGetValue(name, out var entry)) throw new FileNotFoundException("The Word package has no part named " + name + ".");
        return PackageSafety.ReadBytes(entry);
    }

    // The part as stored. Shared instance: clone it before changing it.
    public XDocument GetDocument(string name, bool preserveWhitespace = false)
    {
        EnsureOpen();
        if (documents.TryGetValue((name, preserveWhitespace), out var cached)) return cached;
        var document = PackageSafety.Parse(name, ReadEntryText(name), preserveWhitespace);
        DocumentsParsed++;
        documents[(name, preserveWhitespace)] = document;
        return document;
    }

    // The part with tracked changes and comments resolved. Shared instance.
    public XDocument GetFinalView(string name)
    {
        EnsureOpen();
        if (finalViews.TryGetValue(name, out var cached)) return cached;
        var copy = new XDocument(GetDocument(name, false));
        ReviewMarkup.Normalize(copy);
        finalViews[name] = copy;
        return copy;
    }

    // The same paragraph text DocumentProcessor.ReadText returns for a .docx.
    public string ExtractText()
    {
        EnsureOpen();
        return extractedText ??= string.Join("\n", VisibleWordXmlParts.Select(name => ParagraphText(GetFinalView(name))));
    }

    internal static string ParagraphText(XDocument document)
    {
        var paragraphs = document.Descendants().Where(x => x.Name.LocalName == "p").Select(p => string.Concat(p.Descendants().Where(x => x.Name.LocalName == "t" || x.Name.LocalName == "tab" || x.Name.LocalName == "br" || x.Name.LocalName == "cr").Select(x => x.Name.LocalName == "t" ? x.Value : " ")));
        return string.Join("\n", paragraphs);
    }

    private void EnsureOpen()
    {
        if (disposed) throw new ObjectDisposedException(nameof(DocumentPackageContext));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        archive.Dispose();
        texts.Clear();
        documents.Clear();
        finalViews.Clear();
    }
}

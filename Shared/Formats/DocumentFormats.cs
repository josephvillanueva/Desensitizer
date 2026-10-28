using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Desensitizer;

// One supported document type. DocumentProcessor dispatches by extension through
// DocumentFormats, so the interface, the file dialogs and the drop filter all
// agree on what can be opened. Every implementation must fail closed: when any
// check cannot finish, Transform throws and leaves no output file.
public interface IDocumentFormat
{
    // Lower case, with the dot: ".docx".
    string Extension { get; }
    // Plain words for messages: "Word document".
    string DisplayName { get; }
    // True for the formats whose pipeline lives inside DocumentProcessor itself
    // (Word and plain text). Their behavior is unchanged by the registry.
    bool IsBuiltIn { get; }

    // Throws InvalidDataException with a friendly sentence when the file must not be processed (macros, damage, limits).
    void Validate(string path);
    // The text that detectors and reviewers see. Labelled lines, one value per line.
    string ReadText(string path, DocumentPackageContext? context = null);
    // Table context for reviewers, or null where the format has no meaningful tables.
    TableReviewContext? ReadTableContext(string path, string text, DocumentPackageContext? context = null);
    // What the format resolves silently (tracked changes, comments), or Empty.
    ReviewMarkupSummary InspectReviewMarkup(string path, DocumentPackageContext? context = null);
    // Text kept outside the visible text that the export removes, as labelled lines.
    IReadOnlyList<string> ReadHiddenChannels(string path, DocumentPackageContext? context = null);
    // Sentences saying what the protected copy will not contain (images, charts, external links, ...). Empty when nothing is removed.
    IReadOnlyList<string> DescribeRemovals(string path, DocumentPackageContext? context = null);
    // forward: replace values with tokens. Otherwise restore tokens. Returns the number of unknown tokens (rehydrate only).
    int Transform(string source, string output, IEnumerable<Mapping> mappings, bool forward, DocumentProcessingOptions? options = null, DocumentPackageContext? context = null, bool allowUnappliedMappings = false);
    // Re-checks a finished protected copy against its source: structure unchanged, only mapped text replaced, no mapped value left.
    void Audit(string source, string output, IEnumerable<Mapping> mappings);
}

// Registry keyed by extension.
public static class DocumentFormats
{
    private static readonly Dictionary<string, IDocumentFormat> Formats = new(StringComparer.OrdinalIgnoreCase);

    static DocumentFormats()
    {
        Register(new WordFormat());
        Register(new TextFormat());
        Register(new MarkdownFormat());
        Register(new CsvFormat());
        Register(new XlsxFormat());
        Register(new PptxFormat());
    }

    private static void Register(IDocumentFormat format) => Formats[format.Extension] = format;

    public static IReadOnlyCollection<IDocumentFormat> All => Formats.Values;

    // The format for a file name or an extension, or null when it is not supported.
    public static IDocumentFormat? Find(string pathOrExtension)
    {
        var extension = pathOrExtension.StartsWith(".", StringComparison.Ordinal) && pathOrExtension.IndexOf('.', 1) < 0 && pathOrExtension.IndexOfAny(new[] { '\\', '/' }) < 0
            ? pathOrExtension : Path.GetExtension(pathOrExtension);
        return Formats.TryGetValue(extension, out var format) ? format : null;
    }

    public static IDocumentFormat Require(string path) =>
        Find(path) ?? throw new InvalidDataException(SupportedFormats.RejectionMessage(path));

    // The format when its pipeline is not the built in Word or text one.
    internal static IDocumentFormat? Extended(string path)
    {
        var format = Find(path);
        return format != null && !format.IsBuiltIn ? format : null;
    }
}

// The single table of what the apps accept. File dialogs, the drop filter, the
// About text and the rejection messages all read it.
public static class SupportedFormats
{
    public sealed record Entry(string Extension, string Label);

    // In the order shown to the user.
    public static readonly IReadOnlyList<Entry> Supported = new[]
    {
        new Entry(".docx", "Word document"),
        new Entry(".xlsx", "Excel workbook"),
        new Entry(".pptx", "PowerPoint presentation"),
        new Entry(".csv", "CSV spreadsheet"),
        new Entry(".md", "Markdown file"),
        new Entry(".txt", "Text file"),
    };

    private const string ToDocx = "In Word, use Save As and choose Word Document (.docx), then add that copy.";
    private const string ToXlsx = "In Excel, use Save As and choose Excel Workbook (.xlsx), then add that copy.";
    private const string ToPptx = "In PowerPoint, use Save As and choose PowerPoint Presentation (.pptx), then add that copy.";

    // Known files that are not supported, with the advice shown to the user.
    private static readonly Dictionary<string, string> Rejected = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = "Old Word files (.doc) cannot be protected reliably. " + ToDocx,
        [".dot"] = "Old Word templates (.dot) cannot be protected reliably. " + ToDocx,
        [".docm"] = "Word files with macros (.docm) are not accepted. " + ToDocx + " Saving as .docx removes the macros.",
        [".dotx"] = "Word templates (.dotx) are not accepted. " + ToDocx,
        [".dotm"] = "Word templates with macros (.dotm) are not accepted. " + ToDocx,
        [".rtf"] = "Rich Text files (.rtf) cannot be protected reliably. " + ToDocx,
        [".odt"] = "OpenDocument text files (.odt) cannot be protected reliably. " + ToDocx,
        [".wps"] = "WPS Writer files cannot be protected reliably. " + ToDocx,
        [".pages"] = "Pages files cannot be protected reliably. Export the document as Word (.docx) from Pages, then add that copy.",
        [".pdf"] = "PDF files are not supported, because their text cannot be replaced reliably in place. Open the original document and save it as .docx, .xlsx or .pptx instead. Do not use Save as PDF.",
        [".xls"] = "Old Excel files (.xls) cannot be protected reliably. " + ToXlsx,
        [".xlt"] = "Old Excel templates (.xlt) cannot be protected reliably. " + ToXlsx,
        [".xlsm"] = "Excel files with macros (.xlsm) are not accepted. " + ToXlsx + " Saving as .xlsx removes the macros.",
        [".xlsb"] = "Binary Excel files (.xlsb) cannot be protected reliably. " + ToXlsx,
        [".xltx"] = "Excel templates (.xltx) are not accepted. " + ToXlsx,
        [".xltm"] = "Excel templates with macros (.xltm) are not accepted. " + ToXlsx,
        [".ods"] = "OpenDocument spreadsheets (.ods) cannot be protected reliably. " + ToXlsx,
        [".numbers"] = "Numbers files cannot be protected reliably. Export the sheet as Excel (.xlsx) from Numbers, then add that copy.",
        [".ppt"] = "Old PowerPoint files (.ppt) cannot be protected reliably. " + ToPptx,
        [".pps"] = "Old PowerPoint shows (.pps) cannot be protected reliably. " + ToPptx,
        [".pptm"] = "PowerPoint files with macros (.pptm) are not accepted. " + ToPptx + " Saving as .pptx removes the macros.",
        [".ppsx"] = "PowerPoint shows (.ppsx) are not accepted. " + ToPptx,
        [".potx"] = "PowerPoint templates (.potx) are not accepted. " + ToPptx,
        [".odp"] = "OpenDocument presentations (.odp) cannot be protected reliably. " + ToPptx,
        [".key"] = "Keynote files cannot be protected reliably. Export the presentation as PowerPoint (.pptx) from Keynote, then add that copy.",
    };

    public static string Extension(string path) => Path.GetExtension(path ?? "").ToLowerInvariant();

    public static bool IsSupported(string path) => Supported.Any(e => e.Extension == Extension(path));

    public static bool IsKnownUnsupported(string path) => Rejected.ContainsKey(Extension(path));

    // "Word (.docx), Excel (.xlsx), PowerPoint (.pptx), CSV (.csv), Markdown (.md) and text (.txt)".
    public static string ListForHumans() => "Word (.docx), Excel (.xlsx), PowerPoint (.pptx), CSV (.csv), Markdown (.md) and text (.txt)";

    // Short phrase for status lines: ".docx, .xlsx, .pptx, .csv, .md or .txt".
    public static string ListOfExtensions()
    {
        var items = Supported.Select(e => e.Extension).ToArray();
        return string.Join(", ", items.Take(items.Length - 1)) + " or " + items[^1];
    }

    // The sentence shown when a file of this type is added. Never contains the file name or any document text.
    public static string RejectionMessage(string path)
    {
        var extension = Extension(path);
        if (Rejected.TryGetValue(extension, out var advice)) return advice;
        if (IsSupported(path)) return "";
        var shown = extension.Length == 0 ? "This file has no file type" : "Files of type " + extension + " are not supported";
        return shown + ". Supported types are " + ListForHumans() + ". For anything else, save a copy as .docx, .xlsx or .pptx first.";
    }

    // OpenFileDialog filter: all supported types first, then each type on its own.
    public static string OpenFilter()
    {
        var all = string.Join(";", Supported.Select(e => "*" + e.Extension));
        return "Supported documents|" + all + "|" + string.Join("|", Supported.Select(e => e.Label + "|*" + e.Extension));
    }

    // SaveFileDialog filter and default extension for the output of a source file.
    public static string OutputExtension(string sourcePath) => IsSupported(sourcePath) ? Extension(sourcePath) : ".txt";

    public static string SaveFilter(string extension)
    {
        var entry = Supported.FirstOrDefault(e => e.Extension == extension.ToLowerInvariant()) ?? Supported[^1];
        return entry.Label + "|*" + entry.Extension;
    }

    // One summary line for the drop filter and the add-files dialog result.
    public static string SkippedSummary(IEnumerable<string> skippedPaths)
    {
        var skipped = skippedPaths.ToList();
        if (skipped.Count == 0) return "";
        var prefix = skipped.Count + " item" + (skipped.Count == 1 ? " was" : "s were") + " skipped. ";
        var messages = skipped.Select(RejectionMessage).Distinct().ToList();
        return messages.Count == 1 && messages[0].Length > 0
            ? prefix + messages[0]
            : prefix + "Only existing " + ListForHumans() + " files can be added. For an old format, save a copy as .docx, .xlsx or .pptx first.";
    }
}

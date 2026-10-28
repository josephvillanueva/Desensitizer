using System;
using System.Collections.Generic;
using System.Linq;

namespace Desensitizer;

// Word (.docx) and plain text (.txt) keep their pipelines inside DocumentProcessor.
// These classes only expose them through IDocumentFormat, so nothing about their
// behavior changes: every method calls the same public DocumentProcessor method
// the interface called before the registry existed.
public sealed class WordFormat : IDocumentFormat
{
    public string Extension => ".docx";
    public string DisplayName => "Word document";
    public bool IsBuiltIn => true;
    public void Validate(string path) => DocumentProcessor.Check(path, null);
    public string ReadText(string path, DocumentPackageContext? context = null) => DocumentProcessor.ReadText(path, context);
    public TableReviewContext? ReadTableContext(string path, string text, DocumentPackageContext? context = null) => DocumentProcessor.ReadTableContext(path, text, context);
    public ReviewMarkupSummary InspectReviewMarkup(string path, DocumentPackageContext? context = null) => DocumentProcessor.InspectReviewMarkup(path, context);
    public IReadOnlyList<string> ReadHiddenChannels(string path, DocumentPackageContext? context = null) => DocumentProcessor.ReadHiddenChannels(path, context);
    // Pictures and embedded objects leave visible placeholders in a Word export, so there is nothing extra to report.
    public IReadOnlyList<string> DescribeRemovals(string path, DocumentPackageContext? context = null) => Array.Empty<string>();
    public int Transform(string source, string output, IEnumerable<Mapping> mappings, bool forward, DocumentProcessingOptions? options = null, DocumentPackageContext? context = null, bool allowUnappliedMappings = false)
    {
        if (forward) { DocumentProcessor.Desensitize(source, output, mappings, options, context, allowUnappliedMappings); return 0; }
        return DocumentProcessor.Rehydrate(source, output, mappings, context);
    }
    public void Audit(string source, string output, IEnumerable<Mapping> mappings) => PreservationAudit.AssertPreserved(source, output, mappings);
}

public sealed class TextFormat : IDocumentFormat
{
    public string Extension => ".txt";
    public string DisplayName => "Text file";
    public bool IsBuiltIn => true;
    public void Validate(string path) => DocumentProcessor.Check(path, null);
    public string ReadText(string path, DocumentPackageContext? context = null) => DocumentProcessor.ReadText(path, context);
    public TableReviewContext? ReadTableContext(string path, string text, DocumentPackageContext? context = null) => null;
    public ReviewMarkupSummary InspectReviewMarkup(string path, DocumentPackageContext? context = null) => ReviewMarkupSummary.Empty;
    public IReadOnlyList<string> ReadHiddenChannels(string path, DocumentPackageContext? context = null) => Array.Empty<string>();
    public IReadOnlyList<string> DescribeRemovals(string path, DocumentPackageContext? context = null) => Array.Empty<string>();
    public int Transform(string source, string output, IEnumerable<Mapping> mappings, bool forward, DocumentProcessingOptions? options = null, DocumentPackageContext? context = null, bool allowUnappliedMappings = false)
    {
        if (forward) { DocumentProcessor.Desensitize(source, output, mappings, options, context, allowUnappliedMappings); return 0; }
        return DocumentProcessor.Rehydrate(source, output, mappings, context);
    }
    public void Audit(string source, string output, IEnumerable<Mapping> mappings) => PreservationAudit.AssertPreserved(source, output, mappings);
}

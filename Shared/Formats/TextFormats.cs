using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Desensitizer;

// Markdown is plain text with a different name. It is read and written line by
// line like a .txt file (same encoding rules, the extension is kept), and the
// protected copy is also searched as a whole, so a value split across lines is
// caught instead of shipping.
public sealed class MarkdownFormat : IDocumentFormat
{
    public string Extension => ".md";
    public string DisplayName => "Markdown file";
    public bool IsBuiltIn => false;
    public void Validate(string path) { if (!File.Exists(path)) throw new FileNotFoundException("The file does not exist."); }
    public string ReadText(string path, DocumentPackageContext? context = null) => TextEncodingSupport.Read(path).Text;
    public TableReviewContext? ReadTableContext(string path, string text, DocumentPackageContext? context = null) => null;
    public ReviewMarkupSummary InspectReviewMarkup(string path, DocumentPackageContext? context = null) => ReviewMarkupSummary.Empty;
    public IReadOnlyList<string> ReadHiddenChannels(string path, DocumentPackageContext? context = null) => Array.Empty<string>();
    public IReadOnlyList<string> DescribeRemovals(string path, DocumentPackageContext? context = null) => Array.Empty<string>();

    public int Transform(string source, string output, IEnumerable<Mapping> mappings, bool forward, DocumentProcessingOptions? options = null, DocumentPackageContext? context = null, bool allowUnappliedMappings = false) =>
        FormatSupport.Run(source, output, () =>
        {
            var loaded = TextEncodingSupport.Read(source);
            var list = FormatSupport.Prepare(loaded.Text, mappings, forward, allowUnappliedMappings);
            var changed = forward
                ? string.Join("\n", loaded.Text.Split('\n').Select(line => FormatSupport.Protect(line, list)))
                : FormatSupport.Restore(loaded.Text, list);
            if (forward) AuditText(loaded.Text, changed, list);
            var unknown = forward ? 0 : DocumentProcessor.UnknownTokens(changed, list);
            FormatSupport.Release(output, TextEncodingSupport.Write(changed, loaded.Encoding));
            return unknown;
        });

    public void Audit(string source, string output, IEnumerable<Mapping> mappings)
    {
        var list = mappings.OrderByDescending(m => m.OriginalValue.Length).ToList();
        AuditText(TextEncodingSupport.Read(source).Text, TextEncodingSupport.Read(output).Text, list);
    }

    private static void AuditText(string original, string protectedText, List<Mapping> list)
    {
        var expected = string.Join("\n", original.Split('\n').Select(line => FormatSupport.Protect(line, list)));
        if (expected != protectedText) throw new InvalidDataException("Export blocked: text outside the selected replacements changed.");
        var scanner = FormatSupport.Scanner(list);
        var hit = FormatSupport.FindResidue(scanner, list, protectedText);
        if (hit != null) throw FormatSupport.ResidueInText(hit, "the document", "text");
    }
}

// Comma separated values. The file is parsed into cells and written back with
// every delimiter, quote, line ending and cell position exactly as it was; only
// text inside cells changes. Anything that is not clearly well formed (an
// unterminated quote, a stray quote inside an unquoted cell, text after a
// closing quote) stops the read and the export, because a file that is parsed
// differently by another program could hide a value.
public sealed class CsvFormat : IDocumentFormat
{
    public string Extension => ".csv";
    public string DisplayName => "CSV spreadsheet";
    public bool IsBuiltIn => false;
    public void Validate(string path) { if (!File.Exists(path)) throw new FileNotFoundException("The file does not exist."); }

    public string ReadText(string path, DocumentPackageContext? context = null) => CellText(CsvDocument.Parse(TextEncodingSupport.Read(path).Text));
    public TableReviewContext? ReadTableContext(string path, string text, DocumentPackageContext? context = null) => null;
    public ReviewMarkupSummary InspectReviewMarkup(string path, DocumentPackageContext? context = null) => ReviewMarkupSummary.Empty;
    public IReadOnlyList<string> ReadHiddenChannels(string path, DocumentPackageContext? context = null) => Array.Empty<string>();
    public IReadOnlyList<string> DescribeRemovals(string path, DocumentPackageContext? context = null) => Array.Empty<string>();

    // One line per row, cells separated by a tab. A line break inside a cell is shown as a space.
    internal static string CellText(CsvDocument document) =>
        string.Join("\n", document.Records.Select(r => string.Join("\t", r.Fields.Select(f => Flatten(f.Value)))));

    private static string Flatten(string value) => value.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0 ? value : System.Text.RegularExpressions.Regex.Replace(value, @"[\r\n\t]+", " ");

    public int Transform(string source, string output, IEnumerable<Mapping> mappings, bool forward, DocumentProcessingOptions? options = null, DocumentPackageContext? context = null, bool allowUnappliedMappings = false) =>
        FormatSupport.Run(source, output, () =>
        {
            var loaded = TextEncodingSupport.Read(source);
            var document = CsvDocument.Parse(loaded.Text);
            var list = FormatSupport.Prepare(CellText(document), mappings, forward, allowUnappliedMappings);
            var changed = document.Map(value => FormatSupport.Apply(value, list, forward));
            var text = changed.Serialize();
            if (forward) AuditCsv(document, text, list, true);
            var unknown = forward ? 0 : DocumentProcessor.UnknownTokens(CellText(changed), list);
            FormatSupport.Release(output, TextEncodingSupport.Write(text, loaded.Encoding));
            return unknown;
        });

    public void Audit(string source, string output, IEnumerable<Mapping> mappings)
    {
        var list = mappings.OrderByDescending(m => m.OriginalValue.Length).ToList();
        AuditCsv(CsvDocument.Parse(TextEncodingSupport.Read(source).Text), TextEncodingSupport.Read(output).Text, list, true);
    }

    // The protected text must parse into the same shape, differ from the source only inside cells by the selected
    // replacements, and no mapped value may remain in any cell or anywhere in the raw text.
    private static void AuditCsv(CsvDocument source, string protectedText, List<Mapping> list, bool forward)
    {
        var parsed = CsvDocument.Parse(protectedText, source.Delimiter);
        if (parsed.Records.Count != source.Records.Count) throw new InvalidDataException("Export blocked: the number of rows changed.");
        for (var r = 0; r < source.Records.Count; r++)
        {
            var before = source.Records[r];
            var after = parsed.Records[r];
            if (before.Fields.Count != after.Fields.Count) throw new InvalidDataException("Export blocked: the number of columns changed in row " + (r + 1) + ".");
            if (before.Terminator != after.Terminator) throw new InvalidDataException("Export blocked: a line ending changed in row " + (r + 1) + ".");
            for (var c = 0; c < before.Fields.Count; c++)
            {
                if (before.Fields[c].Quoted != after.Fields[c].Quoted) throw new InvalidDataException("Export blocked: the quoting of a cell changed in row " + (r + 1) + ".");
                if (FormatSupport.Protect(before.Fields[c].Value, list) != after.Fields[c].Value)
                    throw new InvalidDataException("Export blocked: text outside the selected replacements changed in row " + (r + 1) + ", column " + (c + 1) + ".");
            }
        }
        var scanner = FormatSupport.Scanner(list);
        foreach (var record in parsed.Records)
            foreach (var field in record.Fields)
            {
                var hit = FormatSupport.FindResidue(scanner, list, field.Value);
                if (hit != null) throw FormatSupport.ResidueInText(hit, "the file", "a cell");
            }
        var whole = FormatSupport.FindResidue(scanner, list, protectedText);
        if (whole != null) throw FormatSupport.ResidueInText(whole, "the file", "text spread across cells or rows");
    }
}

internal sealed class CsvField
{
    // The cell text without quotes and with "" turned back into ".
    public string Value = "";
    public bool Quoted;
    // The characters as written, quotes included.
    public string Raw = "";
}

internal sealed class CsvRecord
{
    public List<CsvField> Fields = new();
    // The line ending that follows this row exactly as written ("", "\n", "\r\n" or "\r").
    public string Terminator = "";
}

internal sealed class CsvDocument
{
    public char Delimiter = ',';
    public List<CsvRecord> Records = new();

    private static readonly char[] Candidates = { ',', ';', '\t', '|' };

    public static CsvDocument Parse(string text, char? delimiter = null)
    {
        var chosen = delimiter ?? Detect(text);
        var document = ParseWith(text, chosen);
        // Safety net: a document that does not write back to the same characters is never trusted.
        if (document.Serialize() != text) throw new InvalidDataException("This CSV file could not be read exactly as written. It was not processed.");
        return document;
    }

    private static char Detect(string text)
    {
        var best = ',';
        var bestScore = -1;
        foreach (var candidate in Candidates)
        {
            CsvDocument parsed;
            try { parsed = ParseWith(text, candidate, 60); }
            catch (InvalidDataException) { continue; }
            var multi = parsed.Records.Where(r => r.Fields.Count > 1).ToList();
            if (multi.Count == 0) continue;
            var mode = multi.GroupBy(r => r.Fields.Count).OrderByDescending(g => g.Count()).First();
            var score = mode.Count() * 1000 + mode.Key;
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return best;
    }

    private static CsvDocument ParseWith(string text, char delimiter, int maxRecords = int.MaxValue)
    {
        var document = new CsvDocument { Delimiter = delimiter };
        var pos = 0;
        var row = 1;
        while (pos < text.Length && document.Records.Count < maxRecords)
        {
            var record = new CsvRecord();
            while (true)
            {
                var field = new CsvField();
                var start = pos;
                if (pos < text.Length && text[pos] == '"')
                {
                    field.Quoted = true;
                    pos++;
                    var value = new StringBuilder();
                    while (true)
                    {
                        if (pos >= text.Length) throw new InvalidDataException("This CSV file has an opening quote without a closing quote (row " + row + "). Fix the file in a text editor or re-export it, then add it again.");
                        var c = text[pos];
                        if (c == '"')
                        {
                            if (pos + 1 < text.Length && text[pos + 1] == '"') { value.Append('"'); pos += 2; continue; }
                            pos++;
                            break;
                        }
                        value.Append(c);
                        pos++;
                    }
                    if (pos < text.Length && text[pos] != delimiter && text[pos] != '\r' && text[pos] != '\n')
                        throw new InvalidDataException("This CSV file has text right after a closing quote (row " + row + "). Fix the file in a text editor or re-export it, then add it again.");
                    field.Value = value.ToString();
                }
                else
                {
                    while (pos < text.Length && text[pos] != delimiter && text[pos] != '\r' && text[pos] != '\n')
                    {
                        if (text[pos] == '"') throw new InvalidDataException("This CSV file has a quote inside an unquoted cell (row " + row + "). Fix the file in a text editor or re-export it, then add it again.");
                        pos++;
                    }
                    field.Value = text.Substring(start, pos - start);
                }
                field.Raw = text.Substring(start, pos - start);
                record.Fields.Add(field);
                if (pos < text.Length && text[pos] == delimiter) { pos++; continue; }
                break;
            }
            if (pos < text.Length)
            {
                if (text[pos] == '\r' && pos + 1 < text.Length && text[pos + 1] == '\n') { record.Terminator = "\r\n"; pos += 2; }
                else { record.Terminator = text[pos].ToString(); pos++; }
            }
            document.Records.Add(record);
            row++;
            // Row numbers count lines inside quoted cells too, but a user looks for the row, so count records.
        }
        return document;
    }

    public string Serialize()
    {
        var builder = new StringBuilder();
        foreach (var record in Records)
        {
            for (var i = 0; i < record.Fields.Count; i++)
            {
                if (i > 0) builder.Append(Delimiter);
                builder.Append(record.Fields[i].Raw);
            }
            builder.Append(record.Terminator);
        }
        return builder.ToString();
    }

    // A copy in which every cell's text has been passed through the function. Quoted cells stay quoted; a cell
    // that now needs quotes (restored text with a delimiter, quote or line break) gets them.
    public CsvDocument Map(Func<string, string> change)
    {
        var copy = new CsvDocument { Delimiter = Delimiter };
        foreach (var record in Records)
        {
            var next = new CsvRecord { Terminator = record.Terminator };
            foreach (var field in record.Fields)
            {
                var value = change(field.Value);
                if (value == field.Value) { next.Fields.Add(new CsvField { Value = field.Value, Quoted = field.Quoted, Raw = field.Raw }); continue; }
                var needsQuotes = field.Quoted || value.IndexOfAny(new[] { Delimiter, '"', '\r', '\n' }) >= 0;
                next.Fields.Add(new CsvField { Value = value, Quoted = needsQuotes, Raw = needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value });
            }
            copy.Records.Add(next);
        }
        return copy;
    }
}

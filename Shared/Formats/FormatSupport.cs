using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Desensitizer;

// Safety primitives shared by the formats beyond Word and text. They follow the
// rules the Word pipeline follows: mappings are chosen by the same privacy
// policy, a value the policy would drop blocks the export unless the caller
// allowed it, the protected copy is audited in memory, and only then is it
// written to a temporary file next to the output and moved into place. A
// failure removes the temporary file and leaves no output.
internal static class FormatSupport
{
    // Mappings the export applies, longest first. Forward chooses them with the same policy as Word.
    public static List<Mapping> Prepare(string policyText, IEnumerable<Mapping> mappings, bool forward, bool allowUnapplied)
    {
        List<Mapping> chosen;
        if (forward)
        {
            var (applied, dropped) = DocumentProcessor.ContentSafeMappings(policyText, mappings);
            if (dropped.Count > 0 && !allowUnapplied) throw new InvalidDataException(DocumentProcessor.DescribeDropped(dropped));
            chosen = applied;
        }
        else chosen = mappings.ToList();
        return chosen.OrderByDescending(m => (forward ? m.OriginalValue : m.Token).Length).ToList();
    }

    public static ResidueScanner Scanner(List<Mapping> list) => new(list, DocumentProcessor.ReplacementTokens(list));

    // Values absent from visible text can still survive in hidden package channels.
    // Keep explicit generic-text exemptions unchanged, but never drop a hidden-only value from the audit.
    public static List<Mapping> AuditMappings(string policyText, List<Mapping> applied, IEnumerable<Mapping> requested) =>
        applied.Concat(requested.Where(m => !string.IsNullOrWhiteSpace(m.OriginalValue) &&
            !System.Text.RegularExpressions.Regex.IsMatch(policyText, DocumentPrivacyPolicy.Pattern(m.OriginalValue.Trim(), true),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))))
        .GroupBy(m => m.Category + "|" + Project.Normalize(m.OriginalValue)).Select(g => g.First()).ToList();

    public static void AuditHiddenPackage(OoxmlPackage package, List<Mapping> list)
    {
        var scanner = Scanner(list);
        foreach (var name in package.Names)
        {
            if (scanner.ContainsAny(name) || scanner.FindHiddenVariant(name) != null)
                throw new InvalidDataException("Export blocked: an identifying value remains in a package part name. No file was released.");
            var document = package.Xml(name);
            // Standalone audits can receive externally altered copies that did
            // not pass through our writer. Comments and processing instructions
            // are unsupported output channels even when their contents are benign.
            if (document.DescendantNodes().Any(n => n is System.Xml.Linq.XComment or System.Xml.Linq.XProcessingInstruction))
                throw new InvalidDataException("Export blocked: hidden XML annotations remain in the protected copy. No file was released. Create a new protected copy from the source document.");
            if (scanner.Find(document, _ => true) != null || scanner.FindPackageVariant(document) != null)
                throw new InvalidDataException("Export blocked: an identifying value remains in hidden or encoded package content. No file was released. Remove the value from the source document, then try again.");
        }
    }

    // The protected form of one piece of text (a line, a cell, a string).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<Mapping>, ResidueScanner> Quick = new();

    // Text without any selected value is returned as is, without building a pattern per mapping. Large sheets have many cells.
    public static string Protect(string text, List<Mapping> list)
    {
        if (text.Length == 0 || list.Count == 0) return text;
        if (!Quick.GetValue(list, l => Scanner(l)).ContainsAny(text)) return text;
        return DocumentProcessor.ExpectedProtectedText(text, list);
    }
    public static string Restore(string text, List<Mapping> list)
    {
        if (text.Length == 0 || !list.Any(m => m.Token.Length > 0 && text.Contains(m.Token, StringComparison.Ordinal))) return text;
        return DocumentProcessor.Replace(text, list, false);
    }

    public static string Apply(string text, List<Mapping> list, bool forward) => forward ? Protect(text, list) : Restore(text, list);

    public static string CanonicalSpace(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    // Runs a transform so that nothing but a friendly sentence escapes: library messages can quote document text.
    public static int Run(string source, string output, Func<int> body)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output must be a different file from the source. The source is never overwritten.");
        try { return body(); }
        catch (Exception ex) when (!(ex is InvalidDataException || ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is OperationCanceledException || ex is ObjectDisposedException))
        {
            throw new InvalidDataException("The document could not be processed safely (" + ex.GetType().Name + "). Nothing was released.");
        }
    }

    // Writes finished, audited bytes: temporary file next to the output, then one move.
    public static void Release(string output, byte[] data)
    {
        var temp = output + "." + Guid.NewGuid().ToString("N") + DocumentProcessor.TempSuffix;
        try
        {
            File.WriteAllBytes(temp, data);
            DocumentProcessor.MoveInto(temp, output);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    // Names the token and the place, never the original value.
    public static InvalidDataException Residue(ResidueHit hit, string part, string where)
    {
        var location = hit.InText ? "text" : "the " + hit.Attribute!.Name.LocalName + " attribute";
        return new InvalidDataException("Export blocked: a value selected for replacement (" + hit.Mapping.Category + ", replacement " + hit.Mapping.Token + ") remains in " + part + " (" + (where.Length > 0 ? where : location) + "). No file was released. Review this mapping or remove the value from the source document, then try again.");
    }

    public static InvalidDataException ResidueInText(Mapping mapping, string part, string where) =>
        new("Export blocked: a value selected for replacement (" + mapping.Category + ", replacement " + mapping.Token + ") remains in " + part + " (" + where + "). No file was released. Review this mapping or remove the value from the source document, then try again.");

    // The first mapping whose value is still present in the text, or null.
    public static Mapping? FindResidue(ResidueScanner scanner, List<Mapping> list, string text)
    {
        if (!scanner.ContainsAny(text)) return null;
        var masked = scanner.Mask(text);
        foreach (var mapping in list.Where(m => !string.IsNullOrWhiteSpace(m.OriginalValue)))
            if (System.Text.RegularExpressions.Regex.IsMatch(masked, DocumentPrivacyPolicy.Pattern(mapping.OriginalValue, true), System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return mapping;
        return list.First(m => !string.IsNullOrWhiteSpace(m.OriginalValue));
    }
}

// Reads and writes whole text files the way the .txt path does (byte order marks
// are honored and kept), and additionally handles the ANSI files that older
// spreadsheet programs write. A file that is neither valid UTF-8 nor valid
// Windows-1252 is refused instead of being read with replacement characters.
internal static class TextEncodingSupport
{
    public sealed record Loaded(string Text, Encoding Encoding, int PreambleLength);

    public static Loaded Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Encoding encoding;
        int skip;
        if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) { encoding = new UTF8Encoding(true, true); skip = 3; }
        else if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        else
        {
            skip = 0;
            try { new UTF8Encoding(false, true).GetString(bytes); encoding = new UTF8Encoding(false, true); }
            catch (DecoderFallbackException) { encoding = Ansi(); }
        }
        try { return new Loaded(encoding.GetString(bytes, skip, bytes.Length - skip), encoding, skip); }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("This file is not valid text in a supported encoding (UTF-8, UTF-16 or Windows-1252). Save a copy as UTF-8 and add that copy.");
        }
    }

    private static Encoding Ansi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    // Preamble plus text. Throws a friendly message when a restored value cannot be written in the file's encoding.
    public static byte[] Write(string text, Encoding encoding)
    {
        try
        {
            var preamble = encoding.GetPreamble();
            var body = encoding.GetBytes(text);
            var all = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, all, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, all, preamble.Length, body.Length);
            return all;
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidDataException("A restored value contains characters this file's encoding cannot hold. Nothing was released. Save the protected copy as UTF-8 and restore that copy.");
        }
    }
}

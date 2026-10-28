using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Desensitizer;

public sealed class WordMarkupSettings
{
    public DocumentProcessingOptions Options { get; set; } = new();
    // Maps picture alt text with the document mappings. Null leaves alt text out.
    public Func<string, string>? MapAltText { get; set; }
    // Renames bookmarks and the references to them. Null renames nothing.
    public BookmarkMap? Bookmarks { get; set; }
}

// Removes content that can carry data outside the visible text: pictures and
// objects (replaced by placeholders), external hyperlinks and field targets,
// and bookmark names. Internal links survive with renamed targets.
public static class WordMarkupSanitizer
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static void Apply(XDocument doc, WordMarkupSettings settings)
    {
        ImagePlaceholders.ReplaceAll(doc, settings.Options, settings.MapAltText);
        WordHiddenChannels.Strip(doc);
        RenameBookmarks(doc, settings.Bookmarks);
        ResolveHyperlinks(doc, settings.Bookmarks);
        ResolveFields(doc, settings.Bookmarks);
    }

    private static void RenameBookmarks(XDocument doc, BookmarkMap? bookmarks)
    {
        if (bookmarks == null) return;
        foreach (var start in doc.Descendants().Where(e => e.Name.LocalName == "bookmarkStart"))
            foreach (var name in start.Attributes().Where(a => a.Name.LocalName == "name"))
                name.Value = bookmarks.Rename(name.Value);
    }

    // External hyperlinks are unwrapped to their text. A link to a bookmark in
    // this document (w:anchor without a relationship id) is kept, with only its
    // anchor, so tables of contents and cross references still jump.
    private static void ResolveHyperlinks(XDocument doc, BookmarkMap? bookmarks)
    {
        foreach (var link in doc.Descendants().Where(x => x.Name.LocalName == "hyperlink").ToList())
        {
            var anchor = link.Attribute(W + "anchor");
            var external = link.Attributes().Any(a => a.Name.LocalName is "id" && a.Name.Namespace != W);
            if (bookmarks != null && anchor != null && !string.IsNullOrWhiteSpace(anchor.Value) && !external)
            {
                var name = bookmarks.Rename(anchor.Value);
                link.Attributes().Where(a => a != anchor && a.Name != W + "history" && !a.IsNamespaceDeclaration).Remove();
                anchor.Value = name;
                continue;
            }
            link.ReplaceWith(link.Nodes().ToArray());
        }
    }

    private static void ResolveFields(XDocument doc, BookmarkMap? bookmarks)
    {
        foreach (var simple in doc.Descendants().Where(x => x.Name.LocalName == "fldSimple").ToList())
        {
            var instruction = simple.Attributes().FirstOrDefault(a => a.Name.LocalName == "instr");
            if (instruction == null) continue;
            // A field that computes text from data (MERGEFIELD, DOCPROPERTY, IF, ...) keeps its last result and loses its instruction.
            if (!FieldInstructions.IsKept(instruction.Value)) { simple.ReplaceWith(simple.Nodes().ToArray()); continue; }
            var result = FieldInstructions.Resolve(instruction.Value, bookmarks);
            if (result.Action == FieldAction.Remove) instruction.Remove();
            else if (result.Action == FieldAction.Rewrite) instruction.Value = result.Text;
        }
        var stack = new Stack<FieldState>();
        foreach (var element in doc.Descendants().ToList())
        {
            switch (element.Name.LocalName)
            {
                case "fldChar":
                    var type = element.Attribute(W + "fldCharType")?.Value;
                    if (type == "begin") stack.Push(new FieldState());
                    else if (type == "separate" && stack.Count > 0 && stack.Peek().InInstruction) { Finish(stack.Peek(), bookmarks); stack.Peek().InInstruction = false; }
                    else if (type == "end" && stack.Count > 0)
                    {
                        var field = stack.Pop();
                        if (field.InInstruction) Finish(field, bookmarks);
                    }
                    break;
                case "instrText":
                    if (stack.Count > 0 && stack.Peek().InInstruction) stack.Peek().Nodes.Add(element);
                    else Finish(new FieldState { Nodes = { element } }, bookmarks);
                    break;
            }
        }
        // A field that never closes still has its instruction handled.
        while (stack.Count > 0)
        {
            var field = stack.Pop();
            if (field.InInstruction) Finish(field, bookmarks);
        }
    }

    private sealed class FieldState
    {
        public List<XElement> Nodes { get; } = new();
        public bool InInstruction { get; set; } = true;
    }

    // An instruction can be split across several runs. Treat it as one string,
    // then write the result back into the first run and empty the others.
    private static void Finish(FieldState field, BookmarkMap? bookmarks)
    {
        if (field.Nodes.Count == 0) return;
        var combined = string.Concat(field.Nodes.Select(n => n.Value));
        var result = FieldInstructions.Resolve(combined, bookmarks);
        if (result.Action == FieldAction.Keep) return;
        var text = result.Action == FieldAction.Rewrite ? result.Text : "";
        field.Nodes[0].Value = text;
        if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])))
            field.Nodes[0].SetAttributeValue(XNamespace.Xml + "space", "preserve");
        foreach (var extra in field.Nodes.Skip(1)) extra.Value = "";
    }
}

public enum FieldAction { Keep, Rewrite, Remove }

public readonly record struct FieldResult(FieldAction Action, string Text);

// Reads field instructions well enough to find bookmark names. Anything that
// can point outside the document is removed, as before.
public static class FieldInstructions
{
    private sealed record Token(int Start, int End, string Value, bool Quoted);

    // Fields that number, link or lay out the page. They stay working.
    private static readonly HashSet<string> StructuralCodes = new(StringComparer.Ordinal)
    {
        "PAGE", "NUMPAGES", "SECTIONPAGES", "SECTION", "TOC", "REF", "PAGEREF", "NOTEREF", "SEQ", "AUTONUM", "AUTONUMLGL", "AUTONUMOUT", "LISTNUM", "HYPERLINK"
    };

    // True for an instruction that is structural or empty and is therefore kept (HYPERLINK is resolved separately).
    public static bool IsKept(string instruction)
    {
        var tokens = Tokenize(instruction);
        return tokens.Count == 0 || StructuralCodes.Contains(tokens[0].Value.ToUpperInvariant());
    }

    public static FieldResult Resolve(string instruction, BookmarkMap? bookmarks)
    {
        var tokens = Tokenize(instruction);
        var code = tokens.Count > 0 ? tokens[0].Value.ToUpperInvariant() : "";
        if (code == "HYPERLINK") return ResolveHyperlink(instruction, tokens, bookmarks);
        // Only fields that build page structure keep their instruction. Any other
        // instruction (MERGEFIELD, DOCPROPERTY, IF, INCLUDETEXT, ...) can hold
        // names, values or paths that no detector reads, so it is blanked and the
        // field keeps its last result text.
        if (tokens.Count > 0 && !StructuralCodes.Contains(code)) return new FieldResult(FieldAction.Remove, "");
        // Never leave a link target behind in an unexpected position.
        if (instruction.IndexOf("HYPERLINK", StringComparison.OrdinalIgnoreCase) >= 0) return new FieldResult(FieldAction.Remove, "");
        if (bookmarks == null || tokens.Count == 0) return new FieldResult(FieldAction.Keep, instruction);
        Token? target = null;
        switch (code)
        {
            case "REF" or "PAGEREF" or "NOTEREF":
                target = tokens.Skip(1).FirstOrDefault(t => t.Quoted || !t.Value.StartsWith("\\", StringComparison.Ordinal));
                break;
            case "TOC":
                for (var i = 1; i + 1 < tokens.Count; i++)
                    if (!tokens[i].Quoted && tokens[i].Value.Equals("\\b", StringComparison.OrdinalIgnoreCase)) { target = tokens[i + 1]; break; }
                break;
        }
        if (target == null || target.Value.Length == 0) return new FieldResult(FieldAction.Keep, instruction);
        var renamed = bookmarks.Rename(target.Value);
        return renamed == target.Value ? new FieldResult(FieldAction.Keep, instruction) : new FieldResult(FieldAction.Rewrite, Replace(instruction, target, renamed));
    }

    // HYPERLINK "url" is blanked. HYPERLINK \l "bookmark" alone is an internal
    // link and keeps working with the renamed bookmark.
    private static FieldResult ResolveHyperlink(string instruction, List<Token> tokens, BookmarkMap? bookmarks)
    {
        Token? anchor = null;
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Quoted || !token.Value.StartsWith("\\", StringComparison.Ordinal)) return new FieldResult(FieldAction.Remove, "");
            if (token.Value.Equals("\\l", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count) { anchor = tokens[++i]; continue; }
            if (token.Value.Equals("\\m", StringComparison.OrdinalIgnoreCase) || token.Value.Equals("\\n", StringComparison.OrdinalIgnoreCase)) continue;
            return new FieldResult(FieldAction.Remove, "");
        }
        if (bookmarks == null || anchor == null || anchor.Value.Length == 0) return new FieldResult(FieldAction.Remove, "");
        var renamed = bookmarks.Rename(anchor.Value);
        return renamed == anchor.Value ? new FieldResult(FieldAction.Keep, instruction) : new FieldResult(FieldAction.Rewrite, Replace(instruction, anchor, renamed));
    }

    private static string Replace(string instruction, Token token, string name)
    {
        var replacement = token.Quoted ? "\"" + name + "\"" : name;
        return instruction.Substring(0, token.Start) + replacement + instruction.Substring(token.End);
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                if (close < 0) close = text.Length - 1;
                tokens.Add(new Token(i, close + 1, text.Substring(i + 1, Math.Max(0, close - i - 1)), true));
                i = close + 1;
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            tokens.Add(new Token(start, i, text.Substring(start, i - start), false));
        }
        return tokens;
    }
}

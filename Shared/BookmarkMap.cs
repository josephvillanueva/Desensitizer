using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Desensitizer;

// Bookmark names can carry identifying text, so they are renamed to neutral
// names (_bm0001, _bm0002, ...) while every reference to them is rewritten with
// the same map, which keeps REF, PAGEREF, NOTEREF, HYPERLINK \l, TOC \b and
// internal hyperlinks working. Word generates hidden names such as _Toc123456
// and _Ref123456; those are kept as they are unless they contain a mapped value.
// One map covers a whole package, so a reference in a header resolves to a
// bookmark in the body. Renaming is deterministic: names are numbered in the
// order they are first seen, and the map is idempotent for names it generated.
public sealed class BookmarkMap
{
    private static readonly Regex WordGenerated = new(@"^(?:_(?:Toc|Ref|Hlk|Hlt|RefNum)\d+|_GoBack)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Func<string, bool> containsMappedValue;
    private readonly Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> generated = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
    private int counter;

    public BookmarkMap(Func<string, bool> containsMappedValue) { this.containsMappedValue = containsMappedValue; }

    // Original name to name in the output. Includes kept names.
    public IReadOnlyDictionary<string, string> Renames => map;

    // Registers the bookmarks of a part so generated names cannot collide with them.
    public void Collect(XDocument document)
    {
        foreach (var start in document.Descendants().Where(e => e.Name.LocalName == "bookmarkStart"))
        {
            var name = start.Attributes().FirstOrDefault(a => a.Name.LocalName == "name")?.Value;
            if (!string.IsNullOrEmpty(name)) taken.Add(name);
        }
    }

    public string Rename(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (generated.Contains(name)) return name;
        if (map.TryGetValue(name, out var existing)) return existing;
        if (WordGenerated.IsMatch(name) && !containsMappedValue(name)) return map[name] = name;
        string next;
        do { next = "_bm" + (++counter).ToString("D4"); } while (taken.Contains(next));
        generated.Add(next);
        taken.Add(next);
        return map[name] = next;
    }
}

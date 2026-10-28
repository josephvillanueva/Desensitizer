using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Desensitizer;

// Tracked changes and comments are resolved before text is read or exported:
// insertions and move destinations are accepted, deletions and move sources are
// dropped, formatting revisions are dropped and comments are removed. That is
// intentional, but it is silent, so DocumentProcessor.InspectReviewMarkup(path)
// reports what will be resolved. A caller can show ReviewMarkupSummary.Describe()
// as a warning before the user scans or exports the file.
public sealed class ReviewMarkupSummary
{
    public static readonly ReviewMarkupSummary Empty = new();

    // Content insertions that are accepted into the final text.
    public int Insertions { get; set; }
    // Content deletions that are dropped from the final text.
    public int Deletions { get; set; }
    // Moves. One move has a source (dropped) and a destination (kept), counted once.
    public int Moves { get; set; }
    // Paragraph-mark insertions and deletions. Those revisions are discarded
    // without merging or splitting paragraphs.
    public int ParagraphMarkChanges { get; set; }
    // Property revisions (run, paragraph, section, table formatting). The
    // current formatting is kept and the history is dropped.
    public int FormattingChanges { get; set; }
    // Comments dropped with their anchors. Comment text is never read or exported.
    public int CommentsRemoved { get; set; }
    // Tracked table-cell insertions, deletions and merges. Those cannot be
    // resolved automatically, so reading and export are refused.
    public int TableCellChanges { get; set; }
    public int InsertedCharacters { get; set; }
    public int DeletedCharacters { get; set; }

    public bool HasReviewMarkup => Insertions + Deletions + Moves + ParagraphMarkChanges + FormattingChanges + CommentsRemoved + TableCellChanges > 0;
    public bool HasTextAffectingChanges => Insertions + Deletions + Moves > 0;

    // One plain sentence for a warning, without em dashes. Empty when the file has no review markup.
    public string Describe()
    {
        if (!HasReviewMarkup) return "";
        var parts = new List<string>();
        void Add(int count, string singular, string plural) { if (count > 0) parts.Add(count + " " + (count == 1 ? singular : plural)); }
        Add(Insertions, "tracked insertion accepted", "tracked insertions accepted");
        Add(Deletions, "tracked deletion dropped", "tracked deletions dropped");
        Add(Moves, "tracked move resolved", "tracked moves resolved");
        Add(ParagraphMarkChanges, "paragraph mark revision ignored", "paragraph mark revisions ignored");
        Add(FormattingChanges, "formatting revision dropped", "formatting revisions dropped");
        Add(CommentsRemoved, "comment removed", "comments removed");
        Add(TableCellChanges, "tracked table cell change that needs manual resolution", "tracked table cell changes that need manual resolution");
        return "This document has review markup that is resolved automatically: " + string.Join(", ", parts) + ". Accept or reject changes in Word first if the final text should differ.";
    }
}

public static class ReviewMarkup
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // Accepts insertions and drops deletions, moves out, comments and revision
    // history, in place. This is the single definition used when reading,
    // exporting and auditing.
    public static void Normalize(XDocument doc)
    {
        foreach (var node in doc.Descendants().Where(x => x.Name.Namespace == W && (x.Name.LocalName is "del" or "delText" or "moveFrom" or "moveFromRangeStart" or "moveFromRangeEnd" or "commentRangeStart" or "commentRangeEnd" or "commentReference" || x.Name.LocalName.EndsWith("Change", StringComparison.Ordinal))).ToList()) node.Remove();
        foreach (var node in doc.Descendants().Where(x => x.Name.Namespace == W && (x.Name.LocalName is "ins" or "moveTo")).ToList()) node.ReplaceWith(node.Nodes().ToArray());
        foreach (var node in doc.Descendants().Where(x => x.Name.Namespace == W && (x.Name.LocalName is "moveToRangeStart" or "moveToRangeEnd")).ToList()) node.Remove();
    }

    // Counts the markup Normalize would resolve. Parts are not modified.
    public static ReviewMarkupSummary Inspect(IEnumerable<XDocument> contentParts, IEnumerable<XDocument> commentParts)
    {
        var summary = new ReviewMarkupSummary();
        var referencedComments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in contentParts)
        {
            foreach (var element in part.Descendants().Where(x => x.Name.Namespace == W))
            {
                var name = element.Name.LocalName;
                var markOnly = element.Parent?.Name == W + "rPr";
                switch (name)
                {
                    case "ins":
                        if (markOnly) summary.ParagraphMarkChanges++;
                        else
                        {
                            summary.Insertions++;
                            summary.InsertedCharacters += element.Descendants(W + "t").Sum(t => t.Value.Length);
                        }
                        break;
                    case "del":
                        if (markOnly) summary.ParagraphMarkChanges++;
                        else
                        {
                            summary.Deletions++;
                            summary.DeletedCharacters += element.Descendants(W + "delText").Sum(t => t.Value.Length);
                        }
                        break;
                    case "moveFrom":
                        summary.Moves++;
                        break;
                    case "cellIns" or "cellDel" or "cellMerge":
                        summary.TableCellChanges++;
                        break;
                    case "commentReference":
                        var id = element.Attribute(W + "id")?.Value;
                        if (id != null) referencedComments.Add(id);
                        break;
                    default:
                        if (name.EndsWith("Change", StringComparison.Ordinal)) summary.FormattingChanges++;
                        break;
                }
            }
        }
        var commentElements = commentParts.Sum(part => part.Descendants(W + "comment").Count());
        summary.CommentsRemoved = Math.Max(commentElements, referencedComments.Count);
        return summary;
    }
}

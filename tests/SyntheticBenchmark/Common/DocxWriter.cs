using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace SyntheticBenchmark;

// Writes native Word content: paragraphs, tables, grid spans, vertical merges and split runs,
// so that detectors see text the way real documents present it.
public static class DocxWriter
{
    public static void Write(GeneratedDoc doc, string path, int seed)
    {
        var rng = new Rng(seed * 7919L + doc.Truth.Id.Sum(ch => (long)ch) * 31);
        using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        var body = new Body();
        foreach (var block in doc.Blocks)
        {
            if (block is ParaBlock p) body.Append(Para(p.Text, p.Heading, false, rng));
            else if (block is TableBlock t) body.Append(MakeTable(t, rng));
        }
        main.Document = new Document(body);
        main.Document.Save();
    }

    static Paragraph Para(string text, bool heading, bool bold, Rng rng)
    {
        var para = new Paragraph();
        foreach (var run in Runs(text, heading, bold, rng)) para.Append(run);
        return para;
    }

    // Split about a third of the longer texts into two or three runs at arbitrary points (even mid-word).
    static IEnumerable<Run> Runs(string text, bool heading, bool bold, Rng rng)
    {
        var pieces = new List<string>();
        if (text.Length > 12 && rng.Chance(0.33))
        {
            var a = 3 + rng.Next(text.Length - 6);
            if (rng.Chance(0.4) && text.Length > 24)
            {
                var b = Math.Min(text.Length - 2, a + 3 + rng.Next(Math.Max(1, text.Length - a - 4)));
                pieces.Add(text.Substring(0, a)); pieces.Add(text.Substring(a, b - a)); pieces.Add(text.Substring(b));
            }
            else { pieces.Add(text.Substring(0, a)); pieces.Add(text.Substring(a)); }
        }
        else pieces.Add(text);
        foreach (var piece in pieces)
        {
            var run = new Run();
            if (heading || bold) run.Append(new RunProperties(new Bold()));
            run.Append(new DocumentFormat.OpenXml.Wordprocessing.Text(piece) { Space = SpaceProcessingModeValues.Preserve });
            yield return run;
        }
    }

    static DocumentFormat.OpenXml.Wordprocessing.Table MakeTable(TableBlock t, Rng rng)
    {
        var cols = t.Rows.Max(r => r.Sum(c => c.Span));
        var table = new DocumentFormat.OpenXml.Wordprocessing.Table();
        table.Append(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));
        var grid = new TableGrid();
        for (var i = 0; i < cols; i++) grid.Append(new GridColumn());
        table.Append(grid);
        var firstRow = true;
        foreach (var row in t.Rows)
        {
            var tr = new TableRow();
            if (firstRow && row.All(c => c.Bold)) tr.Append(new TableRowProperties(new TableHeader()));
            firstRow = false;
            foreach (var cell in row)
            {
                var props = new TableCellProperties();
                if (cell.Span > 1) props.Append(new GridSpan { Val = cell.Span });
                if (cell.VMerge == "restart") props.Append(new VerticalMerge { Val = MergedCellValues.Restart });
                else if (cell.VMerge == "continue") props.Append(new VerticalMerge());
                var tc = new TableCell(props);
                tc.Append(cell.VMerge == "continue" ? new Paragraph() : Para(cell.Text, false, cell.Bold, rng));
                tr.Append(tc);
            }
            table.Append(tr);
        }
        return table;
    }
}

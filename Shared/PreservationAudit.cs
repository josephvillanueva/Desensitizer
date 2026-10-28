using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Desensitizer;

// Checks transformation fidelity, not whether the selected spans were correct.
public static class PreservationAudit
{
    private static readonly XNamespace W="http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // Optional contexts let a caller that already parsed the source or the output
    // reuse those parts. A context that is not supplied is opened and closed here.
    public static void AssertPreserved(string source,string output,IEnumerable<Mapping> mappings,Func<string,string>? expectedReplacement=null,DocumentProcessingOptions? options=null,DocumentPackageContext? sourceContext=null,DocumentPackageContext? outputContext=null)
    {
        var selected=mappings.ToList();
        expectedReplacement??=paragraph=>DocumentProcessor.ExpectedProtectedText(paragraph,selected);
        options??=new DocumentProcessingOptions();
        if(Path.GetExtension(source).Equals(".txt",StringComparison.OrdinalIgnoreCase))
        {
            var expected=string.Join("\n",File.ReadAllText(source).Split('\n').Select(expectedReplacement));
            if(expected!=File.ReadAllText(output))throw new InvalidDataException("Export blocked: text outside the selected replacements changed.");
            return;
        }
        using var ownedSource=sourceContext==null?DocumentPackageContext.Open(source):null;
        using var ownedOutput=outputContext==null?DocumentPackageContext.Open(output):null;
        var before=sourceContext??ownedSource!;
        var after=outputContext??ownedOutput!;
        foreach(var part in before.EntryNames.Where(IsVisiblePart))
        {
            if(!after.EntryNames.Contains(part))throw new InvalidDataException("Export blocked: a text-bearing Word part was removed: "+part);
            // Work on copies: the contexts share their parsed parts.
            var original=new XDocument(before.GetDocument(part));
            var protectedPart=new XDocument(after.GetDocument(part));
            ReviewMarkup.Normalize(original);ReviewMarkup.Normalize(protectedPart);
            // Pictures and embedded objects are removed. Each leaves one placeholder
            // that must appear, unchanged and in order, in the output. Alt text in a
            // placeholder is compared after the same mapping replacement.
            var placeholders=ImagePlaceholders.ExpectedPlaceholders(original,options,expectedReplacement);
            if(!ImagePlaceholders.ConsumePlaceholders(protectedPart,placeholders))
                throw new InvalidDataException("Export blocked: an image placeholder is missing or changed in "+part+".");
            if(protectedPart.Descendants().Any(ImagePlaceholders.IsObject))
                throw new InvalidDataException("Export blocked: an image or embedded object remains in "+part+".");
            var sourceParagraphs=Paragraphs(original);var outputParagraphs=Paragraphs(protectedPart);
            if(sourceParagraphs.Length!=outputParagraphs.Length)
                throw new InvalidDataException("Export blocked: the paragraph structure changed in "+part+".");
            for(var i=0;i<sourceParagraphs.Length;i++)
                if(CanonicalText(expectedReplacement(sourceParagraphs[i]))!=CanonicalText(outputParagraphs[i]))
                {
                    var error=new InvalidDataException("Export blocked: text outside selected replacements changed in "+part+", paragraph "+(i+1)+". No file was released.");
                    // Only the location is kept. The paragraph text is document content and must not travel in the exception.
                    error.Data["part"]=part;error.Data["paragraph"]=i+1;
                    throw error;
                }
            if(!Shape(original).SequenceEqual(Shape(protectedPart)))
                throw new InvalidDataException("Export blocked: table rows, cells or merge relationships changed in "+part+".");
        }
    }

    private static bool IsVisiblePart(string name)=>name.Equals("word/document.xml",StringComparison.OrdinalIgnoreCase)||
        name.StartsWith("word/header",StringComparison.OrdinalIgnoreCase)&&name.EndsWith(".xml",StringComparison.OrdinalIgnoreCase)||
        name.StartsWith("word/footer",StringComparison.OrdinalIgnoreCase)&&name.EndsWith(".xml",StringComparison.OrdinalIgnoreCase)||
        name.Equals("word/footnotes.xml",StringComparison.OrdinalIgnoreCase)||name.Equals("word/endnotes.xml",StringComparison.OrdinalIgnoreCase);
    private static string[] Paragraphs(XDocument document)=>document.Descendants(W+"p").Select(p=>string.Concat(p.Descendants().Where(n=>n.Name.Namespace==W&&n.Name.LocalName is "t" or "tab" or "br" or "cr").Select(n=>n.Name.LocalName=="t"?n.Value:" "))).ToArray();
    // SDK serialization can change revision/compatibility attributes. Compare
    // ordered structure and actual merge/grid values, not incidental metadata.
    private static IEnumerable<string> Shape(XDocument document)=>document.Descendants().Where(n=>n.Name.Namespace==W&&n.Name.LocalName is "tbl" or "tr" or "tc" or "gridSpan" or "vMerge" or "gridBefore" or "gridAfter").Select(n=>n.Name.LocalName+"|"+(n.Name.LocalName is "gridSpan" or "vMerge" or "gridBefore" or "gridAfter"?n.Attribute(W+"val")?.Value??"":""));
    private static string CanonicalText(string text)=>Regex.Replace(text,@"\s+"," ").Trim();
}

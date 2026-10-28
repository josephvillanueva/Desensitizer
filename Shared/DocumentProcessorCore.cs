using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Validation;

namespace Desensitizer
{
 // The processing pipeline shared by both apps. Each app's DocumentProcessor.cs
 // holds only the two small differences: StripVolatileMetadata and IsAuditedAttribute.
 //
 // Export (Desensitize) never writes unredacted content to disk. The protected
 // package is built in memory from the source parts, sanitised, rewritten to the
 // allow-list of parts, audited, and only then written to a temporary file next
 // to the output and moved into place. Any failure removes the temporary file
 // and leaves no output.
 public static partial class DocumentProcessor
 {
  // Suffix of the temporary files this class creates next to an output.
  public const string TempSuffix=".dstmp";
  private static readonly Regex AppTempName=new(@"^Desensitizer-[0-9a-f]{32}\.(?:docx|txt|xlsx|pptx|csv|md)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);

  // Opens the document once for several operations. Pass the context to ReadText,
  // ReadTableContext, InspectReviewMarkup and Desensitize so each part is read
  // and parsed once, then dispose it:
  //   using var package=DocumentProcessor.OpenContext(path);
  //   var text=DocumentProcessor.ReadText(path,package);
  //   var tables=DocumentProcessor.ReadTableContext(path,text,package);
  // Every method still works without a context and then opens its own.
  public static DocumentPackageContext OpenContext(string path)
  {
   if(Ext(path)!=".docx")throw new InvalidDataException("Only .docx files can be opened as a Word package.");
   return DocumentPackageContext.Open(path);
  }
  public static string ReadText(string path,DocumentPackageContext? context=null)
  {
   Check(path,context);
   if(DocumentFormats.Extended(path) is{} extended)return extended.ReadText(path,context);
   if(Ext(path)==".txt")return ReadTextFile(path);
   using var owned=context==null?DocumentPackageContext.Open(path):null;
   return (context??owned!).ExtractText();
  }
  public static TableReviewContext? ReadTableContext(string path,string text,DocumentPackageContext? context=null)
  {
   Check(path,context);if(DocumentFormats.Extended(path) is{} extended)return extended.ReadTableContext(path,text,context);if(Ext(path)==".txt")return null;
   using var owned=context==null?DocumentPackageContext.Open(path):null;
   var package=context??owned!;
   var parts=package.VisibleWordXmlParts.Select(name=>package.GetFinalView(name)).ToArray();
   return TableReviewContext.Create(parts,text);
  }
  // Tracked changes and comments are resolved silently when a file is read or
  // exported: insertions are accepted, deletions and comments are dropped. This
  // reports what will be resolved, without changing anything, so the interface
  // can warn before the user relies on the extracted text. See ReviewMarkupSummary.
  public static ReviewMarkupSummary InspectReviewMarkup(string path,DocumentPackageContext? context=null)
  {
   var extension=Ext(path);
   if(DocumentFormats.Extended(path) is{} extended)return extended.InspectReviewMarkup(path,context);
   if(extension==".txt")return ReviewMarkupSummary.Empty;
   if(extension!=".docx")throw new InvalidDataException("Only supported document files can be inspected.");
   using var owned=context==null?DocumentPackageContext.Open(path):null;
   var package=context??owned!;
   var content=package.VisibleWordXmlParts.Select(name=>package.GetDocument(name)).ToArray();
   var comments=package.EntryNames.Where(name=>name.Equals("word/comments.xml",StringComparison.OrdinalIgnoreCase)).Select(name=>package.GetDocument(name)).ToArray();
   return ReviewMarkup.Inspect(content,comments);
  }
  // Text that Word keeps outside the visible paragraphs, as labelled lines such
  // as "sdt alias: ...", "document variable value: ...", "field code: ...",
  // "glossary entry name: ...", "list number text: ...", "table caption: ...".
  // The export removes these channels, so a value found only here cannot ship.
  // Callers that want detectors to see them (so the value is also mapped, not
  // only removed) can append these lines to the text they scan. The interface
  // does not do this yet; that wiring belongs to a later UI stage.
  public static IReadOnlyList<string> ReadHiddenChannels(string path,DocumentPackageContext? context=null)
  {
   Check(path,context);
   if(DocumentFormats.Extended(path) is{} extended)return extended.ReadHiddenChannels(path,context);
   if(Ext(path)==".txt")return Array.Empty<string>();
   using var owned=context==null?DocumentPackageContext.Open(path):null;
   var package=context??owned!;
   var lines=new List<string>();
   foreach(var name in package.WordXmlParts.Where(n=>!n.Contains("word/comments",StringComparison.OrdinalIgnoreCase)&&!n.Contains("word/people.xml",StringComparison.OrdinalIgnoreCase)))
    foreach(var line in WordHiddenChannels.Describe(package.GetDocument(name)))lines.Add(line);
   return lines;
  }
  // allowUnappliedMappings: an approved saved (non-manual) mapping that the
  // privacy policy drops, and whose value is present in this document, is not
  // applied and not audited. By default that stops the export. Pass true only
  // when the caller has shown the user the warning and the user chose to go on.
  public static void Desensitize(string source,string output,IEnumerable<Mapping> mappings,DocumentProcessingOptions? options=null,DocumentPackageContext? context=null,bool allowUnappliedMappings=false) => Transform(source,output,mappings,true,out _,options,context,allowUnappliedMappings);

  // Mappings the policy keeps, and approved saved mappings it dropped.
  internal static (List<Mapping> Applied,List<Mapping> Dropped) ContentSafeMappings(string text,IEnumerable<Mapping> mappings)
  {
   // Only exact retained spans use a saved token. Rescan discovers narrowed entities.
   var selected=mappings.ToList();
   var applied=new List<Mapping>();var dropped=new List<Mapping>();
   var safe=DocumentPrivacyPolicy.Apply(text,selected.Select(m=>new Candidate{Value=m.OriginalValue,Category=m.Category}));
   foreach(var mapping in selected)
   {
    if(mapping.IsManual){applied.Add(mapping);continue;}
    var candidate=new Candidate{Value=mapping.OriginalValue,Category=mapping.Category};
    var normalized=safe.FirstOrDefault(c=>c.Category==mapping.Category&&Project.Normalize(c.Value)==Project.Normalize(candidate.Value));
    if(normalized==null)
    {
     // A value that is not in this document, or is already a token, is not a loss.
     if(!string.IsNullOrWhiteSpace(mapping.OriginalValue)&&!Regex.IsMatch(mapping.OriginalValue,@"\[[A-Z_]+_\d{3,}\]")&&Regex.IsMatch(text,DocumentPrivacyPolicy.Pattern(mapping.OriginalValue.Trim(),true),RegexOptions.IgnoreCase))dropped.Add(mapping);
     continue;
    }
    var copy=System.Text.Json.JsonSerializer.Deserialize<Mapping>(System.Text.Json.JsonSerializer.Serialize(mapping))!;
    copy.OriginalValue=normalized.Value;applied.Add(copy);
   }
   return (applied,dropped);
  }
  internal static string DescribeDropped(List<Mapping> dropped)
  {
   var groups=dropped.GroupBy(m=>Project.Normalize(m.OriginalValue)+"|"+m.Category).Select(g=>g.First()).GroupBy(m=>m.Category).OrderBy(g=>g.Key).Select(g=>g.Key+": "+g.Count()).ToArray();
   var total=dropped.GroupBy(m=>Project.Normalize(m.OriginalValue)+"|"+m.Category).Count();
   return "Export blocked: "+total+" approved replacement"+(total==1?"":"s")+" ("+string.Join(", ",groups)+") would not be applied because the privacy policy treats the value as a common word or label, yet it appears in this document. Nothing was released. Remove it from the approved list, add it as a manual replacement if it must be replaced, or export with unapplied replacements allowed.";
  }
  public static int Rehydrate(string source,string output,IEnumerable<Mapping> mappings,DocumentPackageContext? context=null) { Transform(source,output,mappings,false,out var unknown,null,context,true); return unknown; }

  // Removes leftover temporary files from earlier runs: the ".dstmp" files this
  // class writes next to an output, and "Desensitizer-<32 hex>.docx/.txt" files
  // the interface creates in a temp folder. Only files older than olderThan
  // (default 10 minutes, so a second running copy is not disturbed) are removed.
  // Returns the number removed. Call it once at startup with the temp folder and,
  // if different, the output folder. Never throws.
  public static int SweepStaleTempFiles(string folder,TimeSpan? olderThan=null)
  {
   var age=olderThan??TimeSpan.FromMinutes(10);
   var removed=0;
   try
   {
    if(string.IsNullOrWhiteSpace(folder)||!Directory.Exists(folder))return 0;
    foreach(var file in Directory.EnumerateFiles(folder))
    {
     var name=Path.GetFileName(file);
     if(!name.EndsWith(TempSuffix,StringComparison.OrdinalIgnoreCase)&&!AppTempName.IsMatch(name))continue;
     try{if(DateTime.UtcNow-File.GetLastWriteTimeUtc(file)>=age){File.Delete(file);removed++;}}catch{}
    }
   }
   catch{}
   return removed;
  }

  private static void Transform(string source,string output,IEnumerable<Mapping> mappings,bool forward,out int unknown,DocumentProcessingOptions? options=null,DocumentPackageContext? context=null,bool allowUnapplied=false)
  {
   options??=new DocumentProcessingOptions();
   options.Validate();
   Check(source,context);
   unknown=0;
   // Formats beyond Word and text build and audit their own protected copy (Shared/Formats).
   if(DocumentFormats.Extended(source) is{} extended){unknown=extended.Transform(source,output,mappings,forward,options,context,allowUnapplied);return;}
   var temp=output+"."+Guid.NewGuid().ToString("N")+TempSuffix;
   try
   {
    if(Ext(source)==".txt")unknown=TransformText(source,output,temp,mappings,forward,allowUnapplied);
    else unknown=TransformWord(source,output,temp,mappings,forward,options,context,allowUnapplied);
   }
   catch(Exception ex) when(!(ex is InvalidDataException||ex is ArgumentException||ex is IOException||ex is UnauthorizedAccessException||ex is OperationCanceledException||ex is ObjectDisposedException))
   {
    // Library messages can quote document text. Report the kind of failure only.
    throw new InvalidDataException("The document could not be processed safely ("+ex.GetType().Name+"). Nothing was released.");
   }
   finally
   {
    try{if(File.Exists(temp))File.Delete(temp);}catch{}
   }
  }

  private static void Release(string temp,byte[] data,string output)
  {
   File.WriteAllBytes(temp,data);
   MoveInto(temp,output);
  }
  internal static void MoveInto(string temp,string output)
  {
   // Nothing can fail after the move, so a failed call never leaves a finished-looking output behind.
   File.Move(temp,output,true);
  }

  private static int TransformText(string source,string output,string temp,IEnumerable<Mapping> mappings,bool forward,bool allowUnapplied)
  {
   var text=ReadTextFile(source);
   List<Mapping> chosen;
   if(forward)
   {
    var (applied,dropped)=ContentSafeMappings(text,mappings);
    if(dropped.Count>0&&!allowUnapplied)throw new InvalidDataException(DescribeDropped(dropped));
    chosen=applied;
   }
   else chosen=mappings.ToList();
   var textList=chosen.OrderByDescending(m=>(forward?m.OriginalValue:m.Token).Length).ToList();
   var changed=forward?string.Join("\n",text.Split('\n').Select(p=>ExpectedProtectedText(p,textList))):Replace(text,textList,false);
   var unknown=forward?0:UnknownTokens(changed,textList);
   File.WriteAllText(temp,changed,DetectEncoding(source));
   if(forward)PreservationAudit.AssertPreserved(source,temp,textList,paragraph=>ExpectedProtectedText(paragraph,textList));
   MoveInto(temp,output);
   return unknown;
  }

  private static int TransformWord(string source,string output,string temp,IEnumerable<Mapping> mappings,bool forward,DocumentProcessingOptions options,DocumentPackageContext? context,bool allowUnapplied)
  {
   var requestedMappings=mappings.ToList();
   mappings=requestedMappings;
   // One parse of each source part serves the safety check, the text used to
   // choose mappings, the bookmark scan and the rewrite itself.
   using var owned=context==null?DocumentPackageContext.Open(source):null;
   var package=context??owned!;
   var policyText=ReadText(source,package);
   if(forward&&options.PlaceholdersEnabled&&options.IncludeImageAltText)
   {
    // Alt text is protected by the same mappings, so mappings found only there still apply.
    var altTexts=package.VisibleWordXmlParts.SelectMany(name=>ImagePlaceholders.AltTexts(package.GetFinalView(name))).ToList();
    if(altTexts.Count>0)policyText+="\n"+string.Join("\n",altTexts);
   }
   List<Mapping> chosen;
   if(forward)
   {
    var (applied,dropped)=ContentSafeMappings(policyText,mappings);
    if(dropped.Count>0&&!allowUnapplied)throw new InvalidDataException(DescribeDropped(dropped));
    chosen=applied;
   }
   else chosen=mappings.ToList();
   var list=chosen.OrderByDescending(m=>(forward?m.OriginalValue:m.Token).Length).ToList();
   WordMarkupSettings? markup=null;
   if(forward)
   {
    var scanner=new ResidueScanner(list,ReplacementTokens(list));
    var bookmarks=new BookmarkMap(scanner.ContainsAny);
    foreach(var name in package.VisibleWordXmlParts)bookmarks.Collect(package.GetDocument(name));
    markup=new WordMarkupSettings{Options=options,Bookmarks=bookmarks,MapAltText=alt=>ExpectedProtectedText(alt,list)};
   }
   var built=BuildTransformedPackage(package,list,forward,markup);
   if(!forward)
   {
    int unknown;
    using(var restored=DocumentPackageContext.OpenBytes(built))unknown=UnknownTokens(restored.ExtractText(),list);
    Release(temp,built,output);
    return unknown;
   }
   built=SanitizeWordPackage(built);
   built=StripUnsafeWordMarkupAfterSdk(built,markup!);
   built=PackageSafety.Finalize(built);
   using(var protectedPackage=DocumentPackageContext.OpenBytes(built))
   {
    // Hidden-only values were not eligible for visible-text replacement, but
    // must still be checked before release. Do not treat absent visible text as
    // evidence that an approved identifying value is absent from the package.
    var auditMappings=list.Concat(requestedMappings.Where(m=>!string.IsNullOrWhiteSpace(m.OriginalValue)&&
     !Regex.IsMatch(policyText,DocumentPrivacyPolicy.Pattern(m.OriginalValue.Trim(),true),RegexOptions.IgnoreCase)))
     .GroupBy(m=>m.Category+"|"+Project.Normalize(m.OriginalValue)).Select(g=>g.First()).ToList();
    AuditWordPackage(source,built,auditMappings,options,protectedPackage);
    PreservationAudit.AssertPreserved(source,output,list,paragraph=>ExpectedProtectedText(paragraph,list),options,package,protectedPackage);
   }
   Release(temp,built,output);
   return 0;
  }

  // Streams every source entry into a new archive. Word XML parts are rewritten;
  // other entries are copied unchanged. The source file is never copied wholesale.
  private static byte[] BuildTransformedPackage(DocumentPackageContext package,List<Mapping> list,bool forward,WordMarkupSettings? markup)
  {
   var stream=new MemoryStream();
   using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
   {
    foreach(var name in package.EntryNames)
    {
     if(name.EndsWith("/",StringComparison.Ordinal))continue;
     byte[] data;
     if(DocumentPackageContext.IsWordXmlPart(name)&&!IsPrivatePart(name))
     {
      var doc=new XDocument(package.GetDocument(name,true));
      if(forward)ReviewMarkup.Normalize(doc);
      ReplaceTextNodes(doc,list,forward);
      if(forward)StripUnsafeWordMarkup(doc,markup!);
      data=Serialize(doc);
     }
     else data=package.ReadEntryBytes(name);
     var entry=zip.CreateEntry(name,CompressionLevel.Optimal);
     entry.LastWriteTime=PackageSafety.NormalizedTimestamp;
     using var target=entry.Open();
     target.Write(data,0,data.Length);
    }
   }
   return stream.ToArray();
  }
  private static byte[] Serialize(XDocument doc)=>new UTF8Encoding(false).GetBytes(doc.Declaration+doc.ToString(SaveOptions.DisableFormatting));

  // Rewrites an archive entry by entry. The callback returns the new bytes, or null to drop the entry.
  private static byte[] MapEntries(byte[] package,Func<string,byte[],byte[]?> transform)
  {
   using var source=new ZipArchive(new MemoryStream(package,false),ZipArchiveMode.Read);
   PackageSafety.ValidateArchive(source);
   var stream=new MemoryStream();
   using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
   {
    foreach(var entry in source.Entries)
    {
     if(entry.FullName.EndsWith("/",StringComparison.Ordinal))continue;
     var data=transform(entry.FullName,PackageSafety.ReadBytes(entry));
     if(data==null)continue;
     if(entry.FullName.EndsWith(".xml",StringComparison.OrdinalIgnoreCase)||entry.FullName.EndsWith(".rels",StringComparison.OrdinalIgnoreCase))
     {
      // XML comments and processing instructions are never business content.
      // SDK saves can preserve them in settings, themes and relationships too.
      var clean=PackageSafety.Parse(entry.FullName,new UTF8Encoding(false,true).GetString(data).TrimStart('\uFEFF'),true);
      clean.DescendantNodes().Where(n=>n is XComment or XProcessingInstruction).Remove();
      var usedNamespaces=clean.Descendants().Select(e=>e.Name.NamespaceName)
       .Concat(clean.Descendants().Attributes().Where(a=>!a.IsNamespaceDeclaration).Select(a=>a.Name.NamespaceName)).ToHashSet(StringComparer.Ordinal);
      // Unused namespace declarations can themselves hold confidential labels.
      // Keep declarations named by compatibility prefix lists, even if no node uses them.
      var compatibilityPrefixes=string.Join(" ",clean.Descendants().Attributes().Where(a=>a.Name.LocalName is "Ignorable" or "Requires" or "MustUnderstand" or "PreserveElements" or "PreserveAttributes" or "ProcessContent").Select(a=>a.Value))
       .Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(token=>token.Split(':')[0]).ToHashSet(StringComparer.Ordinal);
      clean.Descendants().Attributes().Where(a=>a.IsNamespaceDeclaration&&!usedNamespaces.Contains(a.Value)&&!compatibilityPrefixes.Contains(a.Name.LocalName)).Remove();
      data=Serialize(clean);
     }
     var created=zip.CreateEntry(entry.FullName,CompressionLevel.Optimal);
     created.LastWriteTime=PackageSafety.NormalizedTimestamp;
     using var target=created.Open();
     target.Write(data,0,data.Length);
    }
   }
   return stream.ToArray();
  }

  private static void StripUnsafeWordMarkup(XDocument doc,WordMarkupSettings settings)
  {
   StripVolatileMetadata(doc);
   // Pictures, hyperlinks, field targets and bookmark names. Bookmarks are kept
   // under neutral names so cross references and tables of contents still work.
   // Also hidden text channels: content control alias and tag, document variables,
   // non-structural field instructions, smart tags, table captions, list text.
   WordMarkupSanitizer.Apply(doc,settings);
  }
  private static byte[] SanitizeWordPackage(byte[] package)
  {
   var stream=new MemoryStream();
   stream.Write(package,0,package.Length);stream.Position=0;
   using(var word=WordprocessingDocument.Open(stream,true))
   {
    var properties=word.PackageProperties;
    properties.Title="";properties.Subject="";properties.Creator="";properties.Keywords="";properties.Description="";properties.LastModifiedBy="";properties.Revision="";properties.Category="";properties.Identifier="";properties.ContentStatus="";properties.Language="";properties.Version="";
    properties.Created=null;properties.Modified=null;properties.LastPrinted=null;
    if(word.ExtendedFilePropertiesPart!=null)word.DeletePart(word.ExtendedFilePropertiesPart);
    if(word.CustomFilePropertiesPart!=null)word.DeletePart(word.CustomFilePropertiesPart);
    var visited=new HashSet<OpenXmlPart>();
    void Clean(OpenXmlPartContainer container)
    {
     foreach(var link in container.HyperlinkRelationships.ToList())container.DeleteReferenceRelationship(link);
     foreach(var link in container.ExternalRelationships.ToList())container.DeleteReferenceRelationship(link);
     foreach(var pair in container.Parts.ToList())
     {
      var part=pair.OpenXmlPart;
      if(part.ContentType.StartsWith("image/",StringComparison.OrdinalIgnoreCase)||IsEmbeddedPart(part.Uri.ToString())||IsPrivatePart(part.Uri.ToString())){container.DeletePart(part);continue;}
      if(visited.Add(part))
      {
       if(part.RootElement!=null)
       {
        foreach(var hyperlink in part.RootElement.Descendants<Hyperlink>().ToList())
        {
         var parent=hyperlink.Parent;
         if(parent==null)continue;
         // A link to a bookmark in this document has no outside target. Keep it.
         if(hyperlink.Anchor!=null&&hyperlink.Id==null)continue;
         foreach(var child in hyperlink.ChildElements.ToList())parent.InsertBefore(child.CloneNode(true),hyperlink);
         hyperlink.Remove();
        }
        part.RootElement.Save();
       }
       Clean(part);
      }
     }
    }
    Clean(word);
   }
   return stream.ToArray();
  }
  private static byte[] StripUnsafeWordMarkupAfterSdk(byte[] package,WordMarkupSettings settings)
  {
   // Any picture left at this point is removed without alt text, because only
   // the first pass maps alt text.
   var cleanup=new WordMarkupSettings{Options=settings.Options,Bookmarks=settings.Bookmarks};
   return MapEntries(package,(name,data)=>
   {
    if(!DocumentPackageContext.IsWordXmlPart(name))return data;
    var doc=PackageSafety.Parse(name,new UTF8Encoding(false).GetString(data).TrimStart('﻿'),true);
    StripUnsafeWordMarkup(doc,cleanup);
    return Serialize(doc);
   });
  }
  internal static string[] ReplacementTokens(IEnumerable<Mapping> mappings)=>mappings.Select(mapping=>mapping.Token).Where(token=>!string.IsNullOrWhiteSpace(token)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(token=>token.Length).ToArray();
  private static void AuditWordPackage(string source,byte[] built,List<Mapping> mappings,DocumentProcessingOptions options,DocumentPackageContext package)
  {
   var validator=new OpenXmlValidator(FileFormatVersions.Office2021);
   using var original=WordprocessingDocument.Open(source,false);
   var originalErrors=validator.Validate(original).GroupBy(ValidationKey).ToDictionary(group=>group.Key,group=>group.Count());
   using(var word=WordprocessingDocument.Open(new MemoryStream(built,false),false))
   {
    foreach(var error in validator.Validate(word))
    {
     var key=ValidationKey(error);
     if(originalErrors.TryGetValue(key,out var remaining)&&remaining>0){originalErrors[key]=remaining-1;continue;}
     // The validator's description can quote attribute values, so only its code and location are reported.
     throw new InvalidDataException("The protected copy has a new Word structure error (code "+error.Id+" in "+error.Part?.Uri+" at "+error.Path?.XPath+"). Export was blocked. No file was released.");
    }
   }
   // One combined search per part instead of one per mapping. Fixed placeholder
   // wording is masked like a replacement token. Alt text inside a placeholder is
   // not masked, so a mapped value left there still blocks the export.
   var scanner=new ResidueScanner(mappings,ReplacementTokens(mappings),ImagePlaceholders.FixedTextMask(options));
   foreach(var name in package.EntryNames)
   {
    if(IsEmbeddedPart(name)||IsPrivatePart(name))throw new InvalidDataException("The output still contains hidden review or embedded content. Export was blocked.");
    if(new[]{".png",".jpg",".jpeg",".gif",".bmp",".tif",".tiff",".svg",".emf",".wmf",".webp"}.Any(extension=>name.EndsWith(extension,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("The output still contains an image. Export was blocked because image content was not checked.");
    // Every part that is written must be one the audit can read. Anything else (printer settings, thumbnails, fonts, unknown parts) is unchecked content.
    if(!PackageSafety.IsAllowedOutputPart(name))throw new InvalidDataException("The output contains a part that is not on the allowed list ("+name+"). Export was blocked because its content cannot be checked.");
    if(scanner.ContainsAny(name)||scanner.FindHiddenVariant(name)!=null)
     throw new InvalidDataException("Export blocked: an identifying value remains in a package part name. No file was released.");
    var document=package.GetDocument(name,true);
    if(name.EndsWith(".rels",StringComparison.OrdinalIgnoreCase)&&document.Descendants().Any(x=>x.Attributes().Any(a=>a.Name.LocalName=="TargetMode"&&a.Value.Equals("External",StringComparison.OrdinalIgnoreCase))))throw new InvalidDataException("The output still contains an external link. Export was blocked.");
    if(DocumentPackageContext.IsWordXmlPart(name)&&document.Descendants().Any(ImagePlaceholders.IsObject))throw new InvalidDataException("The output still contains an image or embedded object. Export was blocked.");
    var hit=scanner.Find(document,attribute=>IsAuditedAttribute(name,attribute));
    if(hit!=null)
    {
     // Name the token and the place, never the original value.
     var location=hit.InText?"text":"the "+hit.Attribute!.Name.LocalName+" attribute";
     throw new InvalidDataException("Export blocked: a value selected for replacement ("+hit.Mapping.Category+", replacement "+hit.Mapping.Token+") remains in "+name+" ("+location+"). No file was released. Review this mapping or remove the value from the source document, then try again.");
    }
    if(scanner.FindPackageVariant(document)!=null)
     throw new InvalidDataException("Export blocked: an identifying value remains in encoded or hidden package content. No file was released. Review the source document or contact support.");
   }
  }
  private static string ValidationKey(ValidationErrorInfo error)=>string.Join("|",error.Id,error.Description,error.Part?.Uri,error.Path?.XPath);
  private static void ReplaceTextNodes(XDocument doc,List<Mapping> map,bool forward)
  {
   var markerPrefix=""+Guid.NewGuid().ToString("N")+"_";
   var handled=new HashSet<XElement>();
   foreach(var paragraph in doc.Descendants().Where(x=>x.Name.LocalName=="p"))
   {
    var nodes=paragraph.Descendants().Where(x=>x.Name.LocalName is "t" or "tab" or "br" or "cr").Select(x=>x.Name.LocalName=="t"?x:new XElement(x.Name.Namespace+"t"," ")).ToList();
    foreach(var node in nodes)handled.Add(node);
    var paragraphMap=forward?OccurrenceIdentity.ForParagraph(string.Concat(nodes.Select(n=>n.Value)),map):map;
    var contextualReplacements=paragraphMap.Select((mapping,index)=>new{From=forward?mapping.OriginalValue:mapping.Token,To=forward?mapping.Token:mapping.OriginalValue,Marker=markerPrefix+index+""}).ToList();
    foreach(var replacement in contextualReplacements)ReplaceAcrossRuns(nodes,replacement.From,replacement.Marker,forward);
    foreach(var replacement in contextualReplacements)ReplaceAcrossRuns(nodes,replacement.Marker,replacement.To,false);
   }
   foreach(var node in doc.Descendants().Where(x=>x.Name.LocalName=="t"&&!handled.Contains(x)))node.Value=Replace(node.Value,map,forward);
  }

  internal static void ReplaceAcrossRuns(List<XElement> nodes,string from,string to,bool ignoreCase)
  {
   if(nodes.Count==0||string.IsNullOrEmpty(from))return;
   var searchFrom=0;
   while(true)
   {
    var combined=string.Concat(nodes.Select(x=>x.Value));
    var occurrence=Regex.Match(combined.Substring(searchFrom),ignoreCase?DocumentPrivacyPolicy.Pattern(from,true):Regex.Escape(from),ignoreCase?RegexOptions.IgnoreCase:RegexOptions.None);
    var index=occurrence.Success?searchFrom+occurrence.Index:-1;
    if(index<0)return;
    var end=index+occurrence.Length;
    var offset=0;var startNode=-1;var endNode=-1;var startLocal=0;var endLocal=0;
    for(var i=0;i<nodes.Count;i++)
    {
     var next=offset+nodes[i].Value.Length;
     if(startNode<0&&index>=offset&&index<next){startNode=i;startLocal=index-offset;}
     if(end>offset&&end<=next){endNode=i;endLocal=end-offset;break;}
     offset=next;
    }
    if(startNode<0||endNode<0)return;
    if(startNode==endNode)
    {
     var value=nodes[startNode].Value;
     nodes[startNode].Value=value.Substring(0,startLocal)+to+value.Substring(endLocal);
    }
    else
    {
     var finalSuffix=nodes[endNode].Value.Substring(endLocal);
     nodes[startNode].Value=nodes[startNode].Value.Substring(0,startLocal)+to;
     for(var i=startNode+1;i<endNode;i++)nodes[i].Value="";
     nodes[endNode].Value=finalSuffix;
    }
    searchFrom=index+to.Length;
   }
  }
  public static string ExpectedProtectedText(string paragraph,IEnumerable<Mapping> mappings) => Replace(paragraph,OccurrenceIdentity.ForParagraph(paragraph,mappings).OrderByDescending(m=>m.OriginalValue.Length).ToList(),true);
  internal static string Replace(string text,List<Mapping> map,bool forward)
  {
   var markerPrefix=""+Guid.NewGuid().ToString("N")+"_";
   var replacements=map.Select((mapping,index)=>new{From=forward?mapping.OriginalValue:mapping.Token,To=forward?mapping.Token:mapping.OriginalValue,Marker=markerPrefix+index+""}).ToList();
   foreach(var replacement in replacements)text=Regex.Replace(text,forward?DocumentPrivacyPolicy.Pattern(replacement.From,true):Regex.Escape(replacement.From),replacement.Marker,forward?RegexOptions.IgnoreCase:RegexOptions.None);
   foreach(var replacement in replacements)text=text.Replace(replacement.Marker,replacement.To,StringComparison.Ordinal);
   return text;
  }
  internal static int UnknownTokens(string text,List<Mapping> mappings) { var known=new HashSet<string>(mappings.Select(m=>m.Token),StringComparer.Ordinal); return Regex.Matches(text,@"\[[A-Z_]+_\d{3,}\]").Cast<Match>().Count(m=>!known.Contains(m.Value)); }
  internal static string Ext(string p)=>Path.GetExtension(p).ToLowerInvariant();
  internal static string ReadTextFile(string p)=>File.ReadAllText(p,DetectEncoding(p));
  internal static Encoding DetectEncoding(string p)
  {
   var b=File.ReadAllBytes(p);
   if(b.Length>=3&&b[0]==239&&b[1]==187&&b[2]==191)return new UTF8Encoding(true);
   if(b.Length>=2&&b[0]==255&&b[1]==254)return Encoding.Unicode;
   if(b.Length>=2&&b[0]==254&&b[1]==255)return Encoding.BigEndianUnicode;
   return new UTF8Encoding(false);
  }
  // Rejects unsupported files and unsafe content. A context that is supplied has
  // its answer remembered, so a second call in the same operation is free.
  internal static void Check(string path,DocumentPackageContext? context=null)
  {
   var ext=Ext(path);
   if(ext!=".docx"&&ext!=".txt")
   {
    var extended=DocumentFormats.Extended(path)??throw new InvalidDataException(SupportedFormats.RejectionMessage(path));
    extended.Validate(path);return;
   }
   if(ext!=".docx")return;
   if(context!=null&&!Path.GetFullPath(context.Path).Equals(Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("The document context was opened for a different file.");
   if(context is{Validated:true})return;
   using var owned=context==null?DocumentPackageContext.Open(path):null;
   var package=context??owned!;
   if(package.EntryNames.Any(name=>name.IndexOf("vbaProject",StringComparison.OrdinalIgnoreCase)>=0||name.IndexOf("activeX/",StringComparison.OrdinalIgnoreCase)>=0))
    throw new InvalidDataException("This document contains macros or active controls. Remove them before processing.");
   XNamespace word="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
   foreach(var name in package.VisibleWordXmlParts)
   {
    var document=package.GetDocument(name);
    if(document.Descendants().Any(element=>element.Name.Namespace==word&&element.Name.LocalName is "cellIns" or "cellDel" or "cellMerge"))
     throw new InvalidDataException("This document has tracked table-cell changes that cannot be safely resolved automatically. Accept or reject those changes in Word, then scan the saved copy.");
   }
   package.Validated=true;
  }
  private static bool IsEmbeddedPart(string name)=>DocumentPackageContext.IsEmbeddedPart(name);
  private static bool IsPrivatePart(string name)=>DocumentPackageContext.IsPrivatePart(name);
 }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Desensitizer
{
 // Decisions about an export that do not need a window: which mappings apply to every document of a project,
 // and which file names the protected copy and its temporary working copy get. Tests: tests/UiLogic.
 public static class UiExportScope
 {
  // One encrypted replacement map covers the whole project, so every approved mapping (added by hand or found
  // automatically) applies to every exported document. The transform skips values that are absent from a document.
  // Keep as-is and Review mappings never apply, and neither does a mapping that still needs a context decision.
  public static List<Mapping> Approved(IEnumerable<Mapping> mappings)=>mappings.Where(m=>m.Approved&&!m.RequiresContextReview).ToList();

  public static int ApprovedCount(IEnumerable<Mapping> mappings)=>Approved(mappings).Count;

  // How many of the approved mappings have their value in this text at all (spacing and case are ignored).
  public static int CountPresent(IEnumerable<Mapping> approved,string text)
  {
   var haystack=Squash(text);
   return approved.Count(m=>{var needle=Squash(m.OriginalValue);return needle.Length>0&&haystack.Contains(needle,StringComparison.Ordinal);});
  }

  // Non-blocking sentence for the export summary when nothing in the document is replaced. Empty when something is.
  public static string ZeroAppliedWarning(IEnumerable<Mapping> approved,string text)
  {
   var list=approved.ToList();
   if(list.Count==0)return "No approved replacements exist in this project, so nothing was replaced in this copy.";
   return CountPresent(list,text)==0?"None of the "+list.Count+" approved replacement"+(list.Count==1?"":"s")+" appear in this document, so nothing was replaced in this copy. The final safety scan still ran.":"";
  }

  private static string Squash(string? value)=>string.Join(" ",(value??"").Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
 }

 public sealed class UiExportPaths
 {
  public string OutputPath="";
  public string TempPath="";
  // True when the name the user typed was changed to carry the source file's extension.
  public bool Corrected;
  public string ShownName=>Path.GetFileName(OutputPath);
 }

 public static class UiOutputPath
 {
  // The protected copy always has the same file type as the source: the transform and the final scan both pick
  // their reader from the extension, so a different typed extension must never decide how the copy is read.
  public static string ForceExtension(string chosenPath,string sourcePath,out bool corrected)
  {
   var extension=SupportedFormats.OutputExtension(sourcePath);
   var directory=Path.GetDirectoryName(chosenPath)??"";
   var original=Path.GetFileName(chosenPath);
   var name=original.TrimEnd('.',' ');
   if(name.Length==0)name="Desensitized-document";
   var typed=Path.GetExtension(name);
   if(string.Equals(typed,extension,StringComparison.OrdinalIgnoreCase)){corrected=!string.Equals(original,name,StringComparison.Ordinal);return Path.Combine(directory,name);}
   corrected=true;
   // Only a known document extension (for example .txt typed for an Excel source) is swapped. Anything else, such as
   // the ".2026 report" in "Q3.2026 report", is part of the name and the right extension is appended.
   if(SupportedFormats.IsSupported(name))name=name.Substring(0,name.Length-typed.Length);
   return Path.Combine(directory,name+extension);
  }

  // Fail closed: the working copy must be read by the same format reader as the transform wrote it with.
  public static void RequireSameFormat(string sourcePath,string tempPath)
  {
   if(!string.Equals(Path.GetExtension(sourcePath),Path.GetExtension(tempPath),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The temporary protected copy does not have the type of the source document, so it cannot be checked reliably. Nothing was exported.");
  }

  public static UiExportPaths Plan(string chosenPath,string sourcePath,string tempFolder)
  {
   var output=ForceExtension(chosenPath,sourcePath,out var corrected);
   return new UiExportPaths{OutputPath=output,Corrected=corrected,TempPath=Path.Combine(tempFolder,"Desensitizer-"+Guid.NewGuid().ToString("N")+SupportedFormats.OutputExtension(sourcePath))};
  }
 }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Desensitizer
{
 // Labels the user decided to keep as-is. They are stored PER PROJECT, inside the encrypted project
 // (Project.ReviewPreferences, see Shared/ProjectReviewPreferences.cs), never in a global file, so one
 // project's decision cannot silence a value in another project or in the final gate.
 //
 // API for the interface layer:
 //   ReviewAllowList.Bind(project.ReviewPreferences);   // when a project is unlocked (ProjectScope.Bind does this)
 //   ReviewAllowList.Unbind();                           // when it is closed
 //   ReviewAllowList.Current.Add("Boarding Pass");                          // default categories PROJECT, SYSTEM, PRODUCT
 //   ReviewAllowList.Current.Add("Boarding Pass", new[]{"PRODUCT"}, "ticket rules");   // category and context limited
 //   ReviewAllowList.Current.Contains(value, category, context);
 // After changing the list the caller saves the project (ProjectStore.Save) so the change is stored.
 //
 // Nothing here reads or writes a global file unless a host calls Configure(path) explicitly (tests,
 // tools). That file mode is unauthenticated and not used by the apps. The old global file is only
 // readable through LegacyKeepAsIsFile, and importing it is the user's choice.
 //
 // A matching value is only exempt from the model-suggestion generic-label filter. Detectors and
 // the explicit identifier rules are unchanged, and emails, addresses with digits and very long
 // values are refused by Add.
 public sealed class ReviewAllowList
 {
  private static readonly object Gate=new object();
  private static ReviewAllowList? current;
  private readonly object sync=new object();
  private ProjectReviewPreferences preferences;
  private readonly bool bound;

  public ReviewAllowList(string? path){Path=path;preferences=new ProjectReviewPreferences();}
  private ReviewAllowList(ProjectReviewPreferences projectPreferences){Path=null;preferences=projectPreferences;bound=true;}

  public string? Path {get;}
  public bool IsProjectBound=>bound;
  public bool IsSupportedVersion=>preferences.IsSupportedVersion;

  // One string per entry. Plain labels keep their plain spelling; an entry with non-default categories or a
  // context hint carries them, so changing either changes this collection (the review cache keys on it).
  public IReadOnlyCollection<string> Labels{get{return preferences.KeepAsIsSnapshot().Select(Describe).OrderBy(label=>label,StringComparer.OrdinalIgnoreCase).ToList();}}
  public IReadOnlyList<KeepAsIsEntry> Entries=>preferences.KeepAsIsSnapshot();

  // The list used by IsClearlyGenericCandidate. It is empty until a project is bound (or Configure is
  // called), so no value is ever exempt because of a file or a project the user did not open.
  public static ReviewAllowList Current
  {
   get
   {
    lock(Gate){return current??=new ReviewAllowList((string?)null);}
   }
  }

  public static string DefaultPath(string appFolderName)=>System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),appFolderName,"keep-as-is-labels.json");

  // Makes this project's preferences the active list. Edits go straight into the project data.
  public static ReviewAllowList Bind(ProjectReviewPreferences projectPreferences)
  {
   var list=new ReviewAllowList(projectPreferences);
   lock(Gate){current=list;}
   return list;
  }

  public static void Unbind(){lock(Gate){current=new ReviewAllowList((string?)null);}}

  // Replaces the process-wide list with a file-backed or in-memory one. A null path keeps it in memory only.
  public static ReviewAllowList Configure(string? path,IEnumerable<string>? initialLabels=null)
  {
   var list=new ReviewAllowList(path);
   list.Load();
   foreach(var label in initialLabels??Array.Empty<string>())list.AddInMemory(label,null,null);
   lock(Gate){current=list;}
   return list;
  }

  // File mode only: reloads from Path. A missing or damaged file is an empty list. A bound list is unaffected.
  public void Load()
  {
   if(bound)return;
   var loaded=new ProjectReviewPreferences();
   try
   {
    if(Path!=null&&File.Exists(Path))
    {
     using var document=JsonDocument.Parse(File.ReadAllText(Path));
     var root=document.RootElement;
     if(root.TryGetProperty("entries",out var entries)&&entries.ValueKind==JsonValueKind.Array)
     {
      foreach(var item in entries.EnumerateArray())
      {
       if(item.ValueKind!=JsonValueKind.Object||!item.TryGetProperty("Value",out var value)||value.ValueKind!=JsonValueKind.String)continue;
       var categories=item.TryGetProperty("Categories",out var cats)&&cats.ValueKind==JsonValueKind.Array?cats.EnumerateArray().Where(c=>c.ValueKind==JsonValueKind.String).Select(c=>c.GetString()!).ToList():null;
       var hint=item.TryGetProperty("ContextHint",out var h)&&h.ValueKind==JsonValueKind.String?h.GetString():null;
       loaded.AddKeepAsIs(value.GetString(),categories,hint);
      }
     }
     else if(root.TryGetProperty("labels",out var array)&&array.ValueKind==JsonValueKind.Array)
      foreach(var item in array.EnumerateArray())
       if(item.ValueKind==JsonValueKind.String)loaded.AddKeepAsIs(item.GetString());
    }
   }
   catch(JsonException){}
   catch(IOException){}
   catch(UnauthorizedAccessException){}
   lock(sync){if(Path==null){foreach(var entry in preferences.KeepAsIsSnapshot())loaded.AddKeepAsIs(entry.Value,entry.Categories,entry.ContextHint);}preferences=loaded;}
  }

  // Any entry with this value, whatever its categories or context. Kept for callers that only have a value.
  public bool Contains(string? value)
  {
   if(string.IsNullOrWhiteSpace(value))return false;
   var key=ProjectReviewPreferences.KeyOf(value);
   return preferences.KeepAsIsSnapshot().Any(entry=>ProjectReviewPreferences.KeyOf(entry.Value)==key);
  }

  // The decision the detectors use: category aware, and limited to a context when the entry has one.
  public bool Contains(string? value,string? category,string? context=null)=>preferences.MatchesKeepAsIs(value,category,context);

  public bool Add(string? value)=>Add(value,null,null);

  public bool Add(string? value,IEnumerable<string>? categories,string? contextHint)
  {
   if(!AddInMemory(value,categories,contextHint))return false;
   Save();
   return true;
  }

  public bool Remove(string value,string? contextHint=null)
  {
   if(!preferences.RemoveKeepAsIs(value,contextHint))return false;
   Save();
   return true;
  }

  private bool AddInMemory(string? value,IEnumerable<string>? categories,string? contextHint)=>preferences.AddKeepAsIs(value,categories,contextHint);

  private void Save()
  {
   if(Path==null||bound)return;
   var entries=preferences.KeepAsIsSnapshot().OrderBy(entry=>entry.Value,StringComparer.Ordinal).ToList();
   var labels=entries.Select(entry=>entry.Value).Distinct(StringComparer.Ordinal).ToList();
   Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
   var temporary=Path+".tmp";
   File.WriteAllText(temporary,JsonSerializer.Serialize(new{version=2,labels,entries=entries.Select(entry=>new{entry.Value,entry.Categories,entry.ContextHint})},new JsonSerializerOptions{WriteIndented=true}));
   File.Move(temporary,Path,true);
  }

  private static string Describe(KeepAsIsEntry entry)
  {
   var plain=entry.ContextHint.Length==0&&entry.Categories.Count==ProjectReviewPreferences.KeepAsIsCategories.Count&&ProjectReviewPreferences.KeepAsIsCategories.All(entry.Categories.Contains);
   return plain?entry.Value:entry.Value+" ["+string.Join(",",entry.Categories)+"]"+(entry.ContextHint.Length>0?" @ "+entry.ContextHint:"");
  }
 }

 // Headings and structural words that are not identifying in any document.
 public static class GenericBusinessLabels
 {
  private static readonly HashSet<string> BuiltIn=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {"Introduction","Overview","Scope","Purpose","Background","Objectives","Executive Summary","Table of Contents","Audit Trail","System","Approval"};

  public static IReadOnlyCollection<string> BuiltInLabels=>BuiltIn;
  public static bool IsBuiltIn(string value)=>BuiltIn.Contains(value);
  // Built-in headings only. A saved preference needs a category (and may need a context), see the overloads.
  public static bool IsGeneric(string value)=>BuiltIn.Contains(value);
  // A saved business-label preference must never silence a person, company,
  // contact, location or identifier merely because it has the same spelling.
  public static bool IsGeneric(string value,string category)=>IsGeneric(value,category,null);
  // context: the text around the occurrence, so a context-limited entry can match. Callers that do not
  // know it pass null and such entries then do not apply.
  public static bool IsGeneric(string value,string category,string? context)=>BuiltIn.Contains(value)||ReviewAllowList.Current.Contains(value,category,context);
 }
}

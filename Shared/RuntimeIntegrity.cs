using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Desensitizer
{
 // Environment-variable path overrides (DESENSITIZER_*, AI_DESENSITIZER_*) are
 // honoured only when DESENSITIZER_DEV=1. Otherwise a user-level environment
 // variable could point the app at a different executable or model.
 public static class RuntimeOverrides
 {
  public const string DevVariable="DESENSITIZER_DEV";

  public static bool IsDevelopment=>Environment.GetEnvironmentVariable(DevVariable)=="1";

  public static string? Get(string name)=>Get(name,Environment.GetEnvironmentVariable);

  public static string? Get(string name,Func<string,string?> read)
  {
   if(read(DevVariable)!="1")return null;
   var value=read(name);
   return string.IsNullOrWhiteSpace(value)?null:value;
  }
 }

 public enum IntegrityStatus
 {
  Verified,
  // No manifest ships beside the app. Allowed (behaves as before) but callers should show IntegrityWarning.
  NoManifest,
  Mismatch,
  // A manifest exists but does not list this file. Refused.
  NotListed,
  Missing,
  // The manifest exists but cannot be read or parsed. Refused.
  Unreadable
 }

 public sealed class IntegrityResult
 {
  public IntegrityStatus Status {get;init;}
  public string Message {get;init;}="";
  public bool Allowed=>Status==IntegrityStatus.Verified||Status==IntegrityStatus.NoManifest;
 }

 // Verifies worker executables and models against a SHA-256 manifest that
 // ships beside the app (runtime-manifest.json). With a manifest present every
 // worker must be listed and match, otherwise it is refused (fail closed). With
 // no manifest nothing changes, but ManifestMissing lets the UI warn.
 // Trust-on-first-use manifest creation is deliberately a UI decision.
 //
 // Residual risk: the file is hashed just before launch, so a same-user process
 // could still swap it between the check and the start. The manifest itself is
 // also writable by the same user; it defends against accidents, stale or
 // partial installs and override variables, not against a fully hostile user session.
 public sealed class RuntimeIntegrity
 {
  public const string ManifestFileName="runtime-manifest.json";
  public const string WarningText="No integrity manifest was found beside the app, so the local AI programs and models were not checked against known hashes.";
  private readonly string baseDirectory;
  private readonly object gate=new object();
  private readonly Dictionary<string,(long Length,long Ticks,string Hash)> hashed=new Dictionary<string,(long,long,string)>(StringComparer.OrdinalIgnoreCase);
  private Dictionary<string,string>? entries;
  private string? manifestError;
  private bool loaded;

  public RuntimeIntegrity(string baseDirectory)
  {
   this.baseDirectory=Path.GetFullPath(baseDirectory);
  }

  public static RuntimeIntegrity Default {get;}=new RuntimeIntegrity(AppContext.BaseDirectory);

  public string BaseDirectory=>baseDirectory;
  public bool ManifestPresent{get{Load();return entries!=null||manifestError!=null;}}
  public bool ManifestMissing=>!ManifestPresent;
  public string? IntegrityWarning=>ManifestMissing?WarningText:null;

  public IntegrityResult Verify(string path)
  {
   Load();
   if(manifestError!=null)return new IntegrityResult{Status=IntegrityStatus.Unreadable,Message="The integrity manifest could not be read, so the local AI program was not started."};
   if(entries==null)return new IntegrityResult{Status=IntegrityStatus.NoManifest,Message=WarningText};
   var full=Path.GetFullPath(path);
   if(!File.Exists(full))return new IntegrityResult{Status=IntegrityStatus.Missing,Message="A required local AI file is missing: "+Path.GetFileName(full)+"."};
   var key=RelativeKey(full);
   if(key==null||!entries.TryGetValue(key,out var expected))return new IntegrityResult{Status=IntegrityStatus.NotListed,Message=Path.GetFileName(full)+" is not listed in the integrity manifest, so it was not started."};
   string actual;
   try{actual=CachedHash(full);}
   catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return new IntegrityResult{Status=IntegrityStatus.Unreadable,Message=Path.GetFileName(full)+" could not be read for the integrity check."};}
   return string.Equals(actual,expected,StringComparison.OrdinalIgnoreCase)
    ?new IntegrityResult{Status=IntegrityStatus.Verified,Message=""}
    :new IntegrityResult{Status=IntegrityStatus.Mismatch,Message=Path.GetFileName(full)+" does not match the integrity manifest (it was changed or is damaged), so it was not started."};
  }

  // Throws InvalidOperationException (message safe to show) when the file must not run.
  public void Require(string path)
  {
   var result=Verify(path);
   if(!result.Allowed)throw new InvalidOperationException(result.Message);
  }

  // Verifies the file and, with a manifest, every other listed .exe or .dll in
  // its folder (runtime libraries load from there).
  public void RequireWithRuntimeFiles(string executable)
  {
   Require(executable);
   Load();
   if(entries==null)return;
   var directory=Path.GetDirectoryName(Path.GetFullPath(executable));
   if(directory==null)return;
   foreach(var key in entries.Keys)
   {
    var extension=Path.GetExtension(key);
    if(!extension.Equals(".exe",StringComparison.OrdinalIgnoreCase)&&!extension.Equals(".dll",StringComparison.OrdinalIgnoreCase))continue;
    var full=Path.GetFullPath(Path.Combine(baseDirectory,key.Replace('/',Path.DirectorySeparatorChar)));
    if(!string.Equals(Path.GetDirectoryName(full),directory,StringComparison.OrdinalIgnoreCase))continue;
    Require(full);
   }
  }

  private string CachedHash(string full)
  {
   var info=new FileInfo(full);
   lock(gate)
   {
    if(hashed.TryGetValue(full,out var known)&&known.Length==info.Length&&known.Ticks==info.LastWriteTimeUtc.Ticks)return known.Hash;
   }
   var hash=HashFile(full);
   lock(gate){hashed[full]=(info.Length,info.LastWriteTimeUtc.Ticks,hash);}
   return hash;
  }

  // Streams the file so multi-gigabyte models are never held in memory.
  public static string HashFile(string path)
  {
   using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1<<20,FileOptions.SequentialScan);
   using var sha=SHA256.Create();
   return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
  }

  private string? RelativeKey(string full)
  {
   var root=baseDirectory.EndsWith(Path.DirectorySeparatorChar)?baseDirectory:baseDirectory+Path.DirectorySeparatorChar;
   if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))return null;
   return full.Substring(root.Length).Replace(Path.DirectorySeparatorChar,'/');
  }

  private void Load()
  {
   lock(gate)
   {
    if(loaded)return;
    loaded=true;
    var path=Path.Combine(baseDirectory,ManifestFileName);
    if(!File.Exists(path))return;
    try
    {
     using var document=JsonDocument.Parse(File.ReadAllText(path));
     var parsed=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
     foreach(var item in document.RootElement.GetProperty("files").EnumerateObject())
     {
      var hash=item.Value.GetString();
      if(string.IsNullOrWhiteSpace(hash)||hash.Length!=64)throw new FormatException();
      parsed[item.Name.Replace('\\','/')]=hash.ToLowerInvariant();
     }
     if(parsed.Count==0)throw new FormatException();
     entries=parsed;
    }
    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
    {
     manifestError="unreadable";
    }
   }
  }

  // Manifest generation, used by tools/New-RuntimeManifest.ps1 equivalents and tests.
  // Lists executables, libraries and models under root. Nothing is downloaded.
  public static IEnumerable<string> DefaultManifestFiles(string root)
  {
   var extensions=new[]{".exe",".dll",".gguf"};
   var top=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
   // Only worker folders (analyzer, model-packs); the app's own files are not workers.
   return Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)
    .Where(file=>!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)),top,StringComparison.OrdinalIgnoreCase))
    .Where(file=>extensions.Contains(Path.GetExtension(file),StringComparer.OrdinalIgnoreCase))
    .Where(file=>!Path.GetFileName(file).Equals(ManifestFileName,StringComparison.OrdinalIgnoreCase));
  }

  public static string BuildManifest(string root,IEnumerable<string>? files=null)
  {
   var full=Path.GetFullPath(root);
   var prefix=full.EndsWith(Path.DirectorySeparatorChar)?full:full+Path.DirectorySeparatorChar;
   var map=new SortedDictionary<string,string>(StringComparer.Ordinal);
   foreach(var file in files??DefaultManifestFiles(full))
   {
    var absolute=Path.GetFullPath(file);
    if(!absolute.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))continue;
    map[absolute.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar,'/')]=HashFile(absolute);
   }
   return JsonSerializer.Serialize(new{version=1,files=map},new JsonSerializerOptions{WriteIndented=true});
  }
 }
}

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Desensitizer
{
 // Reads and writes encrypted .dsproj files. The container format (DSP1 reader, DSP2 writer) is in
 // Shared/ProjectContainer.cs.
 //
 // Save is crash safe. The new file is written beside the target, flushed, read back and verified
 // before it replaces anything. The previous file is kept as "<name>.bak" until the replacement has
 // been read back from its final location and verified, and only then is the .bak removed. At every
 // moment either the target or the .bak is a complete, valid project, and Load falls back to the
 // .bak when the target is missing or is not a project file at all.
 public static class ProjectStore
 {
  // Test seam: called with "temp-written", "temp-verified", "replaced" and "verified". A hook that throws
  // simulates a failure at that step. Always null in the shipped apps.
  public static Action<string>? FaultHook;

  public static string BackupPath(string path)=>path+".bak";

  public static void Save(string path, Project project)
  {
   if(string.IsNullOrWhiteSpace(path))throw new InvalidDataException("Invalid project path.");
   SaveSerialized(path,project.Passphrase,Serialize(project));
  }

  // Step one of a save, for callers that write on another thread: checks the passphrase rule and takes a
  // snapshot of the project data. Call it on the thread that owns the project. The snapshot is confidential
  // (plain JSON); pass it straight to SaveSerialized, which wipes it.
  public static byte[] Serialize(Project project)
  {
   // A passphrase that was accepted when the project was unlocked may be an older, shorter one.
   // Only a passphrase chosen for a new or changed project must meet the current rules.
   if(!project.PassphraseAccepted){PassphrasePolicy.Validate(project.Passphrase,project.Name);project.PassphraseAccepted=true;}
   return JsonSerializer.SerializeToUtf8Bytes(project);
  }

  // Step two: encrypts and writes the snapshot with the crash safe sequence described above. Safe to run on
  // any thread. Takes ownership of json and wipes it.
  public static void SaveSerialized(string path, string passphrase, byte[] json)
  {
   if(string.IsNullOrWhiteSpace(path)){ProjectContainer.Wipe("save-json",json);throw new InvalidDataException("Invalid project path.");}
   var fullPath=Path.GetFullPath(path);
   var directory=Path.GetDirectoryName(fullPath);
   if(directory==null){ProjectContainer.Wipe("save-json",json);throw new InvalidDataException("Invalid project path.");}
   Directory.CreateDirectory(directory);
   var backup=BackupPath(fullPath);
   SealedProject? sealedProject=null;
   var temporary=Path.Combine(directory,"."+Path.GetFileName(fullPath)+"."+Guid.NewGuid().ToString("N")+".tmp");
   var replaced=false;var existed=File.Exists(fullPath);
   try
   {
    SweepOrphanTemporaries(directory,Path.GetFileName(fullPath));
    sealedProject=ProjectContainer.Seal(json,passphrase);
    using(var fs=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    {fs.Write(sealedProject.FileBytes);fs.Flush(true);}
    FaultHook?.Invoke("temp-written");
    if(!ProjectContainer.VerifyReadBack(File.ReadAllBytes(temporary),sealedProject,json))throw new IOException("The project file could not be verified after writing. The previous file was left unchanged.");
    FaultHook?.Invoke("temp-verified");
    // A target that is not a project (damaged) must not overwrite a good .bak: promote without touching it.
    var mainIsProject=existed&&ProjectContainer.Detect(File.ReadAllBytes(fullPath))!=ProjectFormat.Unknown;
    if(mainIsProject)
    {
     try{File.Replace(temporary,fullPath,backup,true);}
     catch(PlatformNotSupportedException){CopyThenMove(temporary,fullPath,backup);}
     catch(UnauthorizedAccessException){CopyThenMove(temporary,fullPath,backup);}
    }
    else File.Move(temporary,fullPath,true);
    replaced=true;
    FaultHook?.Invoke("replaced");
    if(!ProjectContainer.VerifyReadBack(File.ReadAllBytes(fullPath),sealedProject,json))throw new IOException("The saved project file failed verification.");
   }
   catch
   {
    // Put the previous file back if the new one was promoted but is not trusted.
    if(replaced&&File.Exists(backup)){try{File.Copy(backup,fullPath,true);File.Delete(backup);}catch(IOException){}catch(UnauthorizedAccessException){}}
    else if(replaced&&!existed){try{File.Delete(fullPath);}catch(IOException){}catch(UnauthorizedAccessException){}}
    throw;
   }
   finally
   {
    try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}
    sealedProject?.Dispose();
    ProjectContainer.Wipe("save-json",json);
   }
   FaultHook?.Invoke("verified");
   // Verified: the previous copy is no longer needed. A failure here only leaves a stale .bak, which
   // the next save replaces and removes.
   try{if(File.Exists(backup))File.Delete(backup);}catch(IOException){}catch(UnauthorizedAccessException){}
  }

  public static void ChangePassphrase(string path,Project project,string currentPassphrase,string newPassphrase)
  {
   if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))throw new InvalidDataException("The project file is missing.");
   PassphrasePolicy.Validate(newPassphrase,project.Name);
   if(string.Equals(currentPassphrase,newPassphrase,StringComparison.Ordinal))throw new ArgumentException("Choose a different new passphrase.",nameof(newPassphrase));
   Load(path,currentPassphrase); // Verify against the encrypted file, not just the unlocked session.
   if(!string.Equals(project.Passphrase,currentPassphrase,StringComparison.Ordinal))throw new InvalidDataException("The project changed since it was unlocked. Reopen it before changing the passphrase.");
   project.Passphrase=newPassphrase;
   try{Save(path,project);}
   catch{project.Passphrase=currentPassphrase;throw;}
  }

  public static Project Load(string path,string passphrase)
  {
   var bytes=ReadProjectBytes(path);
   byte[]? plain=null;
   try
   {
    plain=ProjectContainer.Open(bytes,passphrase,out var format);
    Project p;
    try{p=JsonSerializer.Deserialize<Project>(plain)??throw new InvalidDataException("Project data is invalid.");}
    catch(JsonException){throw new InvalidDataException("Project data is invalid.");}
    p.Passphrase=passphrase;
    p.PassphraseAccepted=true;
    p.LoadedFormat=format;
    return p;
   }
   finally{if(plain!=null)ProjectContainer.Wipe("load-plaintext",plain);ProjectContainer.Wipe("load-file-bytes",bytes);}
  }

  // Which container format the file on disk uses. Dsp1 means the next successful Save upgrades it.
  public static ProjectFormat GetFormat(string path)
  {
   try{return ProjectContainer.Detect(ReadProjectBytes(path));}
   catch(IOException){return ProjectFormat.Unknown;}
  }

  // The target when it exists and is a project file, otherwise the .bak left by an interrupted save.
  private static byte[] ReadProjectBytes(string path)
  {
   var backup=BackupPath(path);
   if(File.Exists(path))
   {
    var main=File.ReadAllBytes(path);
    if(ProjectContainer.Detect(main)!=ProjectFormat.Unknown||!File.Exists(backup))return main;
    var fallback=File.ReadAllBytes(backup);
    if(ProjectContainer.Detect(fallback)==ProjectFormat.Unknown)return main;
    return fallback;
   }
   if(File.Exists(backup))return File.ReadAllBytes(backup);
   return File.ReadAllBytes(path); // throws the usual FileNotFoundException
  }

  private static void CopyThenMove(string temporary,string target,string backup)
  {
   File.Copy(target,backup,true);
   File.Move(temporary,target,true);
  }

  private static void SweepOrphanTemporaries(string directory,string fileName)
  {
   try
   {
    foreach(var orphan in Directory.EnumerateFiles(directory,"."+fileName+".*.tmp"))
     if(DateTime.UtcNow-File.GetLastWriteTimeUtc(orphan)>TimeSpan.FromMinutes(5))
      try{File.Delete(orphan);}catch(IOException){}catch(UnauthorizedAccessException){}
   }
   catch(IOException){}
   catch(UnauthorizedAccessException){}
  }
 }
}

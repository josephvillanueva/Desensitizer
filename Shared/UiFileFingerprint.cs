using System;
using System.IO;
using System.Security.Cryptography;

namespace Desensitizer
{
 public enum FingerprintState
 {
  // The project has no fingerprint for this file (saved before fingerprints existed, or never scanned).
  Unknown,
  Match,
  // The file is not the one that was scanned: the findings may not describe it. A new scan is required.
  Mismatch,
  Missing
 }

 // SHA-256 and length of a source document, kept in the project when the file is scanned or deep checked and
 // compared again at export (UI-8). Nothing about the content other than the hash and the length is kept.
 public static class UiFileFingerprint
 {
  public static (string Hash,long Length) Compute(string path)
  {
   using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
   var hash=Convert.ToHexString(SHA256.HashData(stream));
   return (hash,stream.Length);
  }

  // Records the fingerprint taken from the file. Returns false (and clears the record) when the file cannot be read.
  public static bool Record(FileRecord file,string path)
  {
   try{var (hash,length)=Compute(path);file.ContentSha256=hash;file.ContentLength=length;return true;}
   catch(Exception){file.ContentSha256=null;file.ContentLength=null;return false;}
  }

  public static FingerprintState Check(FileRecord file,string path)=>Check(file,path,out _);

  // The state, plus the fingerprint read now (null when the file cannot be read).
  public static FingerprintState Check(FileRecord file,string path,out (string Hash,long Length)? current)
  {
   (string Hash,long Length) now;
   current=null;
   try{now=Compute(path);}
   catch(Exception){return FingerprintState.Missing;}
   current=now;
   if(string.IsNullOrEmpty(file.ContentSha256)||file.ContentLength==null)return FingerprintState.Unknown;
   return file.ContentLength==now.Length&&string.Equals(file.ContentSha256,now.Hash,StringComparison.OrdinalIgnoreCase)?FingerprintState.Match:FingerprintState.Mismatch;
  }

  // True when the project holds a fingerprint for the file and the one just read is different.
  public static bool Differs(FileRecord file,(string Hash,long Length)? current)=>current!=null&&!string.IsNullOrEmpty(file.ContentSha256)&&file.ContentLength!=null&&(file.ContentLength!=current.Value.Length||!string.Equals(file.ContentSha256,current.Value.Hash,StringComparison.OrdinalIgnoreCase));

  public static void Apply(FileRecord file,(string Hash,long Length)? print){file.ContentSha256=print?.Hash;file.ContentLength=print?.Length;}

  public const string MismatchMessage="This document has changed since it was scanned, so the findings and decisions in this project may not describe it. Scan the document again before exporting. Nothing was exported.";
  public const string UnknownNotice="This document was scanned before content checks existed, so the export could not confirm it is the file that was scanned. Scan it again to be sure.";
 }
}

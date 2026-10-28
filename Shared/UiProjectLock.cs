using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Desensitizer;

// One writable window per encrypted project, across both apps. The OS releases the
// handle after a crash. An empty or stale sidecar does not imply an active owner.
public sealed class UiProjectLock : IDisposable
{
    private FileStream? stream;
    public string LockPath { get; }
    private UiProjectLock(string path, FileStream handle) { LockPath=path;stream=handle; }
    public static string LockPathFor(string projectPath)=>Path.GetFullPath(projectPath)+".lock";
    public static bool IsLockConflict(IOException exception)=>(exception.HResult&0xffff) is 32 or 33;

    // Only a sharing/locking violation means another owner. Access, missing drive,
    // directory and other storage failures must retain their own recovery guidance.
    public static UiProjectLock? TryAcquire(string projectPath,out string holder)
    {
        holder="";
        var path=LockPathFor(projectPath);
        FileStream handle;
        try { handle=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read); }
        catch(IOException ex) when(IsLockConflict(ex)) { holder=ReadHolder(path);return null; }
        try
        {
            handle.SetLength(0);
            var bytes=Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            handle.Write(bytes);handle.Flush();
            return new UiProjectLock(path,handle);
        }
        catch { handle.Dispose();throw; }
    }

    private static string ReadHolder(string path)
    {
        try
        {
            using var handle=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            var bytes=new byte[16];var length=handle.Read(bytes);
            var value=Encoding.ASCII.GetString(bytes,0,length).Trim();
            return int.TryParse(value,out var pid)&&pid>0?pid.ToString():"";
        }
        catch(IOException) { return ""; }
        catch(UnauthorizedAccessException) { return ""; }
    }

    public void Dispose()
    {
        // Keep the harmless sidecar. Never delete by name after releasing the handle:
        // a new owner may already hold it. No document text or passphrase is stored.
        Interlocked.Exchange(ref stream,null)?.Dispose();
    }
}

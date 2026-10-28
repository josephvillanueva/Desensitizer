using System;
using System.IO;

namespace Desensitizer;

// Keep the previous destination intact until the complete protected copy is ready.
// The staging file is on the destination volume so commit uses a filesystem rename,
// not an overwrite-in-place copy. Unsupported replacement fails closed.
public static class AtomicOutput
{
    public static void Copy(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The source and output must be different files.");

        var directory = Path.GetDirectoryName(destination)!;
        var staged = Path.Combine(directory, ".desensitizer-atomic-" + Guid.NewGuid().ToString("N") + ".tmp");
        var createdStaging = false;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                createdStaging = true;
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
                File.Replace(staged, destination, null);
            else
                // Do not overwrite a file that appears after the existence check.
                File.Move(staged, destination);
        }
        finally
        {
            // Delete only the exact staging path allocated by this call. Never remove
            // the previous destination, the source, or a different operation's files.
            if (createdStaging)
            {
                try { File.Delete(staged); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

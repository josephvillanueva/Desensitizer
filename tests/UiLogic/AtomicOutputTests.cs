using Desensitizer;

static class AtomicOutputTests
{
    public static void Run(CheckFn Check, string work)
    {
        var directory = Path.Combine(work, "atomic-output");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "verified-source.txt");
        var destination = Path.Combine(directory, "protected-copy.txt");
        var payload = System.Text.Encoding.UTF8.GetBytes("[PERSON_001] shall approve refunds within 30 days.\n" + new string('x', 131072));
        File.WriteAllBytes(source, payload);
        var unrelated = Path.Combine(directory, ".desensitizer-atomic-unrelated.tmp");
        File.WriteAllText(unrelated, "A different operation owns this file.");
        string[] Snapshot() => Directory.GetFileSystemEntries(directory).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        AtomicOutput.Copy(source, destination);
        Check("atomic output: a new destination contains the complete protected copy", File.ReadAllBytes(destination).SequenceEqual(payload));
        Check("atomic output: creating output leaves source unchanged", File.ReadAllBytes(source).SequenceEqual(payload));
        Check("atomic output: new output leaves no owned staging file", Snapshot().Length == 3);
        Check("atomic output: another operation's staging file is untouched", File.ReadAllText(unrelated) == "A different operation owns this file.");

        File.WriteAllText(destination, "previous verified output");
        AtomicOutput.Copy(source, destination);
        Check("atomic output: an existing destination is replaced with the complete copy", File.ReadAllBytes(destination).SequenceEqual(payload));
        Check("atomic output: replacement leaves source unchanged", File.ReadAllBytes(source).SequenceEqual(payload));
        Check("atomic output: replacement leaves no staging file or backup", Snapshot().Length == 3);

        File.WriteAllText(destination, "previous verified output");
        var beforeFailure = Snapshot();
        var lockedFailure = false;
        using (var lockedDestination = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { AtomicOutput.Copy(source, destination); }
            catch (IOException) { lockedFailure = true; }
        }
        Check("atomic output: a locked destination reports commit failure", lockedFailure);
        Check("atomic output: failed commit preserves the previous destination", File.ReadAllText(destination) == "previous verified output");
        Check("atomic output: failed commit preserves the source", File.ReadAllBytes(source).SequenceEqual(payload));
        Check("atomic output: failed commit removes only its own staging file", Snapshot().SequenceEqual(beforeFailure));

        var sourceReadFailure = false;
        using (var lockedSource = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { AtomicOutput.Copy(source, destination); }
            catch (IOException) { sourceReadFailure = true; }
        }
        Check("atomic output: source read failure is reported", sourceReadFailure);
        Check("atomic output: source read failure does not touch the destination", File.ReadAllText(destination) == "previous verified output");
        Check("atomic output: source read failure leaves no owned staging file", Snapshot().SequenceEqual(beforeFailure));

        var identicalRejected = false;
        try { AtomicOutput.Copy(source, Path.Combine(directory, ".", Path.GetFileName(source))); }
        catch (ArgumentException) { identicalRejected = true; }
        Check("atomic output: canonical same-source destination is rejected", identicalRejected && File.ReadAllBytes(source).SequenceEqual(payload));
        Check("atomic output: identical path rejection leaves other files untouched", Snapshot().SequenceEqual(beforeFailure));

        var missingSourceFailure = false;
        try { AtomicOutput.Copy(Path.Combine(directory, "missing.txt"), destination); }
        catch (FileNotFoundException) { missingSourceFailure = true; }
        Check("atomic output: missing source preserves the previous destination", missingSourceFailure && File.ReadAllText(destination) == "previous verified output");
        Check("atomic output: missing source leaves no owned staging file", Snapshot().SequenceEqual(beforeFailure));

        var absentDirectory = Path.Combine(directory, "missing-folder");
        var missingDirectoryFailure = false;
        try { AtomicOutput.Copy(source, Path.Combine(absentDirectory, "copy.txt")); }
        catch (DirectoryNotFoundException) { missingDirectoryFailure = true; }
        Check("atomic output: an absent destination directory is not created implicitly", missingDirectoryFailure && !Directory.Exists(absentDirectory));
        Check("atomic output: absent directory failure leaves source and existing output intact", File.ReadAllBytes(source).SequenceEqual(payload) && File.ReadAllText(destination) == "previous verified output");
    }
}

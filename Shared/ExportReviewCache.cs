using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Desensitizer;

// Completed section reviews, keyed by SHA-256 of (configuration, section text).
// Owned by one export operation. Nothing is written to disk by this class.
//
// Resume support: Export() and Import() let a caller persist finished sections
// so a cancelled or crashed run can resume without repeating them, and
// OnEntryStored lets a caller checkpoint each section as it completes.
// WARNING: serialized entries contain ORIGINAL CONFIDENTIAL TEXT (the reviewed
// values). The caller decides persistence and must encrypt and authenticate
// the bytes (for example with the project's passphrase encryption), never store
// them in plaintext, and never ship them with a protected document. An entry
// that is altered could hide a finding, so only import data that was
// authenticated by the caller.
public sealed class ExportReviewCache : IDisposable
{
    public const int SnapshotVersion = 1;
    public const int MaxSnapshotBytes = 16 * 1024 * 1024;
    public const int MaxCacheCharacters = 4 * 1024 * 1024;
    private const int MaxEntryCharacters = 1024 * 1024;
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    { "PERSON", "ORGANIZATION", "VENDOR", "PROJECT", "SYSTEM", "PRODUCT", "DEPARTMENT", "ADDRESS", "URL", "EMAIL", "PHONE", "IDENTIFIER" };
    private readonly Dictionary<string, string> entries = new();
    private readonly Dictionary<string, IDisposable> attached = new();
    private readonly object sync = new();
    private readonly int capacity;
    private bool disposed;
    private int retainedCharacters;
    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public int Count { get { lock (sync) return entries.Count; } }

    public ExportReviewCache(int capacity = 2048) { this.capacity = Math.Max(1, capacity); }

    // Called with (key, serialized entry) after a section review completes and is
    // stored. The serialized entry contains original confidential text.
    public Action<string, string>? OnEntryStored { get; set; }

    public static string Configuration(string runtime, string model, string prompt)
    {
        static string Stamp(string path)
        {
            var file = new FileInfo(path);
            return file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        }
        // A cached empty result under an old keep-label preference must not
        // suppress a finding after that preference changes or is removed.
        var preferences=JsonSerializer.Serialize(ReviewAllowList.Current.Labels);
        var preferenceHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preferences)));
        return Stamp(runtime) + "|" + Stamp(model) + "|" + prompt + "|" + preferenceHash + "|" +
            DocumentPrivacyPolicy.ProjectTermsFingerprint() + "|ctx4096-temp0-max1600-policy-v3";
    }

    public static string KeyFor(string configuration, string section) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { configuration, section }))));

    public List<Candidate> Review(string configuration, string section,
        Func<List<Candidate>> review, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        var key = KeyFor(configuration, section);
        string? saved;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            entries.TryGetValue(key, out saved);
        }
        if (saved != null)
        {
            var hit = ReadEntry(saved);
            // A valid snapshot must also belong to this section. Authentication
            // remains the caller's responsibility; shape checks cannot prove it.
            if (hit != null && hit.All(c => section.Contains(c.Value, StringComparison.OrdinalIgnoreCase)))
            {
                lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); Hits++; }
                token.ThrowIfCancellationRequested();
                return hit;
            }
            lock (sync)
            {
                if (entries.Remove(key, out var removed)) retainedCharacters -= removed.Length;
            } // A damaged entry is a miss, never an empty review.
        }
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); Misses++; }
        var result = review(); // Exceptions and incomplete responses are never cached.
        token.ThrowIfCancellationRequested();
        if (!ValidCandidates(result) || result.Any(c => !section.Contains(c.Value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The section review returned invalid or ungrounded findings.");
        var serialized = JsonSerializer.Serialize(result);
        var stored = false;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (serialized.Length <= MaxEntryCharacters && entries.Count < capacity &&
                retainedCharacters + serialized.Length <= MaxCacheCharacters)
            {
                // Another review may have completed the same key concurrently.
                if (entries.Remove(key, out var previous)) retainedCharacters -= previous.Length;
                entries[key] = serialized;
                retainedCharacters += serialized.Length;
                stored = true;
            }
        }
        if (stored) OnEntryStored?.Invoke(key, serialized);
        return result;
    }

    // Contains original confidential text. Encrypt before writing anywhere.
    public Dictionary<string, string> Export()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new Dictionary<string, string>(entries);
        }
    }

    // Returns the number of entries accepted. Malformed keys, unreadable entries,
    // a wrong version or entries over capacity are ignored (fail closed to a miss).
    public int Import(IReadOnlyDictionary<string, string> imported, int version = SnapshotVersion)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (version != SnapshotVersion || imported == null) return 0;
        var accepted = 0;
        foreach (var pair in imported)
        {
            if (pair.Key == null || pair.Key.Length != 64 || !Regex.IsMatch(pair.Key, "\\A[0-9A-F]{64}\\z")) continue;
            if (ReadEntry(pair.Value) == null) continue;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!entries.ContainsKey(pair.Key) && entries.Count >= capacity) continue;
                var previousLength = entries.TryGetValue(pair.Key, out var previous) ? previous.Length : 0;
                if (retainedCharacters - previousLength + pair.Value.Length > MaxCacheCharacters) continue;
                entries[pair.Key] = pair.Value;
                retainedCharacters += pair.Value.Length - previousLength;
            }
            accepted++;
        }
        return accepted;
    }

    // Plaintext UTF-8 JSON of Export(). The caller must encrypt these bytes.
    public byte[] ExportBytes() =>
        JsonSerializer.SerializeToUtf8Bytes(new Snapshot { Version = SnapshotVersion, Entries = Export() });

    public int ImportBytes(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (data.Length > MaxSnapshotBytes) return 0;
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(data);
            return snapshot == null ? 0 : Import(snapshot.Entries, snapshot.Version);
        }
        catch (JsonException) { return 0; }
    }

    private sealed class Snapshot
    {
        public int Version { get; set; }
        public Dictionary<string, string> Entries { get; set; } = new();
    }

    private static List<Candidate>? ReadEntry(string? serialized)
    {
        if (serialized == null || serialized.Length > MaxEntryCharacters) return null;
        try
        {
            var candidates = JsonSerializer.Deserialize<List<Candidate>>(serialized);
            return ValidCandidates(candidates) ? candidates : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool ValidCandidates(List<Candidate>? candidates) => candidates != null && candidates.All(c =>
        c != null && !string.IsNullOrWhiteSpace(c.Value) && c.Value.Length <= 4096 &&
        c.Category != null && Categories.Contains(c.Category) &&
        double.IsFinite(c.Confidence) && c.Confidence >= 0 && c.Confidence <= 1 &&
        c.Source != null && c.Explanation != null && c.DetectionSources != null &&
        c.DetectionSources.All(s => s != null));

    // Lets the operation that owns this cache also own helper resources (for
    // example the local model servers kept alive across export passes). They are
    // disposed with the cache, so cancelling or finishing the export stops them.
    public T GetOrAttach<T>(string name, Func<T> create) where T : class, IDisposable
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (attached.TryGetValue(name, out var existing))
                return existing is T typed ? typed : throw new InvalidOperationException("The cache resource name is already used by another type.");
            var created = create();
            attached[name] = created;
            return created;
        }
    }

    public void Dispose()
    {
        List<IDisposable> resources;
        lock (sync)
        {
            if (disposed) return;
            entries.Clear();
            retainedCharacters = 0;
            resources = new List<IDisposable>(attached.Values);
            attached.Clear();
            disposed = true;
        }
        foreach (var resource in resources) { try { resource.Dispose(); } catch { } }
    }
}

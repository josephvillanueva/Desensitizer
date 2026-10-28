using System.IO.Compression;
using System.Text;
using Desensitizer;

// Helpers shared by the format tests. Fictional values only.
static class Kit
{
    public static Mapping Map(string value, string category, int number, bool manual = true) => new()
    {
        OriginalValue = value,
        NormalizedValue = Project.Normalize(value),
        Category = category,
        Token = "[" + category + "_" + number.ToString("000") + "]",
        Approved = true,
        IsManual = manual,
    };

    public static byte[] Zip(params (string Name, string Content)[] parts) =>
        ZipBytes(parts.Select(p => (p.Name, new UTF8Encoding(false).GetBytes(p.Content))));

    public static byte[] ZipBytes(IEnumerable<(string Name, byte[] Content)> parts)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, content) in parts)
            {
                var entry = zip.CreateEntry(name);
                using var target = entry.Open();
                target.Write(content, 0, content.Length);
            }
        return stream.ToArray();
    }

    public static Dictionary<string, string> Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Where(e => !e.FullName.EndsWith("/")).ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    public static string All(string path) => string.Join("\n", Read(path).Values);

    public static bool Throws(Action action, string? contains = null)
    {
        try { action(); return false; }
        catch (Exception ex) { return contains == null || ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase); }
    }

    public static string Message(Action action)
    {
        try { action(); return ""; }
        catch (Exception ex) { return ex.Message; }
    }

    public static string[] Leftovers(string folder) =>
        Directory.GetFiles(folder).Where(f => f.EndsWith(DocumentProcessor.TempSuffix)).ToArray();

    public static string Dir(string work, string name)
    {
        var dir = Path.Combine(work, name);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

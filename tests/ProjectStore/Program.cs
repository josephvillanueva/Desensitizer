using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desensitizer;

// Deterministic tests for the project store (items P1 to P4). Everything lives in a temp folder created for
// the run and removed at the end. All data is fictional. The user's real projects, catalog and settings are
// never read: ProjectCatalog is pointed at the temp folder before any catalog call.

var checks = new List<object>();
var failures = 0;
void Check(string name, bool passed)
{
    checks.Add(new { name, passed });
    if (!passed) failures++;
    Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
}

var work = Path.Combine(Path.GetTempPath(), "ProjectStoreTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
ProjectCatalog.UseRoot(Path.Combine(work, "app-data"));
var timer = Stopwatch.StartNew();
try
{
    StoreFormat.Run(Check, work);
    SaveSafety.Run(Check, work);
    PassphraseRules.Run(Check, work);
    CatalogTests.Run(Check, work);
    ReviewPreferenceTests.Run(Check, work);
    GenericTermTests.Run(Check, work);
}
catch (Exception ex)
{
    Check("Test run finished without an unexpected exception (" + ex.GetType().Name + ": " + ex.Message + ")", false);
    Console.WriteLine(ex);
}
finally
{
    ProjectCatalog.UseRoot(null);
    ProjectStore.FaultHook = null;
    ProjectContainer.WipeProbe = null;
    ProjectCatalogStorage.FaultHook = null;
    try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}
Console.WriteLine($"{checks.Count - failures} passed, {failures} failed, {timer.Elapsed.TotalSeconds:0.0}s");
return failures == 0 ? 0 : 1;

// The DSP1 writer exactly as it was in the app before DSP2 (git history of ProjectStore.cs). It is the oracle for
// "projects created by the old code keep opening": nothing here calls the new container.
static class LegacyDsp1Oracle
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DSP1");
    public static byte[] Write(byte[] json, string passphrase)
    {
        var salt = Random(16); var nonce = Random(12); var key = Derive(passphrase, salt); var cipher = new byte[json.Length]; var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, json, cipher, tag, Magic);
        using var ms = new MemoryStream();
        ms.Write(Magic); ms.Write(salt); ms.Write(nonce); ms.Write(tag); ms.Write(cipher);
        return ms.ToArray();
    }
    private static byte[] Derive(string pass, byte[] salt) { using var k = new Rfc2898DeriveBytes(pass, salt, 150000, HashAlgorithmName.SHA256); return k.GetBytes(32); }
    private static byte[] Random(int n) { var b = new byte[n]; using var r = RandomNumberGenerator.Create(); r.GetBytes(b); return b; }
}

static class Fixtures
{
    public const string Strong = "violet-lantern-27 harbor";
    public static Project NewProject(string name = "Fictional Alpha Initiative", string passphrase = Strong)
    {
        var project = Project.New(name, passphrase);
        project.EnsureMapping("Avery Example", "PERSON", true, 0.99, "Rules", "fictional", null, false);
        project.EnsureMapping("Northwind Example Holdings", "ORGANIZATION", true, 0.9, "Rules", "fictional", null, false);
        return project;
    }
    public static string Describe(Project p) => p.Name + "|" + string.Join(",", p.Mappings.Select(m => m.Token + "=" + m.OriginalValue));
    public static bool BytesEqual(byte[] a, byte[] b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    public static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } catch (Exception) { return false; } }
    public static string[] Leftovers(string folder) => Directory.GetFiles(folder).Where(f => f.EndsWith(".tmp") || f.EndsWith(".bak")).ToArray();
    // Writes a DSP1 project the way the old app did, from a project object, and returns the JSON used.
    public static byte[] WriteDsp1(string path, Project project, string passphrase)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(project);
        File.WriteAllBytes(path, LegacyDsp1Oracle.Write(json, passphrase));
        return json;
    }
}

// ---- P1: key derivation and file format ------------------------------------------------------
static class StoreFormat
{
    public static void Run(Action<string, bool> check, string work)
    {
        var folder = Path.Combine(work, "format"); Directory.CreateDirectory(folder);
        var pass = Fixtures.Strong;

        // DSP2 round trip and header contents.
        var path = Path.Combine(folder, "alpha.dsproj");
        var project = Fixtures.NewProject();
        ProjectStore.Save(path, project);
        var bytes = File.ReadAllBytes(path);
        check("New projects are written as DSP2", Encoding.ASCII.GetString(bytes, 0, 4) == "DSP2" && ProjectStore.GetFormat(path) == ProjectFormat.Dsp2);
        var iterations = (bytes[6] << 24) | (bytes[7] << 16) | (bytes[8] << 8) | bytes[9];
        check("DSP2 header states version 2, PBKDF2-SHA256, 600,000 iterations, 16-byte salt, 12-byte nonce, 16-byte tag",
            bytes[4] == 2 && bytes[5] == 1 && iterations == 600000 && bytes[10] == 16 && bytes[27] == 12 && bytes[40] == 16 && bytes.Length > 57);
        var loaded = ProjectStore.Load(path, pass);
        check("DSP2 round trip keeps the name and mappings", Fixtures.Describe(loaded) == Fixtures.Describe(project) && loaded.LoadedFormat == ProjectFormat.Dsp2 && loaded.Passphrase == pass);
        check("The file does not contain the project name or mapped values in plaintext",
            !Encoding.UTF8.GetString(bytes).Contains("Fictional Alpha") && !Encoding.UTF8.GetString(bytes).Contains("Avery Example"));
        ProjectStore.Save(path, project);
        check("Two saves of the same project differ (fresh salt and nonce)", !Fixtures.BytesEqual(bytes, File.ReadAllBytes(path)));

        // Wrong passphrases and damaged files fail without returning data.
        var before = File.ReadAllBytes(path);
        check("Wrong passphrase is rejected", Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(path, pass + "x")));
        check("Empty passphrase is rejected", Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(path, "")));
        check("A failed load leaves the file untouched and no temporary files", Fixtures.BytesEqual(before, File.ReadAllBytes(path)) && Fixtures.Leftovers(folder).Length == 0);

        var tampered = new List<int>();
        for (var i = 0; i < 57; i++)
        {
            var copy = (byte[])before.Clone(); copy[i] ^= 0x01;
            var bad = Path.Combine(folder, "tamper-header.dsproj"); File.WriteAllBytes(bad, copy);
            if (!Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(bad, pass))) tampered.Add(i);
        }
        check("Changing any single header or tag byte (offsets 0 to 56) makes Load fail" + (tampered.Count > 0 ? " [accepted offsets: " + string.Join(",", tampered) + "]" : ""), tampered.Count == 0);
        var cipherBad = new List<int>();
        foreach (var offset in new[] { 57, 58, before.Length / 2, before.Length - 1 })
        {
            var copy = (byte[])before.Clone(); copy[offset] ^= 0x80;
            var bad = Path.Combine(folder, "tamper-cipher.dsproj"); File.WriteAllBytes(bad, copy);
            if (!Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(bad, pass))) cipherBad.Add(offset);
        }
        check("Changing ciphertext bytes makes Load fail", cipherBad.Count == 0);
        var truncated = Path.Combine(folder, "truncated.dsproj");
        File.WriteAllBytes(truncated, before.Take(before.Length - 1).ToArray());
        check("A truncated file fails", Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(truncated, pass)));
        File.WriteAllBytes(truncated, before.Concat(new byte[] { 0 }).ToArray());
        check("A file with extra trailing data fails", Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(truncated, pass)));
        File.WriteAllBytes(truncated, before.Take(30).ToArray());
        check("A file cut inside the header fails as not a project", Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(truncated, pass)));
        File.WriteAllBytes(truncated, new byte[200]);
        check("A file with an unknown magic fails as not a project", Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(truncated, pass)));

        // A DSP2 file with an absurd iteration count is refused before any key derivation (denial of service guard).
        var hostile = (byte[])before.Clone(); hostile[6] = 0x7F; hostile[7] = 0xFF; hostile[8] = 0xFF; hostile[9] = 0xFF;
        File.WriteAllBytes(truncated, hostile);
        var sw = Stopwatch.StartNew();
        var refused = Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(truncated, pass));
        check("A hostile iteration count is refused immediately", refused && sw.ElapsedMilliseconds < 150);

        // The old DSP1 format, produced by the old code (oracle), still opens and then upgrades.
        var legacy = Path.Combine(folder, "legacy.dsproj");
        var legacyPass = "pass1234"; // 8 characters: allowed before, still must open
        var legacyProject = Fixtures.NewProject("Fictional Legacy Project", legacyPass);
        var legacyJson = Fixtures.WriteDsp1(legacy, legacyProject, legacyPass);
        var legacyBytes = File.ReadAllBytes(legacy);
        check("The oracle writes a real DSP1 file", Encoding.ASCII.GetString(legacyBytes, 0, 4) == "DSP1" && ProjectStore.GetFormat(legacy) == ProjectFormat.Dsp1 && legacyBytes.Length == 48 + legacyJson.Length);
        var opened = ProjectStore.Load(legacy, legacyPass);
        check("A DSP1 project written by the old code still opens, with its 8-character passphrase", Fixtures.Describe(opened) == Fixtures.Describe(legacyProject) && opened.LoadedFormat == ProjectFormat.Dsp1);
        check("Opening a DSP1 project does not rewrite it", Fixtures.BytesEqual(legacyBytes, File.ReadAllBytes(legacy)));
        check("A DSP1 project rejects a wrong passphrase", Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(legacy, "pass12345")));
        var flipped = (byte[])legacyBytes.Clone(); flipped[legacyBytes.Length - 1] ^= 1; var flippedPath = Path.Combine(folder, "legacy-flip.dsproj"); File.WriteAllBytes(flippedPath, flipped);
        check("A tampered DSP1 project fails", Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(flippedPath, legacyPass)));
        opened.EnsureMapping("Blake Placeholder", "PERSON", true, 0.9, "Rules", "fictional", null, false);
        ProjectStore.Save(legacy, opened);
        check("The next successful save upgrades DSP1 to DSP2 (the weak legacy passphrase is still accepted for saving)", ProjectStore.GetFormat(legacy) == ProjectFormat.Dsp2);
        var upgraded = ProjectStore.Load(legacy, legacyPass);
        check("The upgraded file keeps every mapping including the new one", upgraded.Mappings.Count == 3 && upgraded.Mappings.Any(m => m.OriginalValue == "Blake Placeholder"));
        check("No .bak or temporary file is left after the upgrade", Fixtures.Leftovers(folder).Where(f => f.Contains("legacy.dsproj")).Count() == 0);

        // Old projects written without the newer fields load with defaults.
        var node = JsonNode.Parse(legacyJson)!.AsObject();
        check("The current project JSON has a ReviewPreferences field", node.ContainsKey("ReviewPreferences"));
        node.Remove("ReviewPreferences"); // what the old app wrote
        var oldPath = Path.Combine(folder, "old-no-prefs.dsproj");
        File.WriteAllBytes(oldPath, LegacyDsp1Oracle.Write(Encoding.UTF8.GetBytes(node.ToJsonString()), legacyPass));
        var oldLoaded = ProjectStore.Load(oldPath, legacyPass);
        check("A project without review preferences loads with empty, versioned preferences",
            oldLoaded.ReviewPreferences.Version == ProjectReviewPreferences.CurrentVersion && oldLoaded.ReviewPreferences.KeepAsIs.Count == 0 && oldLoaded.ReviewPreferences.GenericTerms.Count == 0);
        var nullNode = JsonNode.Parse(legacyJson)!.AsObject(); nullNode["ReviewPreferences"] = null;
        File.WriteAllBytes(oldPath, LegacyDsp1Oracle.Write(Encoding.UTF8.GetBytes(nullNode.ToJsonString()), legacyPass));
        check("A null ReviewPreferences field also loads as empty", ProjectStore.Load(oldPath, legacyPass).ReviewPreferences.KeepAsIs.Count == 0);

        // Buffers are wiped.
        var wiped = new Dictionary<string, bool>();
        ProjectContainer.WipeProbe = (what, buffer) => { wiped[what] = buffer.All(b => b == 0) && (!wiped.ContainsKey(what) || wiped[what]); };
        ProjectStore.Load(path, pass);
        Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(path, "not the passphrase"));
        ProjectStore.Save(path, project);
        ProjectContainer.WipeProbe = null;
        check("Plaintext, file buffer, passphrase bytes and keys are zeroed after Load, failed Load and Save",
            new[] { "load-plaintext", "load-file-bytes", "open-key", "passphrase-bytes", "failed-plaintext", "save-json", "seal-key", "verify-plaintext" }.All(k => wiped.TryGetValue(k, out var ok) && ok));
        using (var sealedProject = ProjectContainer.Seal(Encoding.UTF8.GetBytes("{}"), pass))
        {
            var key = (byte[])typeof(SealedProject).GetProperty("Key", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sealedProject)!;
            check("A sealed project holds a non-zero key until it is disposed", key.Any(b => b != 0));
            sealedProject.Dispose();
            check("Disposing the sealed project zeroes the key", key.All(b => b == 0));
        }
    }
}

// ---- P1: crash safety of Save ---------------------------------------------------------------
static class SaveSafety
{
    public static void Run(Action<string, bool> check, string work)
    {
        var folder = Path.Combine(work, "safety"); Directory.CreateDirectory(folder);
        var pass = "pass1234";
        foreach (var startFormat in new[] { ProjectFormat.Dsp1, ProjectFormat.Dsp2 })
        {
            foreach (var stage in new[] { "temp-written", "temp-verified", "replaced" })
            {
                var path = Path.Combine(folder, startFormat + "-" + stage + ".dsproj");
                var original = Fixtures.NewProject("Fictional Safety " + stage, pass);
                if (startFormat == ProjectFormat.Dsp1) Fixtures.WriteDsp1(path, original, pass);
                else { var seed = Project.New(original.Name, pass); seed.PassphraseAccepted = true; seed.Mappings.AddRange(original.Mappings); ProjectStore.Save(path, seed); }
                var originalBytes = File.ReadAllBytes(path);
                var project = ProjectStore.Load(path, pass);
                project.EnsureMapping("Casey Placeholder", "PERSON", true, 0.9, "Rules", "fictional", null, false);
                var validAtEveryStage = new List<string>();
                ProjectStore.FaultHook = s =>
                {
                    // At every step a complete, loadable copy of the previous content exists.
                    try { ProjectStore.Load(path, pass); validAtEveryStage.Add(s); } catch (InvalidDataException) { }
                    if (s == stage) throw new IOException("simulated failure at " + s);
                };
                var threw = Fixtures.Throws<IOException>(() => ProjectStore.Save(path, project));
                ProjectStore.FaultHook = null;
                var after = ProjectStore.Load(path, pass);
                check(startFormat + " save failing at '" + stage + "' throws and keeps the previous project readable and unchanged",
                    threw && after.Mappings.Count == 2 && !after.Mappings.Any(m => m.OriginalValue == "Casey Placeholder") && Fixtures.BytesEqual(originalBytes, File.ReadAllBytes(path)));
                check(startFormat + " save failing at '" + stage + "' leaves no temporary or backup file", Fixtures.Leftovers(folder).Where(f => f.Contains(startFormat + "-" + stage)).Count() == 0);
                check(startFormat + " save: a valid copy existed at every step up to the failure", validAtEveryStage.Count >= 1 && validAtEveryStage.Contains(stage));
            }
        }

        // A failure after the new file was verified leaves the NEW file in place (the save did succeed).
        {
            var path = Path.Combine(folder, "late.dsproj");
            var p = Fixtures.NewProject("Fictional Late", pass); Fixtures.WriteDsp1(path, p, pass);
            var project = ProjectStore.Load(path, pass); project.EnsureMapping("Devon Placeholder", "PERSON", true, 0.9, "Rules", "fictional", null, false);
            ProjectStore.FaultHook = s => { if (s == "verified") throw new IOException("simulated"); };
            var threw = Fixtures.Throws<IOException>(() => ProjectStore.Save(path, project));
            ProjectStore.FaultHook = null;
            check("A failure after verification keeps the verified new file", threw && ProjectStore.GetFormat(path) == ProjectFormat.Dsp2 && ProjectStore.Load(path, pass).Mappings.Count == 3);
            ProjectStore.Save(path, project);
            check("The next save removes the stale backup", !File.Exists(ProjectStore.BackupPath(path)));
        }

        // Crash layouts the code must recover from (files as a killed process would leave them).
        {
            var p = Fixtures.NewProject("Fictional Crash", pass);
            var oldPath = Path.Combine(folder, "crash-old.dsproj"); var oldBytes = Fixtures.WriteDsp1(oldPath, p, pass).Length > 0 ? File.ReadAllBytes(oldPath) : Array.Empty<byte>();
            var newer = ProjectStore.Load(oldPath, pass); newer.EnsureMapping("Emery Placeholder", "PERSON", true, 0.9, "Rules", "fictional", null, false);

            // (a) new file promoted, stale .bak of the old one still there
            var a = Path.Combine(folder, "crash-a.dsproj"); ProjectStore.Save(a, newer); File.WriteAllBytes(ProjectStore.BackupPath(a), oldBytes);
            check("Layout a (new file plus stale .bak): opens the new file", ProjectStore.Load(a, pass).Mappings.Count == 3);
            ProjectStore.Save(a, newer);
            check("Layout a: the next save removes the .bak", !File.Exists(ProjectStore.BackupPath(a)));

            // (b) target missing, only the .bak
            var b = Path.Combine(folder, "crash-b.dsproj"); File.WriteAllBytes(ProjectStore.BackupPath(b), oldBytes);
            check("Layout b (target missing, .bak only): the project still opens from the .bak", ProjectStore.Load(b, pass).Mappings.Count == 2);
            ProjectStore.Save(b, newer);
            check("Layout b: save restores the target and removes the .bak", ProjectStore.GetFormat(b) == ProjectFormat.Dsp2 && !File.Exists(ProjectStore.BackupPath(b)) && ProjectStore.Load(b, pass).Mappings.Count == 3);

            // (c) target damaged, good .bak
            var c = Path.Combine(folder, "crash-c.dsproj"); File.WriteAllBytes(c, new byte[40]); File.WriteAllBytes(ProjectStore.BackupPath(c), oldBytes);
            check("Layout c (damaged target, good .bak): opens the .bak", ProjectStore.Load(c, pass).Mappings.Count == 2);
            var shouldKeep = Fixtures.Throws<IOException>(() => { ProjectStore.FaultHook = s => { if (s == "temp-written") throw new IOException("x"); }; ProjectStore.Save(c, newer); });
            ProjectStore.FaultHook = null;
            check("Layout c: a failed save does not destroy the good .bak", shouldKeep && File.Exists(ProjectStore.BackupPath(c)) && ProjectStore.Load(c, pass).Mappings.Count == 2);
            ProjectStore.Save(c, newer);
            check("Layout c: a later save repairs the target and removes the .bak", ProjectStore.Load(c, pass).Mappings.Count == 3 && !File.Exists(ProjectStore.BackupPath(c)));

            // (d) damaged target and no .bak
            var d = Path.Combine(folder, "crash-d.dsproj"); File.WriteAllBytes(d, new byte[40]);
            check("Layout d (damaged target, no .bak): fails cleanly", Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(d, pass)));

            // (e) an orphan temporary file from a killed save is swept by the next save
            var e = Path.Combine(folder, "crash-e.dsproj"); ProjectStore.Save(e, newer);
            var orphan = Path.Combine(folder, ".crash-e.dsproj." + Guid.NewGuid().ToString("N") + ".tmp"); File.WriteAllBytes(orphan, new byte[10]); File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));
            ProjectStore.Save(e, newer);
            check("An old orphan temporary file is removed by the next save", !File.Exists(orphan));
        }
    }
}

// ---- P1: passphrase rules -----------------------------------------------------------------
static class PassphraseRules
{
    public static void Run(Action<string, bool> check, string work)
    {
        var folder = Path.Combine(work, "rules"); Directory.CreateDirectory(folder);
        var weak = new[] { "", "short", "pass1234", "abcdefghijk", "aaaaaaaaaaaa", "123456789012", "abcdefghijkl", "qwertyuiop12", "password1234", "Password1234", "ababababababab" };
        check("Weak passphrases are rejected by the policy: " + string.Join(" | ", weak.Where(w => PassphrasePolicy.Check(w).IsAcceptable)), weak.All(w => !PassphrasePolicy.Check(w).IsAcceptable));
        var strong = new[] { "violet-lantern-27 harbor", "correct horse battery staple", "Tr4in-Ship-Quartz!", "mango sunrise over 3 hills" };
        check("Reasonable passphrases are accepted: " + string.Join(" | ", strong.Where(s => !PassphrasePolicy.Check(s).IsAcceptable)), strong.All(s => PassphrasePolicy.Check(s).IsAcceptable));
        var single = PassphrasePolicy.Check("short");
        check("A rejection carries a readable message and the specific problem", single.Message.Contains("12") && single.Problems.Contains(PassphraseProblem.TooShort) && !single.Message.Contains('—'));
        check("Eleven characters fail and twelve (varied) pass", !PassphrasePolicy.Check("aB3-dE6-gH9").IsAcceptable && PassphrasePolicy.Check("aB3-dE6-gH9x").IsAcceptable);
        check("A passphrase built from the project name is flagged", PassphrasePolicy.Check("alpharefund12", "Alpha Refund").Problems.Contains(PassphraseProblem.ContainsProjectName));
        check("Validate throws the dedicated exception type", Fixtures.Throws<PassphrasePolicyException>(() => PassphrasePolicy.Validate("short")));
        check("The exception is also an ArgumentException (older callers still catch it)", Fixtures.Throws<ArgumentException>(() => PassphrasePolicy.Validate("short")));

        // Creating a project with a weak passphrase fails at the first save and writes nothing.
        var path = Path.Combine(folder, "new.dsproj");
        var weakProject = Project.New("Fictional Beta", "pass1234");
        check("Creating a project with a weak passphrase is refused on first save", Fixtures.Throws<PassphrasePolicyException>(() => ProjectStore.Save(path, weakProject)) && !File.Exists(path) && Fixtures.Leftovers(folder).Length == 0);
        var strongProject = Project.New("Fictional Beta", Fixtures.Strong);
        ProjectStore.Save(path, strongProject);
        check("A strong passphrase is accepted for a new project", ProjectStore.GetFormat(path) == ProjectFormat.Dsp2);

        // Changing the passphrase.
        var before = File.ReadAllBytes(path);
        var unlocked = ProjectStore.Load(path, Fixtures.Strong);
        check("Changing to a weak passphrase is refused and the file is unchanged", Fixtures.Throws<PassphrasePolicyException>(() => ProjectStore.ChangePassphrase(path, unlocked, Fixtures.Strong, "weak1234")) && Fixtures.BytesEqual(before, File.ReadAllBytes(path)) && unlocked.Passphrase == Fixtures.Strong);
        check("Changing to an eight-character passphrase is refused (old minimum no longer enough)", Fixtures.Throws<PassphrasePolicyException>(() => ProjectStore.ChangePassphrase(path, unlocked, Fixtures.Strong, "Zx9!kLm2")));
        check("Changing to the same passphrase is refused", Fixtures.Throws<ArgumentException>(() => ProjectStore.ChangePassphrase(path, unlocked, Fixtures.Strong, Fixtures.Strong)));
        check("Changing with a wrong current passphrase is refused", Fixtures.Throws<InvalidDataException>(() => ProjectStore.ChangePassphrase(path, unlocked, "wrong current passphrase", "new strong passphrase 55")));
        ProjectStore.ChangePassphrase(path, unlocked, Fixtures.Strong, "new strong passphrase 55");
        check("A strong new passphrase works: the old one stops and the new one opens", Fixtures.Throws<InvalidDataException>(() => ProjectStore.Load(path, Fixtures.Strong)) && ProjectStore.Load(path, "new strong passphrase 55").Name == "Fictional Beta" && Fixtures.Leftovers(folder).Length == 0);

        // A legacy 8-character project can be changed to a strong passphrase and then still opens.
        var legacy = Path.Combine(folder, "legacy.dsproj"); var legacyProject = Fixtures.NewProject("Fictional Gamma", "pass1234"); Fixtures.WriteDsp1(legacy, legacyProject, "pass1234");
        var opened = ProjectStore.Load(legacy, "pass1234");
        check("The legacy passphrase is reported as below the new rules (so the interface can suggest changing it)", !PassphrasePolicy.Check(opened.Passphrase).IsAcceptable);
        ProjectStore.ChangePassphrase(legacy, opened, "pass1234", "much stronger passphrase 9");
        check("Changing a legacy DSP1 project's passphrase upgrades it to DSP2", ProjectStore.GetFormat(legacy) == ProjectFormat.Dsp2 && ProjectStore.Load(legacy, "much stronger passphrase 9").Mappings.Count == 2);
    }
}

// ---- P2: the project catalog --------------------------------------------------------------
static class CatalogTests
{
    public static void Run(Action<string, bool> check, string work)
    {
        var root = Path.Combine(work, "app-data"); Directory.CreateDirectory(root);
        var catalogPath = Path.Combine(root, "projects.json");
        check("The catalog is pointed at the temp folder, not the user's profile", ProjectCatalog.Root == root && !ProjectCatalog.ProjectsFolder.Contains("AppData\\Local\\Desensitizer"));

        // An old catalog: plaintext names and absolute paths, one managed project and one stored elsewhere.
        var managed = ProjectCatalog.NewProjectPath(); var managedProject = Fixtures.NewProject("Fictional Orion Refund Review"); ProjectStore.Save(managed, managedProject);
        var elsewhere = Path.Combine(work, "elsewhere", "Fictional Quartz Vendor Audit.dsproj"); Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        var outside = Fixtures.NewProject("Fictional Quartz Vendor Audit"); ProjectStore.Save(elsewhere, outside);
        var missing = Path.Combine(work, "gone", "Fictional Missing.dsproj");
        var oldId1 = "11111111111111111111111111111111"; var oldId2 = "22222222222222222222222222222222";
        var legacyJson = JsonSerializer.Serialize(new[]
        {
            new { Id = oldId1, Name = "Fictional Orion Refund Review", FilePath = managed, LastOpened = DateTimeOffset.UtcNow.AddDays(-1), LastOpenedLabel = "x" },
            new { Id = oldId2, Name = "Fictional Quartz Vendor Audit", FilePath = elsewhere, LastOpened = DateTimeOffset.UtcNow, LastOpenedLabel = "x" },
            new { Id = "33333333333333333333333333333333", Name = "Fictional Missing", FilePath = missing, LastOpened = DateTimeOffset.UtcNow.AddDays(-9), LastOpenedLabel = "x" }
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(catalogPath, legacyJson);
        var legacyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath)));

        var entries = ProjectCatalog.Load();
        check("Old catalog entries retain available and temporarily missing projects", entries.Count == 3 && entries.Any(e => e.FilePath == managed && e.Id == oldId1) && entries.Any(e => e.FilePath == elsewhere && e.Id == oldId2));
        check("Migrated entries show neutral labels, not the old names", entries.All(e => e.Name.StartsWith("Project ") && e.Label.StartsWith("Project ")) && entries.Select(e => e.Label).Distinct().Count() == 3);
        var text = File.ReadAllText(catalogPath);
        check("The catalog on disk was rewritten in the new format", legacyHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))) && text.Contains("\"version\": 2"));
        check("The new catalog holds no project names", !text.Contains("Orion") && !text.Contains("Quartz") && !text.Contains("Fictional"));
        check("The new catalog holds no folder or file paths", !text.Contains(work) && !text.Contains("elsewhere") && !text.Contains(Path.GetFileName(elsewhere)) && !text.Contains(":\\\\"));
        check("A project outside the managed folder is stored sealed, not as a path", text.Contains("protectedLocation") && !text.Contains("Quartz") && !text.Contains(Path.GetFileNameWithoutExtension(elsewhere)));
        check("A managed project is stored by file name only, relative to the Projects folder", text.Contains("\"location\": \"Projects/" + Path.GetFileName(managed) + "\""));
        check("No plaintext copy of the old catalog remains (.bak or temp)", Directory.GetFiles(root).All(f => !File.ReadAllText(f).Contains("Quartz")) && Directory.GetFiles(root, "*.tmp").Length == 0);
        check("Migration lists a missing project without forgetting its catalog row", entries.Any(e => e.FilePath == missing) && JsonDocument.Parse(text).RootElement.GetProperty("entries").GetArrayLength() == 3);

        // Loading again is stable.
        var again = ProjectCatalog.Load();
        check("Loading the new catalog returns the same entries and labels", again.Count == 3 && again.All(a => entries.Any(e => e.Id == a.Id && e.Label == a.Label && e.FilePath == a.FilePath)));

        var offline = elsewhere + ".offline";
        File.Move(elsewhere, offline);
        try
        {
            var disconnected = ProjectCatalog.Load();
            check("A disconnected external project remains in the recent-project list", disconnected.Any(e => e.Id == oldId2 && e.FilePath == elsewhere && !e.NameRevealed));
            ProjectCatalog.Save(disconnected);
            check("Saving while disconnected preserves the external project entry", ProjectCatalog.Load().Any(e => e.Id == oldId2 && e.FilePath == elsewhere));
            var disconnectedJson = File.ReadAllText(catalogPath);
            check("Preserving an offline project does not expose its name or path", !disconnectedJson.Contains("Quartz") && !disconnectedJson.Contains(elsewhere) && !disconnectedJson.Contains("elsewhere"));
        }
        finally { File.Move(offline, elsewhere); }
        check("Reconnected project uses its original catalog identity", ProjectCatalog.Load().Any(e => e.Id == oldId2 && e.FilePath == elsewhere) && ProjectStore.Load(elsewhere, Fixtures.Strong).Name == "Fictional Quartz Vendor Audit");

        // Showing the real name after unlock, memory only.
        var entry = again.First(e => e.FilePath == managed);
        var unlocked = ProjectStore.Load(managed, Fixtures.Strong);
        ProjectCatalog.RevealName(entry, unlocked);
        check("RevealName shows the real name from the unlocked project", entry.Name == "Fictional Orion Refund Review" && entry.NameRevealed);
        ProjectCatalog.Save(again);
        check("Saving after RevealName still writes no real name", !File.ReadAllText(catalogPath).Contains("Orion"));
        ProjectCatalog.HideName(entry);
        check("HideName returns to the neutral label", entry.Name == entry.Label && !entry.NameRevealed);

        // The old UI creates entries with Name and FilePath only: still works, label assigned on save.
        var created = ProjectCatalog.NewProjectPath(); ProjectStore.Save(created, Fixtures.NewProject("Fictional Zephyr Rollout"));
        var fresh = new ProjectEntry { Name = "Fictional Zephyr Rollout", FilePath = created };
        var list = ProjectCatalog.Load(); list.Insert(0, fresh);
        ProjectCatalog.Save(list);
        var reread = ProjectCatalog.Load();
        check("A new entry created the old way gets the next free neutral label and persists without its name",
            fresh.Label.StartsWith("Project ") && reread.Any(e => e.Id == fresh.Id && e.Label == fresh.Label && e.Name == fresh.Label) && !File.ReadAllText(catalogPath).Contains("Zephyr"));
        check("Labels are unique", reread.Select(e => e.Label).Distinct().Count() == reread.Count);
        var custom = new ProjectEntry { Label = "Client matter A", FilePath = created, Name = "ignored" };
        ProjectCatalog.Save(new[] { custom });
        check("A caller chosen neutral label is kept", ProjectCatalog.Load().Single().Label == "Client matter A");

        // Atomic write: a failure before replace leaves the previous catalog intact and no temp file.
        var good = File.ReadAllBytes(catalogPath);
        ProjectCatalogStorage.FaultHook = s => throw new IOException("simulated");
        var threw = Fixtures.Throws<IOException>(() => ProjectCatalog.Save(list));
        ProjectCatalogStorage.FaultHook = null;
        check("A catalog write that fails midway leaves the previous catalog unchanged and no temp file", threw && Fixtures.BytesEqual(good, File.ReadAllBytes(catalogPath)) && Directory.GetFiles(root, "*.tmp").Length == 0);

        // Damaged catalog: falls back to the backup, then to empty. Never throws.
        ProjectCatalog.Save(reread); ProjectCatalog.Save(reread); // second save creates the .bak
        check("A rolling .bak of the neutral catalog exists after a second save", File.Exists(catalogPath + ".bak"));
        File.WriteAllText(catalogPath, "{ not json");
        check("A damaged catalog recovers from the backup", ProjectCatalog.Load().Count == reread.Count);
        File.WriteAllText(catalogPath + ".bak", "also broken");
        check("A damaged catalog and backup load as an empty list without throwing", ProjectCatalog.Load().Count == 0);
        var orphans = ProjectCatalog.FindUncataloged(new List<ProjectEntry>());
        check("Projects in the managed folder can be rediscovered when the catalog is lost", orphans.Count >= 2 && orphans.All(o => o.Label.StartsWith("Project ")));

        // Adopting an outside project.
        var outsideEntry = new ProjectEntry { FilePath = elsewhere };
        var adopted = ProjectCatalog.AdoptIntoManagedFolder(outsideEntry);
        check("An outside project can be copied into the managed folder (original untouched)", adopted.StartsWith(ProjectCatalog.ProjectsFolder) && File.Exists(elsewhere) && ProjectStore.Load(adopted, Fixtures.Strong).Name == "Fictional Quartz Vendor Audit");
    }
}

// ---- P3: keep-as-is labels live inside the project --------------------------------------
static class ReviewPreferenceTests
{
    public static void Run(Action<string, bool> check, string work)
    {
        var folder = Path.Combine(work, "prefs"); Directory.CreateDirectory(folder);
        ProjectScope.Unbind();
        check("With no project bound, no label is exempt", !LocalAiReview.IsClearlyGenericCandidate("Boarding Pass", "PRODUCT") && ReviewAllowList.Current.Labels.Count == 0 && ReviewAllowList.Current.Path == null);

        var a = Fixtures.NewProject("Fictional Project A"); var b = Fixtures.NewProject("Fictional Project B");
        ProjectScope.Bind(a);
        check("Adding a label to the bound project stores it in the project", ReviewAllowList.Current.Add("Boarding Pass") && a.ReviewPreferences.KeepAsIs.Count == 1 && ReviewAllowList.Current.IsProjectBound);
        check("The label silences a matching product in project A", LocalAiReview.IsClearlyGenericCandidate("Boarding Pass", "PRODUCT") && LocalAiReview.IsClearlyGenericCandidate("boarding   pass", "SYSTEM"));
        check("The label does not silence a person, company or identifier with the same spelling",
            !LocalAiReview.IsClearlyGenericCandidate("Boarding Pass", "PERSON") && !LocalAiReview.IsClearlyGenericCandidate("Boarding Pass", "ORGANIZATION") && !ReviewAllowList.Current.Contains("Boarding Pass", "IDENTIFIER") && !ReviewAllowList.Current.Contains("Boarding Pass", "ADDRESS"));
        ProjectScope.Bind(b);
        check("Project B is not affected by project A's label", !LocalAiReview.IsClearlyGenericCandidate("Boarding Pass", "PRODUCT") && ReviewAllowList.Current.Labels.Count == 0);
        ProjectScope.Bind(a);
        check("Rebinding project A brings its label back", LocalAiReview.IsClearlyGenericCandidate("Boarding Pass", "PRODUCT"));
        ProjectScope.Unbind();
        check("Closing the project removes the exemption", !LocalAiReview.IsClearlyGenericCandidate("Boarding Pass", "PRODUCT"));

        // Category and context limits.
        ProjectScope.Bind(a);
        check("A category outside the allowed set is refused", !a.ReviewPreferences.AddKeepAsIs("Maya Lin", new[] { "PERSON" }) && !a.ReviewPreferences.AddKeepAsIs("Acme", new[] { "ORGANIZATION" }) && !a.ReviewPreferences.AddKeepAsIs("Acme", new[] { "PRODUCT", "PERSON" }));
        check("Values with digits, emails, URLs and placeholders are refused", new[] { "Employee AB123", "maya@example.com", "http://example.com", "[PERSON_001]", "x", "12345" }.All(v => !a.ReviewPreferences.AddKeepAsIs(v)));
        check("A product-only entry does not apply to the SYSTEM category", a.ReviewPreferences.AddKeepAsIs("Ticket Desk", new[] { "PRODUCT" }) && LocalAiReview.IsClearlyGenericCandidate("Ticket Desk", "PRODUCT") && !LocalAiReview.IsClearlyGenericCandidate("Ticket Desk", "SYSTEM"));
        check("A context-limited entry applies only where its context appears", a.ReviewPreferences.AddKeepAsIs("Refund Console", new[] { "SYSTEM" }, "Operations Runbook")
            && a.ReviewPreferences.MatchesKeepAsIs("Refund Console", "SYSTEM", "See the operations runbook for steps") && !a.ReviewPreferences.MatchesKeepAsIs("Refund Console", "SYSTEM", "Vendor contract schedule"));
        check("A context-limited entry never applies when the caller supplies no context", !a.ReviewPreferences.MatchesKeepAsIs("Refund Console", "SYSTEM") && !LocalAiReview.IsClearlyGenericCandidate("Refund Console", "SYSTEM"));
        check("An entry can be removed again", ReviewAllowList.Current.Remove("Boarding Pass") && !LocalAiReview.IsClearlyGenericCandidate("Boarding Pass", "PRODUCT"));
        a.ReviewPreferences.AddKeepAsIs("Boarding Pass");
        var cacheLabelsBefore = string.Join("|", ReviewAllowList.Current.Labels);
        a.ReviewPreferences.AddKeepAsIs("Boarding Pass", new[] { "PRODUCT" }, "ticket rules");
        check("Review-cache identity (Labels) changes when a category or context entry is added", cacheLabelsBefore != string.Join("|", ReviewAllowList.Current.Labels));

        // Stored inside the encrypted project, nowhere else.
        var path = Path.Combine(folder, "a.dsproj");
        ProjectStore.Save(path, a);
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        check("Saved labels are not readable in the project file", !raw.Contains("Boarding") && !raw.Contains("Refund Console"));
        check("No plaintext label file was created anywhere in the test area", Directory.GetFiles(work, "keep-as-is-labels*", SearchOption.AllDirectories).Length == 0);
        var reopened = ProjectStore.Load(path, Fixtures.Strong);
        check("Labels, categories and context hints survive save and load", reopened.ReviewPreferences.KeepAsIs.Count == 4 && reopened.ReviewPreferences.Version == 1
            && reopened.ReviewPreferences.KeepAsIs.Any(e => e.Value == "Refund Console" && e.ContextHint == "operations runbook" && e.Categories.SequenceEqual(new[] { "SYSTEM" })));
        ProjectScope.Bind(reopened);
        check("A reopened project applies its own labels", LocalAiReview.IsClearlyGenericCandidate("Boarding Pass", "PRODUCT"));
        ProjectScope.Unbind();

        // The old global file: read only, never applied silently, import is explicit.
        var legacyFile = Path.Combine(folder, "keep-as-is-labels.json");
        File.WriteAllText(legacyFile, "{\"version\":1,\"labels\":[\"Warehouse Workflow\",\"Pick Ticket\",\"Employee AB123\",\"maya@example.com\"]}");
        var legacyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(legacyFile)));
        var legacyLabels = LegacyKeepAsIsFile.Read(legacyFile);
        check("The legacy loader lists acceptable old labels and skips unacceptable ones", legacyLabels.SequenceEqual(new[] { "Warehouse Workflow", "Pick Ticket" }));
        check("The legacy loader never modifies the file", legacyHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(legacyFile))));
        var c = Fixtures.NewProject("Fictional Project C"); ProjectScope.Bind(c);
        check("Old global entries are not applied until the user imports them", !LocalAiReview.IsClearlyGenericCandidate("Warehouse Workflow", "PRODUCT") && c.ReviewPreferences.LegacyImportDecision == "");
        var imported = c.ReviewPreferences.ImportLegacyLabels(legacyLabels);
        check("Importing adds category-limited entries to this project only and records the decision",
            imported == 2 && LocalAiReview.IsClearlyGenericCandidate("Warehouse Workflow", "PRODUCT") && !LocalAiReview.IsClearlyGenericCandidate("Warehouse Workflow", "PERSON") && c.ReviewPreferences.LegacyImportDecision == "Imported");
        ProjectScope.Bind(b);
        check("Another project does not receive the imported entries", !LocalAiReview.IsClearlyGenericCandidate("Warehouse Workflow", "PRODUCT"));
        ProjectScope.Unbind();
        check("The legacy loader on a missing or damaged file returns nothing", LegacyKeepAsIsFile.Read(Path.Combine(folder, "missing.json")).Count == 0 && LegacyKeepAsIsFile.Read(legacyFile + "x").Count == 0);
        File.WriteAllText(legacyFile, "{ broken");
        check("A damaged legacy file is ignored", LegacyKeepAsIsFile.Read(legacyFile).Count == 0);
        check("The application default is empty and unbound (no file is read at startup)", ReviewAllowList.Current.Path == null && ReviewAllowList.Current.Labels.Count == 0);
    }
}

// ---- P4: generic vocabulary is per project, and suppressed organizations are reported ---------
static class GenericTermTests
{
    public static void Run(Action<string, bool> check, string work)
    {
        ProjectScope.Unbind(); DocumentPrivacyPolicy.ClearProjectTerms(); DocumentPrivacyPolicy.ClearSuppressed();
        var brdTerms = new[] { "GCASH", "Finance", "Treasury", "Admin", "Excel", "Dashboard", "Passenger", "OFW", "Refund Eligibility List Generation" };
        var stillBuiltIn = brdTerms.Where(t => DocumentPrivacyPolicy.IsGeneric(t, "ORGANIZATION") || DocumentPrivacyPolicy.IsGeneric(t, "DEPARTMENT")).ToArray();
        check("Domain vocabulary of one test document is not built in" + (stillBuiltIn.Length > 0 ? " [still generic: " + string.Join(", ", stillBuiltIn) + "]" : ""), stillBuiltIn.Length == 0);
        check("Truly generic wording stays built in", new[] { "Full Name", "Email Address", "Revision History", "Organization", "Plain Text", "Approver" }.All(t => DocumentPrivacyPolicy.IsGeneric(t, "PRODUCT")));
        check("Real company names that share words with the old list are no longer swallowed", !DocumentPrivacyPolicy.IsGeneric("Treasury", "ORGANIZATION") && !DocumentPrivacyPolicy.IsGeneric("Finance", "ORGANIZATION"));

        // Suppression is reported for organization and vendor candidates.
        DocumentPrivacyPolicy.ClearSuppressed();
        DocumentPrivacyPolicy.IsGeneric("Organization", "ORGANIZATION");
        DocumentPrivacyPolicy.IsGeneric("Approver", "VENDOR");
        DocumentPrivacyPolicy.IsGeneric("Organization", "ORGANIZATION");
        DocumentPrivacyPolicy.IsGeneric("Full Name", "PRODUCT");
        var suppressed = DocumentPrivacyPolicy.SuppressedOrganizations;
        check("Organization and vendor candidates dropped by the built-in list are reported with a reason and count",
            suppressed.Count == 2 && suppressed.Any(s => s.Value == "Organization" && s.Category == "ORGANIZATION" && s.Count == 2 && s.Reason == "Built-in generic term") && suppressed.Any(s => s.Value == "Approver" && s.Category == "VENDOR"));
        check("Other categories are not reported", suppressed.All(s => s.Category is "ORGANIZATION" or "VENDOR"));

        var project = Fixtures.NewProject("Fictional Terms Project");
        ProjectScope.Bind(project);
        check("A person-like category cannot be given a generic term", !project.ReviewPreferences.AddGenericTerm("Maya Lin", new[] { "PERSON" }) && !project.ReviewPreferences.AddGenericTerm("Employee AB123") && !project.ReviewPreferences.AddGenericTerm("x"));
        check("Adding a generic term takes effect at once for the bound project", project.ReviewPreferences.AddGenericTerm("Treasury") && DocumentPrivacyPolicy.IsGeneric("Treasury", "DEPARTMENT"));
        check("A project term never silences a person, email or identifier", !DocumentPrivacyPolicy.IsGeneric("Treasury", "PERSON") && !DocumentPrivacyPolicy.IsGeneric("Treasury", "IDENTIFIER") && !DocumentPrivacyPolicy.IsGeneric("Treasury", "EMAIL"));
        check("The organization it hides is listed as suppressed, with the project term as the reason",
            DocumentPrivacyPolicy.IsGeneric("Treasury", "ORGANIZATION") && DocumentPrivacyPolicy.SuppressedOrganizations.Any(s => s.Value == "Treasury" && s.Category == "ORGANIZATION" && s.Reason == "Project generic term"));
        check("A category-limited term applies only to its categories", project.ReviewPreferences.AddGenericTerm("Dashboard", new[] { "PRODUCT" }) && DocumentPrivacyPolicy.IsGeneric("Dashboard", "PRODUCT") && !DocumentPrivacyPolicy.IsGeneric("Dashboard", "ORGANIZATION"));
        check("The candidate pipeline (Apply) drops a project term and a different project keeps the candidate",
            DocumentPrivacyPolicy.Apply("The Treasury team approves.", new[] { new Candidate { Value = "Treasury", Category = "DEPARTMENT" } }).Count == 0);
        var path = Path.Combine(work, "terms.dsproj");
        ProjectStore.Save(path, project);
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        var reopened = ProjectStore.Load(path, Fixtures.Strong);
        check("Generic terms are stored inside the encrypted project and survive a reload", !raw.Contains("Treasury") && reopened.ReviewPreferences.GenericTerms.Count == 2 && reopened.ReviewPreferences.GenericTerms.Any(t => t.Value == "Dashboard" && t.Categories.SequenceEqual(new[] { "PRODUCT" })));
        var other = Fixtures.NewProject("Fictional Other Project");
        ProjectScope.Bind(other);
        check("Another project is not affected, and the suppressed list starts empty", !DocumentPrivacyPolicy.IsGeneric("Treasury", "DEPARTMENT") && DocumentPrivacyPolicy.SuppressedOrganizations.Count == 0
            && DocumentPrivacyPolicy.Apply("The Treasury team approves.", new[] { new Candidate { Value = "Treasury", Category = "DEPARTMENT" } }).Count == 1);
        ProjectScope.Bind(project);
        check("Removing a term takes effect at once", project.ReviewPreferences.RemoveGenericTerm("Treasury") && !DocumentPrivacyPolicy.IsGeneric("Treasury", "DEPARTMENT"));
        ProjectScope.Unbind();
        check("Closing the project removes its terms", !DocumentPrivacyPolicy.IsGeneric("Dashboard", "PRODUCT") && DocumentPrivacyPolicy.SuppressedOrganizations.Count == 0);

        // A detector outside this class can report its own drops.
        DocumentPrivacyPolicy.RecordSuppressed("Cloud Services", "ORGANIZATION", "Role-title filter");
        DocumentPrivacyPolicy.RecordSuppressed("Cloud Services", "PERSON", "Role-title filter");
        check("RecordSuppressed lets other filters report organizations (and ignores other categories)", DocumentPrivacyPolicy.SuppressedOrganizations.Count == 1 && DocumentPrivacyPolicy.SuppressedOrganizations[0].Value == "Cloud Services");
        DocumentPrivacyPolicy.ClearSuppressed();
        check("The suppressed list is bounded", Enumerable.Range(0, 800).Select(i => { DocumentPrivacyPolicy.RecordSuppressed("Org " + new string((char)('a' + i % 26), 1 + i / 26), "ORGANIZATION", "t"); return 0; }).Count() == 800 && DocumentPrivacyPolicy.SuppressedOrganizations.Count <= 500);
        DocumentPrivacyPolicy.ClearSuppressed();
    }
}

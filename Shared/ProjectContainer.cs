using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Desensitizer;

public enum ProjectFormat
{
    Unknown = 0,
    // "DSP1": magic, salt(16), nonce(12), tag(16), ciphertext. PBKDF2-SHA256 150,000 iterations. AAD = "DSP1".
    Dsp1 = 1,
    // "DSP2": see ProjectContainer. Self-describing header, fully authenticated.
    Dsp2 = 2
}

// The result of sealing a project: the bytes to write plus the key that produced them, so the
// writer can verify the file it just wrote without paying for a second key derivation.
public sealed class SealedProject : IDisposable
{
    public byte[] FileBytes { get; }
    internal byte[] Key { get; }
    internal int HeaderLength { get; }
    internal SealedProject(byte[] fileBytes, byte[] key, int headerLength)
    {
        FileBytes = fileBytes;
        Key = key;
        HeaderLength = headerLength;
    }
    public void Dispose() => ProjectContainer.Wipe("seal-key", Key);
}

// Encrypted project container.
//
// DSP2 layout (all integers big endian):
//   0   4  magic "DSP2"
//   4   1  format version (2)
//   5   1  KDF id (1 = PBKDF2-HMAC-SHA256)
//   6   4  KDF cost: iteration count
//   10  1  salt length (16)
//   11  16 salt
//   27  1  nonce length (12)
//   28  12 nonce
//   40  1  tag length (16)
//   41  16 GCM tag
//   57  .. ciphertext (AES-256-GCM of the project JSON)
// The associated data is the whole header, bytes 0 to 40 (everything before the tag), so changing
// the version, KDF, cost, salt, nonce or any length makes decryption fail. The reader refuses any length or cost outside the
// accepted range before it spends time on key derivation.
public static class ProjectContainer
{
    public const int Dsp2Iterations = 600_000;
    public const int MinAcceptedIterations = 100_000;
    public const int MaxAcceptedIterations = 5_000_000;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const byte VersionByte = 2;
    private const byte KdfPbkdf2Sha256 = 1;
    private const int HeaderLength = 4 + 1 + 1 + 4 + 1 + SaltLength + 1 + NonceLength + 1; // up to and including the tag length byte
    private const int Dsp1HeaderLength = 4 + 16 + 12 + 16;
    private const int Dsp1Iterations = 150_000;
    private static readonly byte[] MagicDsp1 = Encoding.ASCII.GetBytes("DSP1");
    private static readonly byte[] MagicDsp2 = Encoding.ASCII.GetBytes("DSP2");

    // Test seam: called with a label and the buffer right after it was zeroed, so a test can see that it was wiped.
    public static Action<string, byte[]>? WipeProbe;

    // Zeroes a buffer that held a key or plaintext.
    public static void Wipe(string what, byte[] buffer)
    {
        CryptographicOperations.ZeroMemory(buffer);
        WipeProbe?.Invoke(what, buffer);
    }

    public const string BadPassphraseMessage = "Incorrect passphrase or corrupted project.";
    public const string NotAProjectMessage = "Not a valid project file.";

    public static ProjectFormat Detect(byte[] bytes)
    {
        if (bytes.Length >= 4 && Matches(bytes, MagicDsp2) && bytes.Length >= HeaderLength + TagLength) return ProjectFormat.Dsp2;
        if (bytes.Length >= Dsp1HeaderLength && Matches(bytes, MagicDsp1)) return ProjectFormat.Dsp1;
        return ProjectFormat.Unknown;
    }

    public static SealedProject Seal(byte[] plaintext, string passphrase, int iterations = Dsp2Iterations)
    {
        if (iterations < MinAcceptedIterations || iterations > MaxAcceptedIterations) throw new ArgumentOutOfRangeException(nameof(iterations));
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var key = Derive(passphrase, salt, iterations);
        try
        {
            var header = new byte[HeaderLength];
            Buffer.BlockCopy(MagicDsp2, 0, header, 0, 4);
            header[4] = VersionByte;
            header[5] = KdfPbkdf2Sha256;
            header[6] = (byte)(iterations >> 24); header[7] = (byte)(iterations >> 16); header[8] = (byte)(iterations >> 8); header[9] = (byte)iterations;
            header[10] = SaltLength;
            Buffer.BlockCopy(salt, 0, header, 11, SaltLength);
            header[27] = NonceLength;
            Buffer.BlockCopy(nonce, 0, header, 28, NonceLength);
            header[40] = TagLength;
            var cipher = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            using (var aes = new AesGcm(key, TagLength)) aes.Encrypt(nonce, plaintext, cipher, tag, header);
            var file = new byte[HeaderLength + TagLength + cipher.Length];
            Buffer.BlockCopy(header, 0, file, 0, HeaderLength);
            Buffer.BlockCopy(tag, 0, file, HeaderLength, TagLength);
            Buffer.BlockCopy(cipher, 0, file, HeaderLength + TagLength, cipher.Length);
            var result = new SealedProject(file, key, HeaderLength);
            key = null!; // ownership moved to the result
            return result;
        }
        finally
        {
            if (key != null) Wipe("seal-key", key);
        }
    }

    // Opens either format. The caller owns the returned plaintext and must zero it.
    public static byte[] Open(byte[] file, string passphrase, out ProjectFormat format)
    {
        format = Detect(file);
        if (format == ProjectFormat.Dsp1) return OpenDsp1(file, passphrase);
        if (format == ProjectFormat.Dsp2) return OpenDsp2(file, passphrase);
        throw new InvalidDataException(NotAProjectMessage);
    }

    // Confirms that bytes read back from disk are the DSP2 file we meant to write: same header, and
    // they decrypt with the key that sealed them to exactly the expected plaintext.
    public static bool VerifyReadBack(byte[] readBack, SealedProject sealedProject, byte[] expectedPlaintext)
    {
        byte[]? plain = null;
        try
        {
            if (Detect(readBack) != ProjectFormat.Dsp2 || readBack.Length != sealedProject.FileBytes.Length) return false;
            if (!CryptographicOperations.FixedTimeEquals(readBack, sealedProject.FileBytes)) return false;
            plain = new byte[readBack.Length - HeaderLength - TagLength];
            using var aes = new AesGcm(sealedProject.Key, TagLength);
            aes.Decrypt(readBack.AsSpan(28, NonceLength), readBack.AsSpan(HeaderLength + TagLength), readBack.AsSpan(HeaderLength, TagLength), plain, readBack.AsSpan(0, HeaderLength));
            return plain.Length == expectedPlaintext.Length && CryptographicOperations.FixedTimeEquals(plain, expectedPlaintext);
        }
        catch (CryptographicException) { return false; }
        finally { if (plain != null) Wipe("verify-plaintext", plain); }
    }

    private static byte[] OpenDsp2(byte[] file, string passphrase)
    {
        // Structural checks first: nothing here costs a key derivation.
        if (file[4] != VersionByte || file[5] != KdfPbkdf2Sha256 || file[10] != SaltLength || file[27] != NonceLength || file[40] != TagLength)
            throw new InvalidDataException(NotAProjectMessage);
        var iterations = (file[6] << 24) | (file[7] << 16) | (file[8] << 8) | file[9];
        if (iterations < MinAcceptedIterations || iterations > MaxAcceptedIterations) throw new InvalidDataException(NotAProjectMessage);
        var cipherLength = file.Length - HeaderLength - TagLength;
        var salt = file.AsSpan(11, SaltLength).ToArray();
        var key = Derive(passphrase, salt, iterations);
        var plain = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(file.AsSpan(28, NonceLength), file.AsSpan(HeaderLength + TagLength), file.AsSpan(HeaderLength, TagLength), plain, file.AsSpan(0, HeaderLength));
            return plain;
        }
        catch (CryptographicException)
        {
            Wipe("failed-plaintext", plain);
            throw new InvalidDataException(BadPassphraseMessage);
        }
        finally { Wipe("open-key", key); }
    }

    private static byte[] OpenDsp1(byte[] file, string passphrase)
    {
        var salt = file.AsSpan(4, 16).ToArray();
        var key = Derive(passphrase, salt, Dsp1Iterations);
        var plain = new byte[file.Length - Dsp1HeaderLength];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(file.AsSpan(20, 12), file.AsSpan(Dsp1HeaderLength), file.AsSpan(32, 16), plain, MagicDsp1);
            return plain;
        }
        catch (CryptographicException)
        {
            Wipe("failed-plaintext", plain);
            throw new InvalidDataException(BadPassphraseMessage);
        }
        finally { Wipe("open-key", key); }
    }

    private static byte[] Derive(string passphrase, byte[] salt, int iterations)
    {
        // Same UTF-8 bytes Rfc2898DeriveBytes(string, ...) uses, so DSP1 files keep opening.
        var password = Encoding.UTF8.GetBytes(passphrase);
        try { return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32); }
        finally { Wipe("passphrase-bytes", password); }
    }

    private static bool Matches(byte[] value, byte[] expect)
    {
        for (var i = 0; i < expect.Length; i++) if (value[i] != expect[i]) return false;
        return true;
    }
}

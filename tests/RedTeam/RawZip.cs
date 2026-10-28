using System.IO.Compression;
using System.Text;

namespace RedTeam;

// A minimal ZIP writer that can produce shapes System.IO.Compression refuses to write:
// duplicate names, archive comments, extra fields, data descriptors, ZIP64, odd names.
public sealed class ZEntry
{
    public string Name = "";
    public byte[] Data = Array.Empty<byte>();
    public bool Deflate = true;
    public byte[] ExtraLocal = Array.Empty<byte>();
    public byte[] ExtraCentral = Array.Empty<byte>();
    public string EntryComment = "";
    public bool DataDescriptor;
    public bool Zip64;
    public bool Utf8Name = true;
    public byte[]? RawName; // overrides Name bytes
}

public static class RawZip
{
    static readonly uint[] Table = MakeTable();
    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++) { var c = i; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
        return t;
    }
    public static uint Crc(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    static byte[] Compress(byte[] data)
    {
        var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, true)) d.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    public static byte[] Write(IEnumerable<ZEntry> entries, string comment = "", byte[]? rawComment = null)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var central = new List<(ZEntry E, byte[] Name, uint Crc, long CSize, long USize, long Offset, ushort Method, ushort Flags)>();
        foreach (var e in entries)
        {
            var name = e.RawName ?? Encoding.UTF8.GetBytes(e.Name);
            var crc = Crc(e.Data);
            var payload = e.Deflate ? Compress(e.Data) : e.Data;
            ushort method = (ushort)(e.Deflate ? 8 : 0);
            ushort flags = (ushort)((e.Utf8Name ? 0x800 : 0) | (e.DataDescriptor ? 0x8 : 0));
            var offset = ms.Position;
            w.Write(0x04034b50u); w.Write((ushort)(e.Zip64 ? 45 : 20)); w.Write(flags); w.Write(method); w.Write((ushort)0); w.Write((ushort)0x21);
            var extraLocal = e.ExtraLocal;
            if (e.Zip64)
            {
                var z = new MemoryStream(); var zw = new BinaryWriter(z);
                zw.Write((ushort)1); zw.Write((ushort)16); zw.Write((long)e.Data.Length); zw.Write((long)payload.Length);
                extraLocal = z.ToArray().Concat(extraLocal).ToArray();
            }
            if (e.DataDescriptor) { w.Write(0u); w.Write(0u); w.Write(0u); }
            else if (e.Zip64) { w.Write(crc); w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); }
            else { w.Write(crc); w.Write((uint)payload.Length); w.Write((uint)e.Data.Length); }
            w.Write((ushort)name.Length); w.Write((ushort)extraLocal.Length);
            w.Write(name); w.Write(extraLocal); w.Write(payload);
            if (e.DataDescriptor) { w.Write(0x08074b50u); w.Write(crc); w.Write((uint)payload.Length); w.Write((uint)e.Data.Length); }
            central.Add((e, name, crc, payload.Length, e.Data.Length, offset, method, flags));
        }
        var cdStart = ms.Position;
        foreach (var c in central)
        {
            var e = c.E;
            var extra = e.ExtraCentral;
            if (e.Zip64)
            {
                var z = new MemoryStream(); var zw = new BinaryWriter(z);
                zw.Write((ushort)1); zw.Write((ushort)16); zw.Write(c.USize); zw.Write(c.CSize);
                extra = z.ToArray().Concat(extra).ToArray();
            }
            var ec = Encoding.UTF8.GetBytes(e.EntryComment);
            w.Write(0x02014b50u); w.Write((ushort)(e.Zip64 ? 45 : 20)); w.Write((ushort)(e.Zip64 ? 45 : 20)); w.Write(c.Flags); w.Write(c.Method); w.Write((ushort)0); w.Write((ushort)0x21);
            w.Write(c.Crc);
            if (e.Zip64) { w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); } else { w.Write((uint)c.CSize); w.Write((uint)c.USize); }
            w.Write((ushort)c.Name.Length); w.Write((ushort)extra.Length); w.Write((ushort)ec.Length); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write((uint)c.Offset);
            w.Write(c.Name); w.Write(extra); w.Write(ec);
        }
        var cdSize = ms.Position - cdStart;
        var com = rawComment ?? Encoding.UTF8.GetBytes(comment);
        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)central.Count); w.Write((ushort)central.Count); w.Write((uint)cdSize); w.Write((uint)cdStart); w.Write((ushort)com.Length); w.Write(com);
        return ms.ToArray();
    }

    public static byte[] Simple(IEnumerable<(string Name, byte[] Data)> parts) => Write(parts.Select(p => new ZEntry { Name = p.Name, Data = p.Data }));
}


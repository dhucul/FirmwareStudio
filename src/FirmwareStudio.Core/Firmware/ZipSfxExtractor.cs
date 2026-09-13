using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
namespace FirmwareStudio.Core.Firmware;

/// <summary>ZIP-SFX reader with central-directory boundaries, exact sizes and CRC32 validation.</summary>
internal static class ZipSfxExtractor
{
    private const int MaxInnerExeBytes = 64 * 1024 * 1024;
    private const int MaxEntries = 256;
    private static readonly uint[] CrcTable = BuildCrcTable();
    public static bool LooksLikeWinRarSfx(byte[] data)
        => data.AsSpan().IndexOf("RarSFX"u8) >= 0 || data.AsSpan().IndexOf("WinRAR SFX"u8) >= 0;
    public static byte[]? ExtractInnerExe(byte[] data) => ExtractInnerExes(data).FirstOrDefault();
    public static IEnumerable<byte[]> ExtractInnerExes(byte[] data, CancellationToken ct = default)
    {
        foreach (var entry in ReadEntries(data))
        {
            ct.ThrowIfCancellationRequested();
            byte[]? inner = Inflate(data, entry, ct);
            if (inner is not null && PeResourceReader.IsPe(inner)) yield return inner;
        }
    }
    private sealed record Entry(int Start, int Compressed, int Uncompressed, ushort Method, uint Crc);
    private static List<Entry> ReadEntries(byte[] data)
    {
        var result = new List<Entry>();
        bool Fits(long p, long n) => p >= 0 && n >= 0 && p <= data.LongLength - n;
        ushort U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p, 2));
        uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
        int end = -1;
        for (int p = data.Length - 22; p >= Math.Max(0, data.Length - 65557); p--)
            if (U32(p) == 0x06054B50 && p + 22L + U16(p + 20) == data.LongLength) { end = p; break; }
        if (end < 0 || U16(end + 4) != 0 || U16(end + 6) != 0) return result;
        int count = U16(end + 10);
        if (count != U16(end + 8) || count > MaxEntries) return result;
        uint centralSize = U32(end + 12), centralOffset = U32(end + 16);
        long central = (long)end - centralSize, archiveBase = central - centralOffset;
        if (archiveBase < 0 || !Fits(central, centralSize)) return result;
        long position = central, totalInflated = 0;
        for (int i = 0; i < count; i++)
        {
            if (position > end - 46 || U32((int)position) != 0x02014B50) return [];
            int p = (int)position;
            ushort flags = U16(p + 8), method = U16(p + 10);
            uint crc = U32(p + 16), compressed = U32(p + 20), uncompressed = U32(p + 24);
            int nameLength = U16(p + 28), extraLength = U16(p + 30), commentLength = U16(p + 32);
            long next = position + 46L + nameLength + extraLength + commentLength;
            if (next > end || U16(p + 34) != 0) return [];
            string name = Encoding.UTF8.GetString(data, p + 46, nameLength);
            position = next;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if ((flags & 0x0041) != 0 || method is not (0 or 8) || uncompressed == 0 || uncompressed > MaxInnerExeBytes || compressed > int.MaxValue) continue;
            totalInflated += uncompressed;
            if (totalInflated > 2L * MaxInnerExeBytes) throw new InvalidDataException("SFX exceeds the total executable budget.");
            long local = archiveBase + U32(p + 42);
            if (!Fits(local, 30) || local >= central || U32((int)local) != 0x04034B50) return [];
            int l = (int)local;
            if (U16(l + 6) != flags || U16(l + 8) != method) return [];
            int localName = U16(l + 26), localExtra = U16(l + 28);
            long start = local + 30L + localName + localExtra;
            if (start > central || compressed > central - start || !Fits(local + 30, localName) ||
                !data.AsSpan(l + 30, localName).SequenceEqual(data.AsSpan(p + 46, nameLength))) return [];
            if ((flags & 8) == 0 && (U32(l + 14) != crc || U32(l + 18) != compressed || U32(l + 22) != uncompressed)) return [];
            if (method == 0 && compressed != uncompressed) return [];
            result.Add(new Entry((int)start, (int)compressed, (int)uncompressed, method, crc));
        }
        return position == end ? result : [];
    }
    private static byte[]? Inflate(byte[] data, Entry entry, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            byte[] output = new byte[entry.Uncompressed];
            if (entry.Method == 0) data.AsSpan(entry.Start, entry.Compressed).CopyTo(output);
            else
            {
                using var source = new MemoryStream(data, entry.Start, entry.Compressed, false);
                using var inflater = new DeflateStream(source, CompressionMode.Decompress);
                int offset = 0;
                while (offset < output.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = inflater.Read(output, offset, Math.Min(81920, output.Length - offset));
                    if (read == 0) return null;
                    offset += read;
                }
                if (inflater.ReadByte() != -1) return null;
            }
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < output.Length; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                crc = (crc >> 8) ^ CrcTable[(crc ^ output[i]) & 0xFF];
            }
            return (crc ^ 0xFFFFFFFF) == entry.Crc ? output : null;
        }
        catch (InvalidDataException) { return null; }
    }
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++) c = (c >> 1) ^ ((c & 1) != 0 ? 0xEDB88320u : 0);
            table[i] = c;
        }
        return table;
    }
}

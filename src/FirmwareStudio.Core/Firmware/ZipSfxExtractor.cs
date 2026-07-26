using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FirmwareStudio.Core.Firmware;

/// <summary>
/// Pulls the inner PE out of a WinRAR ZIP self-extractor. The Pioneer updater ships as a WinRAR SFX
/// (sfxzip module) whose payload is a standard ZIP appended after the stub. We scan for local file headers
/// (<c>PK\x03\x04</c>), find the entry named like an <c>.exe</c>, and inflate it (ZIP method 8 = raw DEFLATE,
/// decoded by the in-box <see cref="DeflateStream"/>; method 0 = stored). Read-only.
/// </summary>
internal static class ZipSfxExtractor
{
    private const int MaxInnerExeBytes = 64 * 1024 * 1024;

    /// <summary>Cheap smell test: does the file carry a WinRAR SFX marker? (Avoids inflating arbitrary PEs.)</summary>
    public static bool LooksLikeWinRarSfx(byte[] data)
        => IndexOfAscii(data, "RarSFX") >= 0 || IndexOfAscii(data, "WinRAR SFX") >= 0;

    public static byte[]? ExtractInnerExe(byte[] data)
    {
        if (data.Length < 30) return null;
        for (int p = 0; p <= data.Length - 30; p++)
        {
            if (data[p] != 0x50 || data[p + 1] != 0x4B || data[p + 2] != 0x03 || data[p + 3] != 0x04)
                continue; // not "PK\x03\x04"

            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 8));
            uint compSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 18));
            uint uncomp = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 22));
            ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 26));
            ushort extraLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 28));
            long nameEnd = (long)p + 30 + nameLen;
            if (nameEnd > data.Length) continue;

            string name = Encoding.ASCII.GetString(data, p + 30, nameLen);
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            long dataStartLong = nameEnd + extraLen;
            if (dataStartLong >= data.Length) continue;
            int dataStart = (int)dataStartLong;

            byte[]? inner = TryInflate(data, dataStart, method, compSize, uncomp);
            if (inner is not null && PeResourceReader.IsPe(inner))
                return inner;   // require a valid inner PE; otherwise keep scanning
        }
        return null;
    }

    private static byte[]? TryInflate(byte[] data, int start, ushort method, uint compSize, uint uncomp)
    {
        try
        {
            if (start < 0 || start > data.Length ||
                compSize > int.MaxValue || uncomp > MaxInnerExeBytes)
                return null;

            switch (method)
            {
                case 0: // stored
                {
                    uint advertised = uncomp != 0 ? uncomp : compSize;
                    if (advertised == 0 || advertised > MaxInnerExeBytes) return null;
                    int len = (int)advertised;
                    if (len > data.Length - start) return null;
                    return data.AsSpan(start, len).ToArray();
                }
                case 8: // raw DEFLATE — DeflateStream stops at the stream's logical end
                {
                    int srcLen = compSize != 0 ? (int)compSize : data.Length - start;
                    if (srcLen <= 0) return null;
                    if (srcLen > data.Length - start) srcLen = data.Length - start;
                    using var ms = new MemoryStream(data, start, srcLen, writable: false);
                    using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                    using var outp = new MemoryStream(uncomp > 0 ? (int)uncomp : 0);
                    var chunk = new byte[81920];
                    while (true)
                    {
                        int read = ds.Read(chunk, 0, chunk.Length);
                        if (read == 0) break;
                        if (outp.Length + read > MaxInnerExeBytes) return null;
                        outp.Write(chunk, 0, read);
                    }
                    return outp.ToArray();
                }
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static int IndexOfAscii(byte[] hay, string needleAscii)
    {
        byte[] n = Encoding.ASCII.GetBytes(needleAscii);
        int end = hay.Length - n.Length;
        for (int i = 0; i <= end; i++)
        {
            bool ok = true;
            for (int j = 0; j < n.Length; j++)
                if (hay[i + j] != n[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}

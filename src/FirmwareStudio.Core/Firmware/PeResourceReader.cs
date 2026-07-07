using System.Buffers.Binary;
using System.Text;

namespace FirmwareStudio.Core.Firmware;

/// <summary>One leaf resource extracted from a PE image.</summary>
internal sealed record PeResource(string Type, string Name, int Lang, byte[] Data);

/// <summary>
/// Minimal, dependency-free PE resource-directory walker (PE32 and PE32+). Pioneer firmware updaters store
/// their microcode images under a custom string resource type <c>"BINARY"</c> (numeric ids 131 = kernel,
/// 132 = normal). Read-only; tolerates malformed trees by returning whatever it could parse.
/// </summary>
internal static class PeResourceReader
{
    public static bool IsPe(byte[] d) => d.Length > 0x40 && d[0] == (byte)'M' && d[1] == (byte)'Z';

    public static List<PeResource> Read(byte[] pe)
    {
        var list = new List<PeResource>();
        if (!IsPe(pe)) return list;

        try
        {
            int lfanew = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
            if (lfanew <= 0 || lfanew + 24 > pe.Length) return list;
            if (BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(lfanew)) != 0x0000_4550) return list; // "PE\0\0"

            int coff = lfanew + 4;
            int numSec = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(coff + 2));
            int optSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(coff + 16));
            int opt = coff + 20;
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(opt));
            int ddOff = opt + (magic == 0x20B ? 112 : 96);              // DataDirectory offset (PE32+ vs PE32)
            uint rsrcRva = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(ddOff + 2 * 8)); // [2] = Resource Table
            if (rsrcRva == 0) return list;

            // Section table → RVA-to-file-offset mapping.
            int secTab = opt + optSize;
            var secs = new List<(uint va, uint vsz, uint rawPtr, uint rawSz)>();
            for (int i = 0; i < numSec; i++)
            {
                int s = secTab + i * 40;
                if (s + 40 > pe.Length) break;
                secs.Add((
                    BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(s + 12)),  // VirtualAddress
                    BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(s + 8)),   // VirtualSize
                    BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(s + 20)),  // PointerToRawData
                    BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(s + 16))));// SizeOfRawData
            }

            long RvaToOff(uint rva)
            {
                foreach (var (va, vsz, rawPtr, rawSz) in secs)
                    if (rva >= va && rva < va + Math.Max(vsz, rawSz))
                        return rawPtr + (rva - va);
                return -1;
            }

            long rsrcBase = RvaToOff(rsrcRva);
            if (rsrcBase < 0) return list;

            string ReadDirString(uint rel)
            {
                long o = rsrcBase + rel;
                if (o + 2 > pe.Length) return string.Empty;
                int wlen = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan((int)o));
                if (o + 2 + wlen * 2 > pe.Length) return string.Empty;
                return Encoding.Unicode.GetString(pe, (int)o + 2, wlen * 2);
            }

            // Three-level tree: Type → Name → Language(leaf).
            void Walk(uint dirRel, int level, string typeVal, string nameVal)
            {
                long o = rsrcBase + dirRel;
                if (o + 16 > pe.Length) return;
                int named = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan((int)o + 12));
                int idcnt = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan((int)o + 14));
                int entry = (int)o + 16;
                for (int i = 0; i < named + idcnt; i++)
                {
                    int e = entry + i * 8;
                    if (e + 8 > pe.Length) return;
                    uint nameField = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(e));
                    uint offField = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(e + 4));
                    string id = (nameField & 0x8000_0000) != 0
                        ? ReadDirString(nameField & 0x7FFF_FFFF)
                        : nameField.ToString();

                    if ((offField & 0x8000_0000) != 0)
                    {
                        uint sub = offField & 0x7FFF_FFFF;
                        if (level == 0) Walk(sub, 1, id, string.Empty);
                        else if (level == 1) Walk(sub, 2, typeVal, id);
                    }
                    else
                    {
                        long de = rsrcBase + offField;              // IMAGE_RESOURCE_DATA_ENTRY
                        if (de + 8 > pe.Length) continue;
                        uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan((int)de));
                        uint dataSz = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan((int)de + 4));
                        long foff = RvaToOff(dataRva);
                        if (foff < 0 || foff + dataSz > pe.Length) continue;
                        var data = pe.AsSpan((int)foff, (int)dataSz).ToArray();
                        int lang = int.TryParse(id, out int l) ? l : 0;
                        list.Add(new PeResource(typeVal, nameVal, lang, data));
                    }
                }
            }

            Walk(0, 0, string.Empty, string.Empty);
        }
        catch
        {
            // Tolerate malformed resource trees — return whatever was collected.
        }
        return list;
    }
}

using System.Buffers.Binary;
using System.Text;
namespace FirmwareStudio.Core.Firmware;

internal sealed record PeResource(string Type, string Name, int Lang, byte[] Data);

/// <summary>Bounded PE32/PE32+ resource traversal with checked raw-section boundaries.</summary>
internal static class PeResourceReader
{
    public static bool IsPe(byte[] data)
    {
        if (data.Length < 64 || data[0] != 'M' || data[1] != 'Z') return false;
        int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C));
        return offset >= 64 && offset <= data.Length - 24 &&
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) == 0x4550;
    }

    public static List<PeResource> Read(byte[] pe, CancellationToken ct = default,
        int maxResources = 1024, long maxTotalBytes = 64L * 1024 * 1024, int maxEntries = 8192)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<PeResource>();
        if (!IsPe(pe)) return result;
        bool Fits(long offset, long size) => offset >= 0 && size >= 0 && offset <= pe.LongLength - size;
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(offset, 2));
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(offset, 4));
        int header = (int)U32(0x3C);
        int sections = U16(header + 6), optionalSize = U16(header + 20), optional = header + 24;
        if (!Fits(optional, optionalSize) || optionalSize < 2) return result;
        int directoryOffset = U16(optional) switch { 0x10B => 96, 0x20B => 112, _ => -1 };
        if (directoryOffset < 0 || optionalSize < directoryOffset + 24 ||
            U32(optional + directoryOffset - 4) < 3) return result;
        uint resourceRva = U32(optional + directoryOffset + 16), resourceSize = U32(optional + directoryOffset + 20);
        if (resourceRva == 0 || resourceSize == 0) return result;
        int sectionTable = optional + optionalSize;
        if (sections > 96 || !Fits(sectionTable, sections * 40L)) throw new InvalidDataException("Invalid PE section table.");

        long Map(uint rva, uint size)
        {
            for (int i = 0; i < sections; i++)
            {
                int section = sectionTable + i * 40;
                uint va = U32(section + 12), rawSize = U32(section + 16), raw = U32(section + 20);
                long relative = (long)rva - va;
                if (relative >= 0 && relative <= (long)rawSize - size && Fits((long)raw + relative, size)) return raw + relative;
            }
            return -1;
        }
        long baseOffset = Map(resourceRva, resourceSize);
        if (baseOffset < 0) throw new InvalidDataException("PE resource directory is outside its raw section.");
        int Relative(uint offset, long size)
        {
            if (offset > (long)resourceSize - size || !Fits(baseOffset + offset, size)) throw new InvalidDataException("PE resource reference is outside its directory.");
            return checked((int)(baseOffset + offset));
        }
        string Name(uint value)
        {
            if ((value & 0x80000000) == 0) return value.ToString();
            uint relative = value & 0x7FFFFFFF;
            int position = Relative(relative, 2), length = U16(position);
            Relative(relative, 2L + 2L * length);
            return Encoding.Unicode.GetString(pe, position + 2, length * 2);
        }
        var visited = new HashSet<uint>();
        int entriesRead = 0;
        long bytesCopied = 0;
        void Walk(uint relative, int level, string type, string name)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(relative)) return;
            int directory = Relative(relative, 16), count = U16(directory + 12) + U16(directory + 14);
            if (count > maxEntries - entriesRead) throw new InvalidDataException("PE resource traversal exceeds the entry budget.");
            entriesRead += count;
            Relative(relative, 16L + count * 8L);
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                int entry = directory + 16 + 8 * i;
                string id = Name(U32(entry));
                uint child = U32(entry + 4);
                if ((child & 0x80000000) != 0)
                {
                    if (level >= 2) throw new InvalidDataException("PE resource tree is too deep.");
                    Walk(child & 0x7FFFFFFF, level + 1, level == 0 ? id : type, level == 1 ? id : name);
                    continue;
                }
                if (level != 2) throw new InvalidDataException("PE resource leaf has an invalid level.");
                int descriptor = Relative(child, 16);
                uint rva = U32(descriptor), length = U32(descriptor + 4);
                long offset = Map(rva, length);
                if (offset < 0) throw new InvalidDataException("PE resource payload is outside its raw section.");
                if (result.Count >= maxResources || length > maxTotalBytes - bytesCopied) throw new InvalidDataException("PE resource extraction exceeds the allocation budget.");
                bytesCopied += length;
                result.Add(new PeResource(type, name, int.TryParse(id, out int lang) ? lang : 0,
                    pe.AsSpan((int)offset, checked((int)length)).ToArray()));
            }
        }
        Walk(0, 0, "", "");
        return result;
    }
}

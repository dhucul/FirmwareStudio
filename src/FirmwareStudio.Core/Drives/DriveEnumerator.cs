using System.Text;
using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Drives;

/// <summary>
/// Lists optical drives via <c>GetLogicalDrives</c> + <c>GetDriveType==DRIVE_CDROM</c> (no handle, no
/// elevation), and resolves a friendly name via <c>IOCTL_STORAGE_QUERY_PROPERTY</c> (opens with zero
/// access, so it also works unelevated).
/// </summary>
public static class DriveEnumerator
{
    public static IReadOnlyList<OpticalDrive> Scan()
    {
        var drives = new List<OpticalDrive>();
        if (!OperatingSystem.IsWindows()) return drives;

        uint mask = Native.GetLogicalDrives();
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            char letter = (char)('A' + i);
            string root = $"{letter}:\\";
            if (Native.GetDriveTypeW(root) != Native.DRIVE_CDROM) continue;
            drives.Add(new OpticalDrive(letter, QueryFriendlyName(letter)));
        }
        return drives;
    }

    /// <summary>OS-reported bus type ("USB", "SATA", "ATAPI", …) for the drive, or "Unknown".</summary>
    public static string QueryBusType(char letter)
    {
        byte[]? desc = QueryStorageDescriptor(letter);
        if (desc is null || Native.SDD_BusType + 1 > desc.Length) return "Unknown";
        return BusTypeName(desc[Native.SDD_BusType]);
    }

    private static string? QueryFriendlyName(char letter)
    {
        byte[]? desc = QueryStorageDescriptor(letter);
        if (desc is null) return null;
        string? vendor = ReadOffsetString(desc, Native.SDD_VendorIdOffset);
        string? product = ReadOffsetString(desc, Native.SDD_ProductIdOffset);
        string name = string.Join(' ', new[] { vendor, product }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static byte[]? QueryStorageDescriptor(char letter)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string path = $@"\\.\{char.ToUpperInvariant(letter)}:";
        IntPtr h = Native.CreateFileW(path, 0,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == Native.INVALID_HANDLE_VALUE) return null;
        try
        {
            // STORAGE_PROPERTY_QUERY { StorageDeviceProperty=0, PropertyStandardQuery=0, AdditionalParameters[] }
            byte[] input = new byte[12];
            byte[] output = new byte[1024];
            bool ok = Native.DeviceIoControlBytes(h, Native.IOCTL_STORAGE_QUERY_PROPERTY,
                input, (uint)input.Length, output, (uint)output.Length, out _, IntPtr.Zero);
            return ok ? output : null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private static string? ReadOffsetString(byte[] buf, int offsetField)
    {
        if (offsetField + 4 > buf.Length) return null;
        int off = BitConverter.ToInt32(buf, offsetField);
        if (off <= 0 || off >= buf.Length) return null;
        int end = off;
        while (end < buf.Length && buf[end] != 0) end++;
        return Encoding.ASCII.GetString(buf, off, end - off).Trim();
    }

    private static string BusTypeName(byte busType) => busType switch
    {
        0x01 => "SCSI",
        0x02 => "ATAPI",
        0x03 => "ATA",
        0x04 => "1394",
        0x05 => "SSA",
        0x06 => "Fibre",
        0x07 => "USB",
        0x08 => "RAID",
        0x09 => "iSCSI",
        0x0A => "SAS",
        0x0B => "SATA",
        0x0C => "SD",
        0x0D => "MMC",
        0x11 => "NVMe",
        _ => "Unknown",
    };
}

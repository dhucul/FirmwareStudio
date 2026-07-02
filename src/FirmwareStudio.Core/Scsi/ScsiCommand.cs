namespace FirmwareStudio.Core.Scsi;

/// <summary>Transfer direction for a CDB, mapped to the SPTD <c>DataIn</c> field.</summary>
public enum ScsiDirection { In, Out, None }

/// <summary>
/// Builders for the CDBs this tool issues. All are read-only or no-data — the tool never writes to a
/// drive. Multi-byte fields in SCSI CDBs are big-endian.
/// </summary>
public static class ScsiCommand
{
    /// <summary>Standard INQUIRY (0x12), 96-byte allocation → vendor/model/firmware-revision.</summary>
    public static byte[] Inquiry(byte allocLength = 96)
        => new byte[] { 0x12, 0x00, 0x00, 0x00, allocLength, 0x00 };

    /// <summary>INQUIRY EVPD (0x12) for a Vital Product Data page (e.g. 0x80 = unit serial number).</summary>
    public static byte[] InquiryVpd(byte page, byte allocLength = 64)
        => new byte[] { 0x12, 0x01, page, 0x00, allocLength, 0x00 };

    /// <summary>MODE SENSE(6) (0x1A) for a mode page.</summary>
    public static byte[] ModeSense6(byte page, byte allocLength = 0xFF)
        => new byte[] { 0x1A, 0x00, page, 0x00, allocLength, 0x00 };

    /// <summary>READ BUFFER (0x3C) descriptor mode (0x03): returns 4 bytes = [0] offset boundary, [1..3] capacity (BE24).</summary>
    public static byte[] ReadBufferDescriptor(byte bufferId)
        => new byte[] { 0x3C, 0x03, bufferId, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00 };

    /// <summary>READ BUFFER (0x3C) data mode (0x02): read <paramref name="length"/> bytes at <paramref name="offset"/> from buffer <paramref name="bufferId"/>.</summary>
    public static byte[] ReadBufferData(byte bufferId, int offset, int length)
    {
        var cdb = new byte[10];
        cdb[0] = 0x3C;
        cdb[1] = 0x02;
        cdb[2] = bufferId;
        WriteBe24(cdb, 3, offset);   // buffer offset
        WriteBe24(cdb, 6, length);   // allocation length
        return cdb;
    }

    /// <summary>
    /// MediaTek vendor "read cache" (0xF1): reads the drive's internal DRAM cache, which typically holds
    /// the resident firmware image. CDB: [0]=0xF1 [1]=0x06 [2..5]=offset (BE32) [6..9]=size (BE32).
    /// Derived from redumper's <c>MEDIATEK_READ_CACHE</c>.
    /// </summary>
    public static byte[] MediaTekReadCache(uint offset, uint size)
    {
        var cdb = new byte[10];
        cdb[0] = 0xF1;
        cdb[1] = 0x06;
        WriteBe32(cdb, 2, offset);
        WriteBe32(cdb, 6, size);
        return cdb;
    }

    /// <summary>
    /// PLDS / Lite-On (Philips &amp; Lite-On Digital Solutions, a MediaTek OEM) vendor command 0xDF, as
    /// issued by the Plextor PX-891SAF firmware updater. 12-byte CDB: [0]=0xDF [1]=mode [2]=bufferId
    /// [3]=arg3 [4]=arg4, rest 0; data-in. The transfer length is the caller's buffer size — the CDB
    /// carries no length field. The updater used (mode=0, bufferId=0x0F, arg3=0x20/0x14/0x11, arg4=0/1)
    /// to read 12-byte drive-state registers; <b>mode 0 is the confirmed data-in direction</b>. Read-only.
    /// Other <paramref name="mode"/> values are vendor subcommands of unknown effect — callers that want to
    /// stay read-only must keep mode 0.
    /// </summary>
    public static byte[] PldsVendor(byte mode, byte bufferId, byte arg3 = 0x00, byte arg4 = 0x00)
        => new byte[] { 0xDF, mode, bufferId, arg3, arg4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    public static void WriteBe24(byte[] buf, int at, int value)
    {
        buf[at] = (byte)((value >> 16) & 0xFF);
        buf[at + 1] = (byte)((value >> 8) & 0xFF);
        buf[at + 2] = (byte)(value & 0xFF);
    }

    public static void WriteBe32(byte[] buf, int at, uint value)
    {
        buf[at] = (byte)((value >> 24) & 0xFF);
        buf[at + 1] = (byte)((value >> 16) & 0xFF);
        buf[at + 2] = (byte)((value >> 8) & 0xFF);
        buf[at + 3] = (byte)(value & 0xFF);
    }

    public static int ReadBe24(byte[] buf, int at)
        => (buf[at] << 16) | (buf[at + 1] << 8) | buf[at + 2];
}

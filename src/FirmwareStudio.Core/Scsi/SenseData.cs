namespace FirmwareStudio.Core.Scsi;

/// <summary>
/// Decoded SCSI sense: sense key + additional sense code (ASC/ASCQ). Handles both fixed (0x70/0x71)
/// and descriptor (0x72/0x73) formats. Optical drives almost always return fixed format.
/// </summary>
public readonly struct SenseData
{
    public byte Key { get; }
    public byte Asc { get; }
    public byte Ascq { get; }
    /// <summary>True when the sense buffer carried a recognised response code.</summary>
    public bool Present { get; }

    /// <summary>ILLEGAL REQUEST — the drive rejected the command (bad opcode or field).</summary>
    public bool IllegalRequest => Present && Key == 0x05;

    /// <summary>
    /// ILLEGAL REQUEST / INVALID COMMAND OPERATION CODE (asc=0x20): the opcode is genuinely
    /// not implemented by this firmware. This is the only "not supported" verdict.
    /// </summary>
    public bool OpcodeUnsupported => IllegalRequest && Asc == 0x20;

    /// <summary>
    /// ILLEGAL REQUEST / INVALID FIELD IN CDB (asc=0x24): the drive *recognised* the opcode
    /// and only objected to a field value (mode/bufferId/length/offset) — the command is live,
    /// so the right response is to retry with corrected fields, not to give up. Confirmed on the
    /// PLEXTOR PX-891SAF PLUS, whose 0xDF servo/quality sub-commands answer asc=24 when a required
    /// field (e.g. an LBA) is left zero, versus asc=20 for a truly-absent opcode.
    /// </summary>
    public bool FieldInvalid => IllegalRequest && Asc == 0x24;

    private SenseData(byte key, byte asc, byte ascq, bool present)
    {
        Key = key; Asc = asc; Ascq = ascq; Present = present;
    }

    public static SenseData Parse(byte[]? sense)
    {
        if (sense is null || sense.Length < 14) return default;
        byte rc = (byte)(sense[0] & 0x7F);
        bool present = rc is 0x70 or 0x71 or 0x72 or 0x73;
        if (!present) return default;
        if (rc is 0x72 or 0x73) // descriptor format
            return new SenseData((byte)(sense[1] & 0x0F), sense[2], sense[3], true);
        // fixed format
        return new SenseData((byte)(sense[2] & 0x0F), sense[12], sense[13], true);
    }

    /// <summary>Human text for the common sense keys and a few notable ASC/ASCQ pairs.</summary>
    public string Describe()
    {
        if (!Present) return "no sense";
        string key = Key switch
        {
            0x00 => "NO SENSE",
            0x01 => "RECOVERED ERROR",
            0x02 => "NOT READY",
            0x03 => "MEDIUM ERROR",
            0x04 => "HARDWARE ERROR",
            0x05 => "ILLEGAL REQUEST",
            0x06 => "UNIT ATTENTION",
            0x07 => "DATA PROTECT",
            0x0B => "ABORTED COMMAND",
            _ => $"KEY 0x{Key:X2}",
        };
        string? asc = (Asc, Ascq) switch
        {
            (0x20, 0x00) => "Invalid Command Operation Code",
            (0x24, 0x00) => "Invalid Field In CDB",
            (0x25, 0x00) => "Logical Unit Not Supported",
            (0x3A, 0x00) => "Medium Not Present",
            (0x28, 0x00) => "Not Ready To Ready Change / Medium May Have Changed",
            (0x04, 0x01) => "Becoming Ready",
            _ => null,
        };
        return asc is null
            ? $"{key} (asc={Asc:X2}/{Ascq:X2})"
            : $"{key} — {asc}";
    }
}

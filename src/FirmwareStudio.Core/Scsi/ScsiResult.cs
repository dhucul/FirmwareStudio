namespace FirmwareStudio.Core.Scsi;

/// <summary>Outcome of a single SCSI command: status, sense, and the transfer buffer (for data-in reads).</summary>
public sealed class ScsiResult
{
    public required byte[] Cdb { get; init; }
    public required ScsiDirection Direction { get; init; }
    public required int RequestedLength { get; init; }
    public required int TransferredLength { get; init; }
    public required byte ScsiStatus { get; init; }
    public required byte[] SenseBytes { get; init; }
    /// <summary>The data-in buffer for a read, or null for no-data / data-out commands.</summary>
    public byte[]? Data { get; init; }
    /// <summary>True if DeviceIoControl itself succeeded (the request reached the driver).</summary>
    public required bool DeviceIoOk { get; init; }
    public int Win32Error { get; init; }
    public string? Note { get; init; }

    public SenseData SenseInfo => SenseData.Parse(SenseBytes);

    /// <summary>SCSI status GOOD (0x00).</summary>
    public bool Good => DeviceIoOk && ScsiStatus == 0x00;

    /// <summary>
    /// The drive understood and accepted the command: GOOD, or CHECK CONDITION with sense key
    /// NO&#160;SENSE / RECOVERED (&lt;= 1). An illegal opcode returns key 0x05 and is NOT accepted.
    /// </summary>
    public bool Accepted => DeviceIoOk && (ScsiStatus == 0x00 || SenseInfo.Key <= 0x01);

    public string CdbHex => Convert.ToHexString(Cdb).Chunk2();

    public string StatusText => ScsiStatus switch
    {
        0x00 => "GOOD",
        0x02 => $"CHECK CONDITION sk={SenseInfo.Key:X2} {SenseInfo.Describe()}",
        0x08 => "BUSY",
        0x18 => "RESERVATION CONFLICT",
        _ => $"STATUS 0x{ScsiStatus:X2}",
    };
}

internal static class HexFormatExtensions
{
    /// <summary>Turns "3C0300" into "3C 03 00".</summary>
    public static string Chunk2(this string hex)
    {
        if (hex.Length <= 2) return hex;
        var sb = new System.Text.StringBuilder(hex.Length + hex.Length / 2);
        for (int i = 0; i < hex.Length; i += 2)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(hex, i, 2);
        }
        return sb.ToString();
    }
}

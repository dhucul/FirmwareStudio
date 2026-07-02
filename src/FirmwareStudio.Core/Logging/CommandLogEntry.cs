using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Logging;

/// <summary>One line of the command audit trail: the CDB, direction, byte counts, status, and sense.</summary>
public sealed record CommandLogEntry(
    DateTime TimestampUtc,
    string CdbHex,
    ScsiDirection Direction,
    int RequestedLength,
    int TransferredLength,
    byte ScsiStatus,
    byte SenseKey,
    byte Asc,
    byte Ascq,
    bool DeviceIoOk,
    int Win32Error,
    string StatusText,
    string? Note)
{
    /// <summary>Ready-to-render single line, e.g. <c>3C 03 00 … | IN req=4 got=4 | GOOD</c>.</summary>
    public string Text
    {
        get
        {
            string dir = Direction switch { ScsiDirection.In => "IN", ScsiDirection.Out => "OUT", _ => "--" };
            string body = DeviceIoOk
                ? $"{CdbHex} | {dir} req={RequestedLength} got={TransferredLength} | {StatusText}"
                : $"{CdbHex} | {dir} req={RequestedLength} | IOCTL FAILED win32={Win32Error}";
            return Note is null ? body : $"{body}   [{Note}]";
        }
    }
}

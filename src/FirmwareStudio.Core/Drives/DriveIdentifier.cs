using System.Text;
using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Drives;

/// <summary>Runs INQUIRY (+ VPD 0x80 serial) to build a <see cref="DriveIdentity"/>.</summary>
public static class DriveIdentifier
{
    public static DriveIdentity Identify(ScsiDevice dev, string busType)
    {
        var inq = dev.SendCommand(ScsiCommand.Inquiry(96), ScsiDirection.In, new byte[96], note: "INQUIRY");
        if (!inq.Good || inq.Data is null || inq.TransferredLength < 36)
            throw new InvalidOperationException(
                $"INQUIRY failed or returned too little data ({Math.Max(0, inq.TransferredLength)}/36 bytes): " +
                inq.StatusText);

        byte[] d = inq.Data;
        string vendor = Ascii(d, 8, 8);
        string model = Ascii(d, 16, 16);
        string fw = Ascii(d, 32, 4);
        return new DriveIdentity(dev.DriveLetter, vendor, model, fw, TryReadSerial(dev), busType);
    }

    private static string? TryReadSerial(ScsiDevice dev)
    {
        var vpd = dev.SendCommand(ScsiCommand.InquiryVpd(0x80, 64), ScsiDirection.In, new byte[64], note: "INQUIRY VPD 0x80 (serial)");
        if (!vpd.Good || vpd.Data is not { } d || vpd.TransferredLength < 4 || d[1] != 0x80) return null;
        int available = Math.Min(vpd.TransferredLength, d.Length);
        int len = (d[2] << 8) | d[3];
        if (len <= 0 || 4 + len > available) len = Math.Max(0, available - 4);
        string serial = Ascii(d, 4, len);
        return string.IsNullOrWhiteSpace(serial) ? null : serial;
    }

    /// <summary>Fixed-length ASCII field: strip non-printable bytes and trim padding.</summary>
    private static string Ascii(byte[] buf, int offset, int length)
    {
        if (offset >= buf.Length) return "";
        length = Math.Min(length, buf.Length - offset);
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            byte b = buf[offset + i];
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : ' ');
        }
        return sb.ToString().Trim();
    }
}

namespace FirmwareStudio.Core.Models;

/// <summary>The drive's SCSI identity from INQUIRY (+ VPD serial and OS-reported bus type).</summary>
public sealed record DriveIdentity(
    char DriveLetter,
    string Vendor,
    string Model,
    string FirmwareRevision,
    string? Serial,
    string BusType)
{
    public string DisplayName => $"{Vendor} {Model}".Trim();
}

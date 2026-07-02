namespace FirmwareStudio.Core.Models;

/// <summary>An optical drive as seen by the OS: its letter and (best-effort) friendly name.</summary>
public sealed record OpticalDrive(char Letter, string? FriendlyName)
{
    public string Root => $"{Letter}:\\";
    public string Display => string.IsNullOrWhiteSpace(FriendlyName) ? $"{Letter}:" : $"{Letter}:  {FriendlyName}";
}

using System.ComponentModel;

namespace FirmwareStudio.Core.Scsi;

/// <summary>Thrown when a drive handle cannot be opened for pass-through. Carries a user-actionable message.</summary>
public sealed class ScsiOpenException : Exception
{
    public char DriveLetter { get; }
    public int Win32Error { get; }

    public ScsiOpenException(char driveLetter, int win32Error)
        : base(BuildMessage(driveLetter, win32Error))
    {
        DriveLetter = driveLetter;
        Win32Error = win32Error;
    }

    private static string BuildMessage(char letter, int err)
    {
        string detail = err switch
        {
            5 => "Access denied. Run FirmwareStudio as administrator, and close any app using the drive "
                 + "(Explorer AutoPlay, a media player, disc-burning software).",
            2 or 3 => "The drive was not found. It may have been removed.",
            32 => "The drive is in use by another process. Close anything accessing it and retry.",
            _ => new Win32Exception(err).Message,
        };
        return $"Could not open drive {char.ToUpperInvariant(letter)}: for SCSI pass-through (error {err}). {detail}";
    }
}

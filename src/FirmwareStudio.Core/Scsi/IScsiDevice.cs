namespace FirmwareStudio.Core.Scsi;

public interface IScsiDevice
{
    char DriveLetter { get; }
    ScsiResult SendCommand(byte[] cdb, ScsiDirection dir, byte[]? data,
        int timeoutSec = 15, string? note = null);
}

internal static class ScsiCancellation
{
    internal static IScsiDevice WithCancellation(this IScsiDevice device, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new CancellableDevice(device, ct);
    }

    private sealed class CancellableDevice(IScsiDevice device, CancellationToken ct) : IScsiDevice
    {
        public char DriveLetter => device.DriveLetter;
        public ScsiResult SendCommand(byte[] cdb, ScsiDirection dir, byte[]? data,
            int timeoutSec = 15, string? note = null)
        {
            ct.ThrowIfCancellationRequested();
            var result = device.SendCommand(cdb, dir, data, timeoutSec, note);
            ct.ThrowIfCancellationRequested();
            return result;
        }
    }
}

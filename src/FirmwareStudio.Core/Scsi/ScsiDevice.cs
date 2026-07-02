using System.Runtime.InteropServices;
using FirmwareStudio.Core.Logging;

namespace FirmwareStudio.Core.Scsi;

/// <summary>
/// A handle to one optical drive, opened for SCSI pass-through. All commands go through
/// <see cref="SendCommand"/>, which marshals the SPTD block, issues the IOCTL, decodes status/sense,
/// and reports the command to the logger. The instance is not thread-safe; use one drive per worker.
/// </summary>
public sealed class ScsiDevice : IDisposable
{
    private readonly IntPtr _handle;
    private readonly IScsiLogger _logger;
    private bool _disposed;

    public char DriveLetter { get; }

    private ScsiDevice(IntPtr handle, char driveLetter, IScsiLogger logger)
    {
        _handle = handle;
        DriveLetter = driveLetter;
        _logger = logger;
    }

    /// <summary>Open <c>\\.\X:</c> for pass-through. Requires administrator rights.</summary>
    public static ScsiDevice Open(char driveLetter, IScsiLogger logger)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("SCSI pass-through is Windows-only.");

        string path = $@"\\.\{char.ToUpperInvariant(driveLetter)}:";
        IntPtr h = Native.CreateFileW(path,
            Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);

        if (h == Native.INVALID_HANDLE_VALUE)
            throw new ScsiOpenException(driveLetter, Marshal.GetLastWin32Error());

        logger.Info($"Opened drive {char.ToUpperInvariant(driveLetter)}: for pass-through.");
        return new ScsiDevice(h, driveLetter, logger);
    }

    /// <summary>
    /// Issue one CDB. <paramref name="data"/> is the transfer buffer (data-in reads land here; pass a
    /// zeroed buffer of the expected length). Returns the decoded result; never throws on SCSI errors —
    /// inspect <see cref="ScsiResult.Good"/> / <see cref="ScsiResult.Accepted"/>.
    /// </summary>
    public ScsiResult SendCommand(byte[] cdb, ScsiDirection dir, byte[]? data,
        int timeoutSec = 15, string? note = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cdb.Length is 0 or > 16)
            throw new ArgumentException("CDB length must be 1..16 bytes.", nameof(cdb));

        byte[] dataBuf = data ?? Array.Empty<byte>();

        var block = new Native.SCSI_PASS_THROUGH_DIRECT_WITH_SENSE
        {
            Sptd = new Native.SCSI_PASS_THROUGH_DIRECT
            {
                Length = (ushort)Marshal.SizeOf<Native.SCSI_PASS_THROUGH_DIRECT>(),
                CdbLength = (byte)cdb.Length,
                SenseInfoLength = 32,
                DataIn = DirByte(dir),
                DataTransferLength = (uint)dataBuf.Length,
                TimeOutValue = (uint)timeoutSec,
                SenseInfoOffset = (uint)Marshal.OffsetOf<Native.SCSI_PASS_THROUGH_DIRECT_WITH_SENSE>(
                    nameof(Native.SCSI_PASS_THROUGH_DIRECT_WITH_SENSE.Sense)),
                Cdb = new byte[16],
                DataBuffer = IntPtr.Zero,
            },
            Sense = new byte[32],
        };
        Array.Copy(cdb, block.Sptd.Cdb, cdb.Length);

        GCHandle dataGch = default;
        IntPtr p = IntPtr.Zero;
        bool ioOk;
        int win32 = 0;
        byte status;
        byte[] sense;
        uint transferred;

        try
        {
            if (dataBuf.Length > 0)
            {
                dataGch = GCHandle.Alloc(dataBuf, GCHandleType.Pinned);
                block.Sptd.DataBuffer = dataGch.AddrOfPinnedObject();
            }

            int size = Marshal.SizeOf<Native.SCSI_PASS_THROUGH_DIRECT_WITH_SENSE>();
            p = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(block, p, false);

            ioOk = Native.DeviceIoControl(_handle, Native.IOCTL_SCSI_PASS_THROUGH_DIRECT,
                p, (uint)size, p, (uint)size, out _, IntPtr.Zero);
            if (!ioOk) win32 = Marshal.GetLastWin32Error();

            var outBlock = Marshal.PtrToStructure<Native.SCSI_PASS_THROUGH_DIRECT_WITH_SENSE>(p);
            status = outBlock.Sptd.ScsiStatus;
            sense = outBlock.Sense ?? new byte[32];
            transferred = outBlock.Sptd.DataTransferLength;
        }
        finally
        {
            if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            if (dataGch.IsAllocated) dataGch.Free();
        }

        var result = new ScsiResult
        {
            Cdb = (byte[])cdb.Clone(),
            Direction = dir,
            RequestedLength = dataBuf.Length,
            TransferredLength = (int)transferred,
            ScsiStatus = status,
            SenseBytes = sense,
            Data = dataBuf.Length > 0 ? dataBuf : null,
            DeviceIoOk = ioOk,
            Win32Error = win32,
            Note = note,
        };

        var s = result.SenseInfo;
        _logger.OnCommand(new CommandLogEntry(
            DateTime.UtcNow, result.CdbHex, dir, result.RequestedLength, result.TransferredLength,
            status, s.Key, s.Asc, s.Ascq, ioOk, win32, result.StatusText, note));

        return result;
    }

    private static byte DirByte(ScsiDirection d) => d switch
    {
        ScsiDirection.In => Native.SCSI_IOCTL_DATA_IN,
        ScsiDirection.Out => Native.SCSI_IOCTL_DATA_OUT,
        _ => Native.SCSI_IOCTL_DATA_UNSPECIFIED,
    };

    /// <summary>
    /// Verify the SPTD marshalled layout matches the OS ABI before any result is trusted:
    /// 56-byte struct, sense at offset 56. Returns null if OK, else a description of the mismatch.
    /// </summary>
    public static string? VerifyLayout()
    {
        int size = Marshal.SizeOf<Native.SCSI_PASS_THROUGH_DIRECT>();
        int senseOff = (int)Marshal.OffsetOf<Native.SCSI_PASS_THROUGH_DIRECT_WITH_SENSE>(
            nameof(Native.SCSI_PASS_THROUGH_DIRECT_WITH_SENSE.Sense));
        if (size != 56) return $"SCSI_PASS_THROUGH_DIRECT is {size} bytes, expected 56 (x64 build required).";
        if (senseOff != 56) return $"Sense offset is {senseOff}, expected 56.";
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != Native.INVALID_HANDLE_VALUE && _handle != IntPtr.Zero)
            Native.CloseHandle(_handle);
    }
}

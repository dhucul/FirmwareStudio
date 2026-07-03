using System.Runtime.InteropServices;

namespace FirmwareStudio.Core.Scsi;

/// <summary>
/// Win32 SCSI pass-through (SPTI) interop. The host is x64, so <see cref="SCSI_PASS_THROUGH_DIRECT"/>
/// uses a 64-bit <c>DataBuffer</c> pointer and marshals to exactly 56 bytes. Every call site is guarded
/// by <see cref="OperatingSystem.IsWindows"/>. Templated on DisasmStudio.Debug/Native.cs.
/// </summary>
internal static class Native
{
    // ---- access / share / disposition ----
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    public const uint OPEN_EXISTING = 3;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // ---- drive type (GetDriveType) ----
    public const uint DRIVE_CDROM = 5;

    // ---- IOCTLs ----
    // CTL_CODE(FILE_DEVICE_CONTROLLER=0x0004, 0x0405, METHOD_BUFFERED=0, FILE_READ_ACCESS|FILE_WRITE_ACCESS=3)
    public const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;
    // CTL_CODE(FILE_DEVICE_MASS_STORAGE=0x002D, 0x0500, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    // CTL_CODE(IOCTL_SCSI_BASE=0x0004, 0x040c, METHOD_BUFFERED=0, FILE_READ_ACCESS|FILE_WRITE_ACCESS=3)
    public const uint IOCTL_ATA_PASS_THROUGH_DIRECT = 0x0004D030;

    // ---- ATA_PASS_THROUGH_DIRECT.AtaFlags ----
    public const ushort ATA_FLAGS_DRDY_REQUIRED = 0x01;
    public const ushort ATA_FLAGS_DATA_IN = 0x02;
    public const ushort ATA_FLAGS_DATA_OUT = 0x04;

    // ---- SCSI_PASS_THROUGH_DIRECT.DataIn ----
    public const byte SCSI_IOCTL_DATA_OUT = 0;
    public const byte SCSI_IOCTL_DATA_IN = 1;
    public const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;

    /// <summary>
    /// Mirrors the Win32 <c>SCSI_PASS_THROUGH_DIRECT</c>. Natural x64 layout: <c>Marshal.SizeOf == 56</c>
    /// (3 bytes pad before <c>DataTransferLength</c>, 4 before <c>DataBuffer</c>, trailing pad to 56).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SCSI_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Cdb;
    }

    /// <summary>
    /// The struct plus its sense buffer in one contiguous allocation, so <c>SenseInfoOffset</c> can point
    /// past the struct. The sense array lands at offset 56 (already 8-aligned); total size 88.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SCSI_PASS_THROUGH_DIRECT_WITH_SENSE
    {
        public SCSI_PASS_THROUGH_DIRECT Sptd;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Sense;
    }

    /// <summary>
    /// Mirrors the Win32 <c>ATA_PASS_THROUGH_DIRECT</c> (ntddscsi.h). x64 natural layout is 48 bytes
    /// (4-byte pad before the 8-byte <c>DataBuffer</c> pointer). <c>CurrentTaskFile</c> is the 8-byte ATA
    /// task file: [0]=Features/Error [1]=SectorCount [2]=LBALow [3]=LBAMid [4]=LBAHigh [5]=Device
    /// [6]=Command/Status [7]=reserved.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ATA_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public ushort AtaFlags;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte ReservedAsUchar;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public uint ReservedAsUlong;
        public IntPtr DataBuffer;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] PreviousTaskFile;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] CurrentTaskFile;
    }

    // ---- STORAGE_QUERY_PROPERTY (friendly name + bus type; does not require elevation) ----
    // STORAGE_DEVICE_DESCRIPTOR field byte offsets (parsed from the raw out buffer):
    public const int SDD_VendorIdOffset = 12;
    public const int SDD_ProductIdOffset = 16;
    public const int SDD_ProductRevisionOffset = 20;
    public const int SDD_SerialNumberOffset = 24;
    public const int SDD_BusType = 28;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetDriveTypeW(string lpRootPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    public static extern bool DeviceIoControlBytes(IntPtr hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}

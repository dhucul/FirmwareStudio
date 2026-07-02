using System.Reflection;
using System.Runtime.InteropServices;

namespace FirmwareStudio.Core.Hardware;

/// <summary>
/// P/Invoke to the WCH CH341 vendor DLL. The 64-bit DLL is <c>CH341DLLA64.DLL</c> (32-bit is
/// <c>CH341DLL.DLL</c>); a resolver tries both so it works with whichever the driver installs. These are the
/// functions used to drive an SPI flash: open, set SPI mode, full-duplex SPI transfer, close.
/// </summary>
internal static class Ch341Native
{
    // Logical name resolved by the import resolver below to the real DLL.
    private const string Lib = "CH341";
    private static int _resolverSet;

    /// <summary>Register the DLL resolver once, before the first CH341 call.</summary>
    internal static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 0)
            NativeLibrary.SetDllImportResolver(typeof(Ch341Native).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == Lib)
        {
            foreach (var name in new[] { "CH341DLLA64.DLL", "CH341DLL.DLL" })
                if (NativeLibrary.TryLoad(name, assembly, searchPath, out IntPtr handle))
                    return handle;
        }
        return IntPtr.Zero; // let the default resolver handle everything else (kernel32, …)
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern IntPtr CH341OpenDevice(uint iIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern void CH341CloseDevice(uint iIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern uint CH341GetVersion();

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CH341SetStream(uint iIndex, uint iMode);

    // Full-duplex SPI: ioBuffer holds the bytes to send and, on return, the bytes received (same length).
    // iChipSelect bit7 = enable, bits2..0 select the CS pin (0 = D0).
    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CH341StreamSPI4(uint iIndex, uint iChipSelect, uint iLength, [In, Out] byte[] ioBuffer);
}

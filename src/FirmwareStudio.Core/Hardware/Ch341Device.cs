namespace FirmwareStudio.Core.Hardware;

/// <summary>
/// A CH341A USB adapter opened in SPI mode. Full-duplex SPI transactions go through <see cref="SpiTransfer"/>,
/// each of which frames one chip-select cycle (CS on D0, active low). Not thread-safe.
/// </summary>
public sealed class Ch341Device : IDisposable
{
    private static readonly IntPtr InvalidHandle = new(-1);

    // CH341SetStream mode 0x81 = SPI MSB-first, single I/O (D3 clock / D5 MOSI / D7 MISO).
    private const uint SpiModeMsbSingle = 0x81;
    // CH341StreamSPI4 chip-select: bit7 enable + CS on D0.
    private const uint ChipSelectD0 = 0x80;

    private readonly uint _index;
    private bool _open;

    public uint Index => _index;

    private Ch341Device(uint index)
    {
        _index = index;
        _open = true;
    }

    /// <summary>Open the CH341 adapter at <paramref name="index"/> (0 for the first) and switch it to SPI mode.</summary>
    public static Ch341Device Open(uint index = 0)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("CH341 access is Windows-only.");

        Ch341Native.EnsureResolver();

        IntPtr handle;
        try
        {
            handle = Ch341Native.CH341OpenDevice(index);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new Ch341Exception(
                "CH341 driver/DLL not found. Install the WCH CH341PAR driver (it provides CH341DLLA64.dll), " +
                "or place a compatible 64-bit CH341 DLL next to FirmwareStudio.exe. " +
                $"Native loader error: {ex.Message}", ex);
        }

        if (handle == InvalidHandle || handle == IntPtr.Zero)
            throw new Ch341Exception(
                $"No CH341 adapter found at index {index}. Plug in the CH341A programmer and confirm the driver " +
                "is installed (check Device Manager).");

        var dev = new Ch341Device(index);
        try
        {
            dev.SetSpiMode();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            dev.Dispose();
            throw new Ch341Exception(
                $"The CH341 native library is incompatible or missing a required SPI function: {ex.Message}", ex);
        }
        catch
        {
            dev.Dispose();
            throw;
        }
        return dev;
    }

    /// <summary>CH341 DLL version (0 if unavailable). Handy for a connection sanity check.</summary>
    public uint DllVersion
    {
        get { try { return Ch341Native.CH341GetVersion(); } catch { return 0; } }
    }

    private void SetSpiMode()
    {
        if (!Ch341Native.CH341SetStream(_index, SpiModeMsbSingle))
            throw new Ch341Exception("Failed to switch the CH341 to SPI mode (CH341SetStream).");
    }

    /// <summary>
    /// One full-duplex SPI transaction: sends <paramref name="data"/> and returns the bytes clocked in on
    /// MISO (same length). For a flash read, put the command + address at the front and pad with zeros for
    /// the bytes to be read back.
    /// </summary>
    public byte[] SpiTransfer(byte[] data)
    {
        ObjectDisposedException.ThrowIf(!_open, this);
        if (data.Length is 0 or > 0x10000)
            throw new ArgumentException("SPI transfer length must be 1..65536 bytes.", nameof(data));

        var buffer = (byte[])data.Clone();
        if (!Ch341Native.CH341StreamSPI4(_index, ChipSelectD0, (uint)buffer.Length, buffer))
            throw new Ch341Exception(
                "SPI transfer failed (CH341StreamSPI4). Check the test-clip contact, chip orientation, and that " +
                "the flash is powered at the correct voltage.");
        return buffer;
    }

    public void Dispose()
    {
        if (!_open) return;
        _open = false;
        try { Ch341Native.CH341CloseDevice(_index); } catch { /* ignore close errors */ }
    }
}

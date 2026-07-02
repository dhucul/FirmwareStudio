namespace FirmwareStudio.Core.Hardware;

/// <summary>
/// Standard SPI NOR flash operations over a <see cref="Ch341Device"/>: read the JEDEC ID (0x9F) to identify
/// the chip, and read the whole chip with READ (0x03). Read-only — no erase or program commands exist here.
/// </summary>
public static class SpiNorFlash
{
    private const int ReadChunk = 4096;
    private const long MaxThreeByteAddress = 16L * 1024 * 1024; // 24-bit addressing ceiling

    // JEDEC manufacturer IDs (first RDID byte). Enough to name the common optical-drive flashes.
    private static readonly Dictionary<byte, string> Manufacturers = new()
    {
        [0xEF] = "Winbond",
        [0xC2] = "Macronix",
        [0x20] = "Micron/ST",
        [0x1F] = "Atmel/Adesto",
        [0xBF] = "SST/Microchip",
        [0x9D] = "ISSI",
        [0x01] = "Spansion/Cypress",
        [0xC8] = "GigaDevice",
        [0x8C] = "ESMT",
        [0x0B] = "XTX",
        [0x68] = "Boya",
        [0x5E] = "Zbit",
        [0xA1] = "Fudan",
    };

    /// <summary>Read RDID (0x9F). The capacity byte is log2(size) for most 25-series NOR chips.</summary>
    public static SpiFlashChip ReadId(Ch341Device dev, Action<string>? log = null)
    {
        byte[] resp = dev.SpiTransfer(new byte[] { 0x9F, 0x00, 0x00, 0x00 });
        byte man = resp[1], type = resp[2], cap = resp[3];
        log?.Invoke($"RDID (9F): {man:X2} {type:X2} {cap:X2}");

        long size = cap is >= 0x10 and <= 0x1F ? 1L << cap : 0;
        string maker = Manufacturers.TryGetValue(man, out string? m) ? m : $"Unknown(0x{man:X2})";
        string name = $"{maker} [{man:X2} {type:X2} {cap:X2}]";
        return new SpiFlashChip(name, man, type, cap, size, new[] { man, type, cap }, GuessVoltage(man, type));
    }

    /// <summary>
    /// Infer supply voltage from (manufacturer, memory-type) for the common families. Several makers encode
    /// the voltage variant in the 2nd RDID byte (e.g. Winbond W25Q..W = 1.8 V; Macronix MX25U = 1.8 V). This
    /// is a hint only — the definitive source is the printing on the chip.
    /// </summary>
    public static FlashVoltage GuessVoltage(byte man, byte type) => man switch
    {
        0xEF => type switch { 0x60 or 0x80 => FlashVoltage.OneEightVolt,       // Winbond W25Q..W / JW
                              0x30 or 0x40 or 0x70 => FlashVoltage.ThreeVolt,  // Winbond W25X / W25Q..V
                              _ => FlashVoltage.Unknown },
        0xC2 => type switch { 0x25 => FlashVoltage.OneEightVolt,               // Macronix MX25U
                              0x28 => FlashVoltage.Wide,                       // Macronix MX25R (1.65–3.6 V)
                              0x20 or 0x23 or 0x26 => FlashVoltage.ThreeVolt,  // Macronix MX25L
                              _ => FlashVoltage.Unknown },
        0x20 => type switch { 0xBB => FlashVoltage.OneEightVolt,               // Micron N25Q/MT25Q 1.8 V
                              0xBA => FlashVoltage.ThreeVolt,                  // Micron N25Q/MT25Q 3.3 V
                              _ => FlashVoltage.Unknown },
        0xC8 => type switch { 0x60 or 0x65 => FlashVoltage.OneEightVolt,       // GigaDevice GD25LQ/WQ
                              0x40 => FlashVoltage.ThreeVolt,                  // GigaDevice GD25Q
                              _ => FlashVoltage.Unknown },
        0x9D => type switch { 0x60 => FlashVoltage.OneEightVolt,               // ISSI IS25WP
                              0x40 => FlashVoltage.ThreeVolt,                  // ISSI IS25LP
                              _ => FlashVoltage.Unknown },
        _ => FlashVoltage.Unknown,
    };

    /// <summary>Read the entire chip via READ (0x03) in 4 KB blocks, reporting progress.</summary>
    public static byte[] ReadAll(Ch341Device dev, SpiFlashChip chip,
        IProgress<int>? progress, Action<string>? log, CancellationToken ct)
    {
        if (!chip.SizeKnown)
            throw new InvalidOperationException(
                "Flash capacity could not be determined from the JEDEC ID, so the read size is unknown.");
        if (chip.SizeBytes > MaxThreeByteAddress)
            throw new NotSupportedException(
                "Flash larger than 16 MB needs 4-byte addressing, which is not implemented (optical-drive flashes are 1–4 MB).");

        long size = chip.SizeBytes;
        var outData = new byte[size];
        log?.Invoke($"Reading {size:N0} bytes via READ (03h) in {ReadChunk}-byte blocks…");

        for (long addr = 0; addr < size; addr += ReadChunk)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(ReadChunk, size - addr);

            var cmd = new byte[4 + n];
            cmd[0] = 0x03;
            cmd[1] = (byte)((addr >> 16) & 0xFF);
            cmd[2] = (byte)((addr >> 8) & 0xFF);
            cmd[3] = (byte)(addr & 0xFF);

            byte[] resp = dev.SpiTransfer(cmd);
            Array.Copy(resp, 4, outData, addr, n);
            progress?.Report((int)(100L * (addr + n) / size));
        }

        log?.Invoke("Read complete.");
        return outData;
    }
}

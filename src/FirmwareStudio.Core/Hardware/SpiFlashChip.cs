namespace FirmwareStudio.Core.Hardware;

/// <summary>
/// Best-effort supply voltage inferred from the JEDEC memory-type byte. RDID does not carry the full part
/// number, so this is a hint (accurate for common makers), not a guarantee — always check the chip marking.
/// </summary>
public enum FlashVoltage { Unknown, ThreeVolt, OneEightVolt, Wide }

/// <summary>An SPI NOR flash identified by its JEDEC ID (manufacturer / memory type / capacity).</summary>
public sealed record SpiFlashChip(
    string Name,
    byte ManufacturerId,
    byte MemoryType,
    byte CapacityCode,
    long SizeBytes,
    byte[] RawId,
    FlashVoltage Voltage)
{
    public string IdHex => $"{ManufacturerId:X2} {MemoryType:X2} {CapacityCode:X2}";

    public string VoltageText => Voltage switch
    {
        FlashVoltage.ThreeVolt => "3.3 V (likely)",
        FlashVoltage.OneEightVolt => "1.8 V (likely) — needs a 1.8 V adapter",
        FlashVoltage.Wide => "1.8–3.3 V wide range",
        _ => "unknown — check the chip marking",
    };

    public bool IsLikely1V8 => Voltage == FlashVoltage.OneEightVolt;

    /// <summary>True when the capacity byte mapped to a known size (most 25-series NOR chips).</summary>
    public bool SizeKnown => SizeBytes > 0;

    public string SizeText => SizeBytes switch
    {
        >= 1024 * 1024 => $"{SizeBytes / (1024 * 1024)} MB",
        >= 1024 => $"{SizeBytes / 1024} KB",
        > 0 => $"{SizeBytes} bytes",
        _ => "unknown",
    };

    /// <summary>True if all three ID bytes are 0x00 or 0xFF — usually no chip / bad clip contact.</summary>
    public bool LooksEmpty =>
        (ManufacturerId is 0x00 or 0xFF) && (MemoryType is 0x00 or 0xFF) && (CapacityCode is 0x00 or 0xFF);
}

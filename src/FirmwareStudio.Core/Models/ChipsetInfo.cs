namespace FirmwareStudio.Core.Models;

public enum ChipsetFamily
{
    Unknown,
    MediaTek,   // LiteOn/PLDS, ASUS, LG/HLDS, Samsung/TSSTcorp, many rebrands — the common modern chipset
    Renesas,    // ex-NEC; Sony Optiarc
    Panasonic,  // Matsushita
    Pioneer,    // custom
    Other,
}

/// <summary>Detected controller family with a confidence and the evidence that produced it.</summary>
public sealed record ChipsetInfo(
    ChipsetFamily Family,
    string Name,
    int ConfidencePercent,
    IReadOnlyList<string> Evidence);

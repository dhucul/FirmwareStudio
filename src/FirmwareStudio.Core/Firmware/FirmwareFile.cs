using FirmwareStudio.Core.Analysis;

namespace FirmwareStudio.Core.Firmware;

/// <summary>What kind of firmware artefact a file on disk is.</summary>
public enum FirmwareFileKind
{
    /// <summary>A PLDS/Lite-On <c>.1KN</c>/<c>.1JN</c> flash update image (parse with <see cref="FirmwareImage"/>).</summary>
    VpdImage,
    /// <summary>A MediaTek/PLDS <c>0xF1</c> controller-RAM image (parse with <see cref="OpticalRamImage"/>).</summary>
    ControllerRam,
    /// <summary>A Pioneer firmware updater (.exe / Updater.exe / raw microcode .bin — parse with <see cref="PioneerFirmwareImage"/>).</summary>
    PioneerUpdate,
}

/// <summary>A firmware file routed to exactly one structured parser.</summary>
public sealed record FirmwareFileAnalysis(
    FirmwareFileKind Kind,
    PioneerUpdateInfo? Pioneer = null,
    OpticalRamImageInfo? ControllerRam = null,
    FirmwareImageInfo? Vpd = null);

/// <summary>
/// One decision point for "what is this firmware file?" so both the WPF app and the Smoke tool route a
/// dropped/opened file to the right structured parser instead of always assuming a <c>.1KN</c> wrapper.
/// A controller-RAM image (the <c>0xF1</c> cache dump, <c>FF 54 54 45</c> header) is detected first;
/// everything else falls through to the VPD flash-image parser.
/// </summary>
public static class FirmwareFile
{
    public static FirmwareFileKind Identify(byte[] data)
        => Analyze(data).Kind;

    /// <summary>
    /// Detect and parse a file in one pass. This avoids rescanning PE resources or inflating an SFX once
    /// for identification and again for the actual analysis.
    /// </summary>
    public static FirmwareFileAnalysis Analyze(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (OpticalRamImage.Looks(data))
            return new FirmwareFileAnalysis(
                FirmwareFileKind.ControllerRam,
                ControllerRam: OpticalRamImage.Parse(data));

        var pioneer = PioneerFirmwareImage.Parse(data);
        if (pioneer.Parts.Count > 0)
            return new FirmwareFileAnalysis(FirmwareFileKind.PioneerUpdate, Pioneer: pioneer);

        return new FirmwareFileAnalysis(
            FirmwareFileKind.VpdImage,
            Vpd: FirmwareImage.Parse(data));
    }
}

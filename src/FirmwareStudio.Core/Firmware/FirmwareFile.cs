using FirmwareStudio.Core.Analysis;

namespace FirmwareStudio.Core.Firmware;

/// <summary>What kind of firmware artefact a file on disk is.</summary>
public enum FirmwareFileKind
{
    /// <summary>A PLDS/Lite-On <c>.1KN</c>/<c>.1JN</c> flash update image (parse with <see cref="FirmwareImage"/>).</summary>
    VpdImage,
    /// <summary>A MediaTek/PLDS <c>0xF1</c> controller-RAM image (parse with <see cref="OpticalRamImage"/>).</summary>
    ControllerRam,
}

/// <summary>
/// One decision point for "what is this firmware file?" so both the WPF app and the Smoke tool route a
/// dropped/opened file to the right structured parser instead of always assuming a <c>.1KN</c> wrapper.
/// A controller-RAM image (the <c>0xF1</c> cache dump, <c>FF 54 54 45</c> header) is detected first;
/// everything else falls through to the VPD flash-image parser.
/// </summary>
public static class FirmwareFile
{
    public static FirmwareFileKind Identify(byte[] data)
        => OpticalRamImage.Looks(data) ? FirmwareFileKind.ControllerRam : FirmwareFileKind.VpdImage;
}

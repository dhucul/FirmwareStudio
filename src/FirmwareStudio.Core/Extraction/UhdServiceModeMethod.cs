using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Extraction;

/// <summary>
/// Method 3 — UHD Blu-ray service-mode read (LibreDrive/MakeMKV-style). Entering "svc" mode and reading
/// firmware is specific to a small set of supported 4K UHD drive models and firmware revisions. v1 ships
/// this as detect-only: the seam and model gate are here, but the entry sequence is not implemented.
/// </summary>
public sealed class UhdServiceModeMethod : IFirmwareExtractionMethod
{
    // Vendor/model fragments seen on UHD-friendly drives (indicative only, not a support guarantee).
    private static readonly string[] UhdHints = { "BW-16D1HT", "BU40N", "BU30N", "WH16NS", "UD2Q", "UD4Q", "BDR-", "UHD" };

    public string Id => "uhd-svc";
    public string DisplayName => "UHD Blu-ray service mode (experimental)";
    public string Description =>
        "Reads firmware from 4K UHD Blu-ray drives via their service (\"svc\") mode, as used by LibreDrive/" +
        "MakeMKV. Works only on specific supported models and firmware. Not implemented in v1 — detection only.";

    public MethodApplicability Evaluate(DriveIdentity id, ChipsetInfo chipset)
    {
        string hay = $"{id.Vendor} {id.Model}".ToUpperInvariant();
        bool looksUhd = UhdHints.Any(h => hay.Contains(h, StringComparison.OrdinalIgnoreCase));
        return looksUhd
            ? MethodApplicability.Perhaps("Looks like a UHD-capable drive, but service-mode read is not implemented in v1.")
            : MethodApplicability.No("Not a recognised UHD Blu-ray drive; service-mode read does not apply.");
    }

    public ExtractionResult Extract(ScsiDevice device, DriveIdentity id, ChipsetInfo chipset,
        IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        progress.Report(new ExtractionProgress(0, "UHD service mode"));
        return ExtractionResult.Unsupported(Id, DisplayName,
            "UHD Blu-ray service-mode read requires a supported drive/firmware and is not implemented in v1. " +
            "Use MakeMKV's LibreDrive tooling for UHD firmware, or a hardware programmer.");
    }
}

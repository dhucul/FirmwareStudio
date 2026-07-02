using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Extraction;

/// <summary>
/// One firmware-extraction strategy. Implementations are read-only. <see cref="Extract"/> runs on a
/// background thread (the orchestrator wraps it in <c>Task.Run</c>); it should poll <paramref name="ct"/>
/// and stream <see cref="ExtractionProgress"/>.
/// </summary>
public interface IFirmwareExtractionMethod
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }

    MethodApplicability Evaluate(DriveIdentity id, ChipsetInfo chipset);

    ExtractionResult Extract(ScsiDevice device, DriveIdentity id, ChipsetInfo chipset,
        IProgress<ExtractionProgress> progress, CancellationToken ct);
}

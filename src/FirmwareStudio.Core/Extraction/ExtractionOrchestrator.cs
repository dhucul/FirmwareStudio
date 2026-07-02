using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Extraction;

/// <summary>Owns the set of extraction methods, picks one, and runs it on a background thread.</summary>
public sealed class ExtractionOrchestrator
{
    private readonly IReadOnlyList<IFirmwareExtractionMethod> _methods;

    public ExtractionOrchestrator(IEnumerable<IFirmwareExtractionMethod>? methods = null)
        => _methods = methods?.ToArray() ?? new IFirmwareExtractionMethod[]
        {
            new UniversalReadBufferMethod(),
            new MediaTekCacheReadMethod(),
            new PldsVendorReadMethod(),
            new UhdServiceModeMethod(),
        };

    public IReadOnlyList<IFirmwareExtractionMethod> Methods => _methods;

    public IFirmwareExtractionMethod? ById(string id)
        => _methods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Auto pick: MediaTek cache read when applicable, otherwise the universal probe.</summary>
    public IFirmwareExtractionMethod PickAuto(DriveIdentity id, ChipsetInfo chip)
    {
        var mtk = ById("mediatek");
        if (mtk is not null && mtk.Evaluate(id, chip).Level == Applicability.Applicable) return mtk;
        return ById("universal")!;
    }

    public async Task<ExtractionResult> RunAsync(ScsiDevice device, DriveIdentity id, ChipsetInfo chip,
        IFirmwareExtractionMethod method, IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => method.Extract(device, id, chip, progress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ExtractionResult.Failed(method.Id, method.DisplayName, ex.Message);
        }
    }
}

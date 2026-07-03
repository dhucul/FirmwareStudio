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
            new MtkFlashReadMethod(),
            new MediaTekCacheReadMethod(),
            new PldsVendorReadMethod(),
            new NecRenesasReadMethod(),
            new UhdServiceModeMethod(),
        };

    public IReadOnlyList<IFirmwareExtractionMethod> Methods => _methods;

    public IFirmwareExtractionMethod? ById(string id)
        => _methods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Auto pick, best firmware-yield first: the NEC/Renesas ReadRAM (0xCC) reads that family's flash
    /// directly; else the MediaTek/Lite-On flash read (READ BUFFER mode 6) reads the actual firmware flash;
    /// then the 0xF1 cache read (controller DRAM); then the universal READ BUFFER probe as the always-safe
    /// fallback. Each vendor method is only "Applicable" for its own chipset family, so there is no conflict.
    /// </summary>
    public IFirmwareExtractionMethod PickAuto(DriveIdentity id, ChipsetInfo chip)
    {
        var nec = ById("nec");
        if (nec is not null && nec.Evaluate(id, chip).Level == Applicability.Applicable) return nec;
        var flash = ById("mtk-flash");
        if (flash is not null && flash.Evaluate(id, chip).Level == Applicability.Applicable) return flash;
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

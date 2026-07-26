using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Extraction;

/// <summary>Owns the set of extraction methods, picks one, and runs it on a background thread.</summary>
public sealed class ExtractionOrchestrator
{
    private static readonly string[] PreferredAutoOrder =
        { "nec", "mtk-flash", "mediatek", "plds" };
    private readonly IReadOnlyList<IFirmwareExtractionMethod> _methods;

    public ExtractionOrchestrator(IEnumerable<IFirmwareExtractionMethod>? methods = null)
    {
        _methods = methods?.ToArray() ?? new IFirmwareExtractionMethod[]
        {
            new UniversalReadBufferMethod(),
            new MtkFlashReadMethod(),
            new MediaTekCacheReadMethod(),
            new PldsVendorReadMethod(),
            new NecRenesasReadMethod(),
            new UhdServiceModeMethod(),
        };
        if (_methods.Count == 0)
            throw new ArgumentException("At least one extraction method is required.", nameof(methods));
        if (_methods.Any(m => m is null))
            throw new ArgumentException("Extraction methods cannot contain null entries.", nameof(methods));
        if (_methods.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new ArgumentException("Extraction method IDs must be unique.", nameof(methods));
    }

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
        var ordered = AutoMethods().ToArray();
        return ordered.FirstOrDefault(m => m.Evaluate(id, chip).Level == Applicability.Applicable)
            ?? ordered.FirstOrDefault(m => m.Evaluate(id, chip).Level == Applicability.Maybe)
            ?? ordered[0];
    }

    /// <summary>
    /// Empirical Auto: try methods in firmware-yield priority order and return the FIRST that actually returns
    /// data. Robust to chipset mislabelling — a MediaTek-based "Optiarc" whose mode-6 flash read is rejected
    /// simply falls through to the 0xF1 cache read that works, because the choice is by what the drive really
    /// answers, not by the detected family. Each non-matching method fails fast on its own support probe
    /// (illegal opcode → Unsupported), so the full read only happens for the method that wins. Priority order
    /// puts higher-yield firmware reads first (nec/mtk-flash flash → mediatek cache → plds regs → universal).
    /// </summary>
    public async Task<ExtractionResult> RunAutoAsync(ScsiDevice device, DriveIdentity id, ChipsetInfo chip,
        IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        ExtractionResult? last = null;
        foreach (var m in AutoMethods())
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new ExtractionProgress(0, $"Auto: trying {m.DisplayName}…",
                $"Auto → {m.DisplayName} ({m.Evaluate(id, chip).Level})"));
            var res = await RunAsync(device, id, chip, m, progress, ct);
            if (res.Success && res.ByteCount > 0)
            {
                progress.Report(new ExtractionProgress(100, null, $"Auto selected {m.DisplayName}."));
                return res;
            }
            last = res;
        }
        return last ?? ExtractionResult.Unsupported("auto", "Auto",
            "No extraction method returned data on this drive.");
    }

    private IEnumerable<IFirmwareExtractionMethod> AutoMethods()
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in PreferredAutoOrder)
        {
            var method = ById(id);
            if (method is not null && yielded.Add(method.Id))
                yield return method;
        }
        foreach (var method in _methods)
            if (!string.Equals(method.Id, "universal", StringComparison.OrdinalIgnoreCase) &&
                yielded.Add(method.Id))
                yield return method;
        var universal = ById("universal");
        if (universal is not null && yielded.Add(universal.Id))
            yield return universal;
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

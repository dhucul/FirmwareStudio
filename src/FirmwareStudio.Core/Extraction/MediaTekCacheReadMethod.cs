using FirmwareStudio.Core.Analysis;
using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Extraction;

/// <summary>
/// Method 2 — MediaTek vendor "read cache" (opcode 0xF1). Reads the drive controller's internal DRAM
/// buffer. Depending on chipset generation this buffer may hold firmware code regions and/or cached disc
/// data; on many drives it is the disc-data cache and reads back empty (all zero) when the drive is idle
/// with no media. Software-only and read-only. Derived from redumper's MEDIATEK_READ_CACHE.
/// </summary>
public sealed class MediaTekCacheReadMethod : IFirmwareExtractionMethod
{
    // 16 KiB per command — the same proven-safe granularity MtkFlashReadMethod uses. A 64 KiB (0x10000)
    // request is the one value that must NOT be used here: the ATAPI transfer byte-count is 16-bit, so a
    // length of exactly 0x10000 truncates to 0 in the transport and the drive returns GOOD with a zero-byte
    // transfer — which the old code misread as a (false) "cache is empty" and blamed on the hardware.
    private const int ChunkSize = 0x4000;
    private const uint MaxSize = 8 * 1024 * 1024; // bounded capture window

    public string Id => "mediatek";
    public string DisplayName => "MediaTek internal cache read (0xF1)";
    public string Description =>
        "Reads the internal DRAM cache of MediaTek-chipset drives (the common modern CD/DVD controller). " +
        "Read-only. The buffer may contain firmware code and/or cached disc data — on many drives it is the " +
        "disc-data cache and reads back empty when idle. This is never a guaranteed byte-exact flash ROM.";

    public MethodApplicability Evaluate(DriveIdentity id, ChipsetInfo chipset) => chipset.Family switch
    {
        ChipsetFamily.MediaTek => MethodApplicability.Yes("MediaTek chipset detected — cache read (0xF1) applies."),
        ChipsetFamily.Unknown => MethodApplicability.Perhaps("Chipset unknown; 0xF1 may work if it is a MediaTek drive."),
        _ => MethodApplicability.No($"Detected {chipset.Family} chipset; 0xF1 read-cache targets MediaTek drives."),
    };

    public ExtractionResult Extract(IScsiDevice device, DriveIdentity id, ChipsetInfo chipset,
        IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        device = device.WithCancellation(ct);
        progress.Report(new ExtractionProgress(0, "Reading controller RAM (raw address space)"));
        using var output = new MemoryStream();
        string stop = "read the configured 8 MiB address window";
        while (output.Length < MaxSize)
        {
            int length = (int)Math.Min(ChunkSize, MaxSize - output.Length);
            var r = device.SendCommand(ScsiCommand.MediaTekReadCache((uint)output.Length, (uint)length),
                ScsiDirection.In, new byte[length], note: $"MediaTek cache offset=0x{output.Length:X} length={length}");
            if (!r.Good || !r.ValidTransferLength || r.TransferredLength == 0 || r.Data is null)
            {
                stop = !r.Good ? r.StatusText : !r.ValidTransferLength
                    ? $"invalid transfer length {r.TransferredLength}/{length}" : "zero-byte transfer";
                if (output.Length == 0)
                    return r.DeviceIoOk && r.SenseInfo.OpcodeUnsupported
                        ? ExtractionResult.Unsupported(Id, DisplayName, stop)
                        : ExtractionResult.Failed(Id, DisplayName, stop);
                break;
            }
            output.Write(r.Data, 0, r.TransferredLength);
            progress.Report(new ExtractionProgress((int)(100 * output.Length / MaxSize)));
        }
        ct.ThrowIfCancellationRequested();
        byte[] data = output.ToArray();
        string summary = $"Captured raw addresses 0x000000..0x{data.Length:X6} ({data.Length:N0} bytes); {stop}. " +
            "Zero-filled spans and repeated bytes are preserved. " + DumpAnalyzer.Analyze(data).Verdict();
        const string label = "raw controller RAM/cache (may include disc data; not a verified ROM)";
        return data.Length == MaxSize
            ? ExtractionResult.Ok(Id, DisplayName, data, label, summary)
            : ExtractionResult.Partial(Id, DisplayName, data, label, summary);
    }
}

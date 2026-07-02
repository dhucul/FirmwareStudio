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
    private const int ChunkSize = 64 * 1024;
    // Read up to the largest plausible cache (~8 MB) but stop early once it is clearly empty.
    private const uint MaxSize = 8 * 1024 * 1024;
    // If nothing non-zero by here, treat the cache as empty and stop. Kept small: repeatedly issuing
    // 0xF1 against an empty cache can make some drives stop responding until the command times out.
    private const uint EmptyExitThreshold = 128 * 1024;

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

    public ExtractionResult Extract(ScsiDevice device, DriveIdentity id, ChipsetInfo chipset,
        IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        progress.Report(new ExtractionProgress(0, "MediaTek cache read (0xF1)"));

        // First read doubles as the support probe.
        var first = device.SendCommand(ScsiCommand.MediaTekReadCache(0, ChunkSize), ScsiDirection.In,
            new byte[ChunkSize], note: "MediaTek 0xF1 read-cache off=0");
        var s = first.SenseInfo;
        if (first.DeviceIoOk && first.ScsiStatus != 0x00 && s.Key == 0x05)
            return ExtractionResult.Unsupported(Id, DisplayName,
                $"The drive rejected the MediaTek 0xF1 read-cache command (ILLEGAL REQUEST, asc={s.Asc:X2}/{s.Ascq:X2}). " +
                "It is not a MediaTek cache-read drive, or the firmware does not expose this command.");
        if (!first.Good || first.Data is null)
            return ExtractionResult.Unsupported(Id, DisplayName,
                $"MediaTek 0xF1 read-cache did not return data ({first.StatusText}).");

        using var ms = new MemoryStream();
        ms.Write(first.Data, 0, first.Data.Length);
        long nonZero = CountNonZero(first.Data);
        uint offset = ChunkSize;
        progress.Report(new ExtractionProgress((int)(100L * offset / MaxSize)));

        while (offset < MaxSize)
        {
            ct.ThrowIfCancellationRequested();

            // Bail early if the cache is clearly empty, rather than reading 8 MB of zeros.
            if (nonZero == 0 && offset >= EmptyExitThreshold) break;

            uint len = Math.Min((uint)ChunkSize, MaxSize - offset);
            var read = device.SendCommand(ScsiCommand.MediaTekReadCache(offset, len), ScsiDirection.In,
                new byte[len], note: $"MediaTek 0xF1 read-cache off={offset} len={len}");
            if (!read.Good || read.Data is null)
            {
                progress.Report(new ExtractionProgress(0, null,
                    $"Cache read stopped at {offset:N0} bytes ({read.StatusText})."));
                break;
            }
            ms.Write(read.Data, 0, (int)len);
            nonZero += CountNonZero(read.Data);
            offset += len;
            progress.Report(new ExtractionProgress((int)(100L * offset / MaxSize)));
        }

        byte[] data = ms.ToArray();

        if (nonZero == 0)
            return ExtractionResult.Unsupported(Id, DisplayName,
                $"The MediaTek 0xF1 cache returned {data.Length:N0} bytes but all were zero. On this drive the " +
                "cache holds disc data (empty with no/blank media), not the firmware ROM. Try again with a " +
                "data disc inserted to capture the cache, or use a hardware programmer for a true firmware dump.");

        return ExtractionResult.Ok(Id, DisplayName, data,
            "MediaTek controller cache/RAM image (may include firmware code and/or cached disc data)",
            $"Read {data.Length:N0} bytes from the MediaTek internal cache via opcode 0xF1 " +
            $"({100.0 * nonZero / Math.Max(1, data.Length):F1}% non-zero).");
    }

    private static long CountNonZero(byte[] buf)
    {
        long n = 0;
        foreach (byte b in buf) if (b != 0) n++;
        return n;
    }
}

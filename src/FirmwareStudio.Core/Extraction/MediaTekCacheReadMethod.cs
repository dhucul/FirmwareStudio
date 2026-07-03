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
    // Read up to the largest plausible cache (~8 MB) but stop early once it is clearly empty.
    private const uint MaxSize = 8 * 1024 * 1024;
    // If nothing non-zero by here, treat the cache as empty and stop. Kept small: repeatedly issuing
    // 0xF1 against an empty cache can make some drives stop responding until the command times out.
    private const uint EmptyExitThreshold = 128 * 1024;
    // The controller aliases a small unique cache region across the whole 0xF1 address window (e.g. ~1 MiB
    // mirrored 8× over 8 MiB). Once ≥2 copies have been read, the repeat is detected and the read stops, so
    // the dump is the unique region, not megabytes of identical mirror copies. Candidate periods are tried
    // smallest-first (so the dump is trimmed maximally) and span common cache sizes down to 256 KiB. Detection
    // uses a whole-overlap non-zero-aware compare — a false small period would need the ENTIRE buffer to
    // coincidentally repeat, which is vanishingly unlikely for real data, so this won't over-trim.
    private const int MinPeriod = 0x40000;   // 256 KiB — the smallest period we will trim to
    private static readonly int[] PeriodCandidates =
        { 0x40000, 0x80000, 0x100000, 0x180000, 0x200000, 0x300000, 0x400000 };

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

        var buf = new byte[MaxSize];
        int written = 0;
        long nonZero = 0;
        uint offset = 0;
        int mirrorPeriod = 0;
        long nextMirrorCheck = 2L * MinPeriod;   // first check once two minimum-period units are in
        string stop = $"reached the {MaxSize / (1024 * 1024)} MiB cap";

        while (offset < MaxSize)
        {
            ct.ThrowIfCancellationRequested();

            // Bail early if the cache is clearly empty, rather than reading megabytes of zeros.
            if (nonZero == 0 && offset >= EmptyExitThreshold) { stop = $"cache empty through {offset:N0} bytes"; break; }

            uint len = Math.Min((uint)ChunkSize, MaxSize - offset);
            var r = device.SendCommand(ScsiCommand.MediaTekReadCache(offset, len), ScsiDirection.In,
                new byte[len], note: $"MediaTek 0xF1 read-cache off={offset} len={len}");

            // The first read doubles as the support probe. ILLEGAL REQUEST (key 0x05) = the drive rejected the
            // command (not a MediaTek cache-read drive); anything else means it understood it.
            if (offset == 0)
            {
                var s = r.SenseInfo;
                if (r.DeviceIoOk && r.ScsiStatus != 0x00 && s.Key == 0x05)
                    return ExtractionResult.Unsupported(Id, DisplayName,
                        $"The drive rejected the MediaTek 0xF1 read-cache command (ILLEGAL REQUEST, asc={s.Asc:X2}/{s.Ascq:X2}). " +
                        "It is not a MediaTek cache-read drive, or the firmware does not expose this command.");
                if (!r.Good || r.Data is null)
                    return ExtractionResult.Unsupported(Id, DisplayName,
                        $"MediaTek 0xF1 read-cache did not return data ({r.StatusText}).");
            }

            if (!r.Good || r.Data is null) { stop = $"drive stopped at {offset:N0} bytes ({r.StatusText})"; break; }

            // Honour the drive-reported transfer length (Windows SPTI sets it to the bytes actually moved); a
            // short-but-non-zero read is a per-command cap, not end-of-data, so continue from offset+got.
            int got = r.TransferredLength;
            if (got < 0 || got > (int)len) got = (int)len;
            if (got == 0) { stop = $"empty read at {offset:N0} bytes (end of readable cache)"; break; }

            Array.Copy(r.Data, 0, buf, written, got);
            for (int i = 0; i < got; i++) if (r.Data[i] != 0) nonZero++;
            written += got;
            offset += (uint)got;
            progress.Report(new ExtractionProgress((int)(100L * offset / MaxSize)));

            // Once ≥2 units are in, detect the cache mirror and stop — keep only the unique region. Triggered by
            // a byte threshold (re-anchored to the next unit), not exact alignment, so an occasional short read
            // can't silently disable de-mirroring. Byte placement is by absolute offset, so the compare is valid
            // regardless of how the reads chunked.
            if (nonZero > 0 && written >= nextMirrorCheck)
            {
                nextMirrorCheck = ((long)(written / MinPeriod) + 1) * MinPeriod;
                int p = FindPeriod(buf, written);
                if (p > 0)
                {
                    mirrorPeriod = p;
                    stop = $"cache mirrors every {p / 1024:N0} KiB ({written / p}× seen) — kept the unique region";
                    break;
                }
            }
        }

        int keep = mirrorPeriod > 0 ? mirrorPeriod : written;
        byte[] data = buf[..keep];

        if (nonZero == 0)
            return ExtractionResult.Unsupported(Id, DisplayName,
                $"The MediaTek 0xF1 cache returned {data.Length:N0} bytes but all were zero ({stop}). On this drive the " +
                "cache holds disc data (empty with no/blank media), not the firmware ROM. Try again with a data disc " +
                "inserted to capture the cache, or use the flash-read method (0x3C mode 6) / a hardware programmer for a true dump.");

        var analysis = DumpAnalyzer.Analyze(data);
        string mirrorNote = mirrorPeriod > 0
            ? $" De-mirrored: the controller aliases this {mirrorPeriod / 1024:N0} KiB region across the full 0xF1 window, so only the unique copy is kept."
            : "";
        return ExtractionResult.Ok(Id, DisplayName, data,
            "MediaTek controller cache/RAM image (resident firmware code/config and/or cached disc data)",
            $"Read {data.Length:N0} bytes from the MediaTek internal cache via opcode 0xF1. {analysis.Verdict()}{mirrorNote}");
    }

    /// <summary>
    /// Smallest candidate period P (P ≤ len/2) at which the whole buffer repeats: the overlap
    /// <c>buf[0..len-P)</c> matches <c>buf[P..len)</c> on their non-zero content. Whole-overlap (not just the
    /// first two blocks) means a spurious small period would need the entire buffer to coincide, so this
    /// won't over-trim. 0 = no repeat detected.
    /// </summary>
    private static int FindPeriod(byte[] buf, int len)
    {
        foreach (int p in PeriodCandidates)
        {
            if ((long)p * 2 > len) break;   // ascending → need at least two copies before P is testable
            if (SimilarNonZero(buf, 0, p, len - p) >= 0.85)
                return p;
        }
        return 0;
    }

    /// <summary>Fraction of "meaningful" positions (either side non-zero) in two blocks that are byte-equal;
    /// returns 0 if there is too little non-zero data to judge (so two near-empty blocks don't false-match).</summary>
    private static double SimilarNonZero(byte[] buf, int aStart, int bStart, int count)
    {
        long considered = 0, matches = 0;
        for (int i = 0; i < count; i++)
        {
            byte a = buf[aStart + i], b = buf[bStart + i];
            if (a == 0 && b == 0) continue;
            considered++;
            if (a == b) matches++;
        }
        return considered < count / 64 ? 0 : (double)matches / considered;
    }
}

using System.Text;

namespace FirmwareStudio.Core.Analysis;

/// <summary>One 256 KiB slice of a dump and how much of it is non-zero.</summary>
public readonly record struct DumpRegion(long Start, long End, long NonZero)
{
    public double Percent => End > Start ? 100.0 * NonZero / (End - Start) : 0;
}

/// <summary>Result of characterising a raw dump: is it empty, sparse-but-real firmware, or mirrored RAM?</summary>
public sealed class DumpAnalysis
{
    public long Size { get; init; }
    public long NonZero { get; init; }
    public double NonZeroPercent => Size > 0 ? 100.0 * NonZero / Size : 0;
    /// <summary>Detected mirror/alias period in bytes (the controller RAM repeats every N bytes), or null.</summary>
    public int? RepeatPeriod { get; init; }
    public IReadOnlyList<DumpRegion> Regions { get; init; } = [];
    public IReadOnlyList<(long Offset, string Text)> Strings { get; init; } = [];

    /// <summary>A one-paragraph, human-honest verdict for the extraction summary / sidecar.</summary>
    public string Verdict()
    {
        if (NonZero == 0)
            return $"All {Size:N0} bytes are zero — nothing was captured (empty/idle controller RAM or unsupported read).";

        var sb = new StringBuilder();
        sb.Append($"{NonZero:N0} of {Size:N0} bytes non-zero ({NonZeroPercent:F1}%).");
        if (RepeatPeriod is int p)
            sb.Append($" Content repeats every {p:N0} bytes — the true unique data is ~{p / 1024} KiB, mirrored {Size / p}× across this dump.");
        int firmwareHits = Strings.Count(s => LooksLikeFirmware(s.Text));
        if (firmwareHits > 0)
            sb.Append($" Contains {Strings.Count} readable strings incl. firmware/config markers — this is real firmware content, not blank cache.");
        else if (Strings.Count > 0)
            sb.Append($" Contains {Strings.Count} readable strings.");
        return sb.ToString();
    }

    private static bool LooksLikeFirmware(string s)
        => s.Contains("PLEXTOR", StringComparison.OrdinalIgnoreCase)
           || s.Contains("LITE-ON", StringComparison.OrdinalIgnoreCase)
           || s.Contains("DVD", StringComparison.OrdinalIgnoreCase)
           || s.Contains("CDROM", StringComparison.OrdinalIgnoreCase)
           || s.Contains("CORPORATION", StringComparison.OrdinalIgnoreCase)
           || s.Contains("KEYPARA", StringComparison.OrdinalIgnoreCase)
           || s.Contains("INQ", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Characterises a raw dump so the tool can tell the user what a mostly-zero image actually contains instead
/// of leaving them to eyeball a hex wall. Reports the non-zero region map, any mirror/alias period (controller
/// RAM often aliases a small unique region across a large address window), and readable ASCII strings — which
/// on an optical drive surface the firmware version/build date and media tables. Pure analysis, read-only.
/// </summary>
public static class DumpAnalyzer
{
    private const int RegionSize = 256 * 1024;

    public static DumpAnalysis Analyze(byte[] data, int maxStrings = 80, int minStringLen = 5)
    {
        long nonZero = 0;
        foreach (byte b in data) if (b != 0) nonZero++;

        // Region map (256 KiB granularity), only slices that carry data.
        var regions = new List<DumpRegion>();
        for (int r = 0; r < data.Length; r += RegionSize)
        {
            int end = Math.Min(r + RegionSize, data.Length);
            long c = 0;
            for (int i = r; i < end; i++) if (data[i] != 0) c++;
            if (c > 0) regions.Add(new DumpRegion(r, end, c));
        }

        // ASCII strings (>= minStringLen printable run).
        var strings = new List<(long, string)>();
        var sb = new StringBuilder();
        int start = 0;
        for (int i = 0; i <= data.Length && strings.Count < maxStrings; i++)
        {
            int c = i < data.Length ? data[i] : 0;
            if (c is >= 32 and < 127) { if (sb.Length == 0) start = i; sb.Append((char)c); }
            else
            {
                if (sb.Length >= minStringLen) strings.Add((start, sb.ToString().Trim()));
                sb.Clear();
            }
        }

        return new DumpAnalysis
        {
            Size = data.Length,
            NonZero = nonZero,
            RepeatPeriod = DetectRepeatPeriod(data, nonZero),
            Regions = regions,
            Strings = strings,
        };
    }

    /// <summary>
    /// Find the smallest power-of-two-ish period P (256 KiB … half the image) at which the dump mirrors itself.
    /// Comparing raw bytes would be fooled by the sea of zeros (any period "matches" ~95% of the time), so we
    /// score only positions where at least one side is non-zero. A live/volatile cache drifts slightly between
    /// reads, so the threshold is lenient (80%). Returns null if nothing repeats.
    /// </summary>
    private static int? DetectRepeatPeriod(byte[] data, long nonZero)
    {
        if (nonZero == 0 || data.Length < 2 * RegionSize) return null;

        foreach (int period in new[] { 0x40000, 0x80000, 0x100000, 0x200000, 0x400000 })
        {
            if (period * 2 > data.Length) break;
            long considered = 0, matches = 0;
            int stride = Math.Max(1, (data.Length - period) / 200_000);   // bound the work
            for (int i = 0; i + period < data.Length; i += stride)
            {
                byte a = data[i], b = data[i + period];
                if (a == 0 && b == 0) continue;   // ignore the zero-sea
                considered++;
                if (a == b) matches++;
            }
            if (considered >= 1000 && (double)matches / considered >= 0.80)
                return period;
        }
        return null;
    }
}

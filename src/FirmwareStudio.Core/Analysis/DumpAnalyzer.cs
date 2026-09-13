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
            return $"All {Size:N0} captured bytes are zero; the contents alone do not establish whether firmware is exposed.";

        var sb = new StringBuilder();
        sb.Append($"{NonZero:N0} of {Size:N0} bytes non-zero ({NonZeroPercent:F1}%).");
        if (RepeatPeriod is int p)
            sb.Append($" The captured span repeats exactly every {p:N0} bytes; all copies remain preserved.");
        int firmwareHits = Strings.Count(s => LooksLikeFirmware(s.Text));
        if (firmwareHits > 0)
            sb.Append($" Contains {Strings.Count} readable strings including possible firmware/config markers; these strings alone do not prove firmware provenance.");
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

    public static DumpAnalysis Analyze(byte[] data, int maxStrings = 80, int minStringLen = 5, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        long nonZero = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (data[i] != 0) nonZero++;
        }

        // Region map (256 KiB granularity), only slices that carry data.
        var regions = new List<DumpRegion>();
        for (int r = 0; r < data.Length; r += RegionSize)
        {
            ct.ThrowIfCancellationRequested();
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
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
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
            RepeatPeriod = DetectRepeatPeriod(data, nonZero, ct),
            Regions = regions,
            Strings = strings,
        };
    }

    /// <summary>
    /// Find an exact period in the captured bytes. This is descriptive only: all raw bytes are retained.
    /// </summary>
    private static int? DetectRepeatPeriod(byte[] data, long nonZero, CancellationToken ct)
    {
        if (nonZero == 0 || data.Length < 2 * RegionSize) return null;

        foreach (int period in new[] { 0x40000, 0x80000, 0x100000, 0x200000, 0x400000 })
        {
            if (period * 2 > data.Length) break;
            bool equal = true;
            for (int i = 0; i + period < data.Length; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                if (data[i] != data[i + period]) { equal = false; break; }
            }
            if (equal) return period;
        }
        return null;
    }
}

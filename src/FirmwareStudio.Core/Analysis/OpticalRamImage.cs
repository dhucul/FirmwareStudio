using System.Text;
using System.Text.RegularExpressions;

namespace FirmwareStudio.Core.Analysis;

/// <summary>A named structural section located inside a controller-RAM image (tag → offset).</summary>
public readonly record struct RamSection(string Name, long Offset);

/// <summary>
/// The executable code bank found inside the RAM image: an 8051/MCS-51 microcode region and the
/// mapping between its file offset and the CPU's runtime code address. For the MT62xx the dump
/// captures runtime <c>0xC000–0xFFFF</c> then <c>0x0000–0x5FFF</c>, so
/// <c>runtime = fileOffset + RuntimeDelta (mod 0x10000)</c> with <see cref="RuntimeDelta"/> = -0x4000.
/// </summary>
public sealed class CodeBankInfo
{
    public long Offset { get; init; }
    public long Length { get; init; }
    public string Arch { get; init; } = "8051/MCS-51";
    public double SignatureDensity { get; init; }
    public int RuntimeDelta { get; init; }
    public int TargetsAligned { get; init; }
    public int TargetsTotal { get; init; }
}

/// <summary>
/// Structured parse of a MediaTek/PLDS optical-drive <b>controller-RAM image</b> — the (de-mirrored)
/// output of a <c>0xF1</c> cache read, headed by the <c>FF 54 54 45</c> tag. Where
/// <see cref="DumpAnalyzer"/> only characterises an arbitrary dump and <see cref="Firmware.FirmwareImage"/>
/// understands the <c>.1KN</c> flash wrapper, this pulls out the drive identity (stored plain and
/// 16-bit word-swapped), serial, OEM, the tagged sections (EXTRAINQ / KEYPARA / MEDIA TYPE LOG / …),
/// the supported-media list, and the 8051 code bank with its runtime mapping. Pure, read-only.
/// </summary>
public sealed class OpticalRamImageInfo
{
    public long Size { get; init; }
    public string? Vendor { get; init; }
    public string? Model { get; init; }
    public string? FirmwareRev { get; init; }
    public string? BuildDate { get; init; }
    public string? Serial { get; init; }
    public string? Oem { get; init; }
    public IReadOnlyList<RamSection> Sections { get; init; } = [];
    public IReadOnlyList<string> Media { get; init; } = [];
    public CodeBankInfo? CodeBank { get; init; }
    public DumpAnalysis Content { get; init; } = null!;

    /// <summary>Honest one-paragraph account of what the RAM image contains.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("MediaTek/PLDS optical-drive controller-RAM image (0xF1 cache read). ");
        if (Model is not null)
        {
            sb.Append($"Identity: {Vendor} {Model}");
            if (FirmwareRev is not null) sb.Append($", firmware {FirmwareRev}");
            if (BuildDate is not null) sb.Append($" (built {BuildDate})");
            sb.Append(". ");
        }
        if (Serial is not null) sb.Append($"Serial {Serial}. ");
        if (Oem is not null) sb.Append($"OEM {Oem}. ");
        if (Sections.Count > 0)
            sb.Append($"{Sections.Count} firmware sections ({string.Join(", ", Sections.Select(s => s.Name))}). ");
        if (Media.Count > 0)
            sb.Append($"Write-strategy table for {Media.Count} media types. ");
        if (CodeBank is { } cb)
            sb.Append($"Embeds a {cb.Length / 1024} KiB {cb.Arch} code bank at 0x{cb.Offset:X} " +
                      $"(runtime = fileOffset {(cb.RuntimeDelta < 0 ? "-" : "+")} 0x{Math.Abs(cb.RuntimeDelta):X4}; " +
                      $"{cb.TargetsAligned}/{cb.TargetsTotal} call/jump targets aligned). ");
        sb.Append("This is decrypted firmware as loaded in controller RAM — useful, but not a byte-exact flashable ROM.");
        return sb.ToString();
    }
}

/// <summary>
/// Parses a MediaTek/PLDS controller-RAM image into structured fields. Read-only, no SCSI.
/// Reuses <see cref="DumpAnalyzer.Analyze"/> for the string/region/mirror backbone.
/// </summary>
public static class OpticalRamImage
{
    private static readonly byte[] RamMagic = [0xFF, 0x54, 0x54, 0x45];   // "\xffTTE"
    private static readonly string[] SectionTags =
        ["EXTRAINQ", "RDBOOT", "KEYPARA", "MEDIA TYPE LOG", "DBUG", "TST1", "TST2", "FLT"];
    private static readonly string[] MediaNames =
        ["CDROM", "CDR", "DLSCDRW", "HSCDRW", "USCDRW", "US+CDRW", "DVD5", "DVD9",
         "DVD+R", "DVD-R", "DVD+RW", "DVD-RW", "DVD+R9", "DVD-R9", "DVDRAM", "DVD+RW9", "DVD-RW9"];

    /// <summary>Cheap detector: is this a controller-RAM image rather than a .1KN flash file?</summary>
    public static bool Looks(byte[] data)
    {
        if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(RamMagic)) return true;
        int hits = 0;
        foreach (var tag in new[] { "KEYPARA", "EXTRAINQ", "MEDIA TYPE LOG" })
            if (IndexOf(data, Encoding.ASCII.GetBytes(tag), 0) >= 0) hits++;
        return hits >= 2;
    }

    public static OpticalRamImageInfo Parse(byte[] data)
    {
        // Build a 1:1 ASCII shadow so regex offsets map straight back to file offsets.
        string ascii = ToAsciiShadow(data);
        var (vendor, model, rev, build) = DetectIdentity(data, ascii);

        return new OpticalRamImageInfo
        {
            Size = data.Length,
            Vendor = vendor,
            Model = model,
            FirmwareRev = rev,
            BuildDate = build,
            Serial = DetectSerial(ascii, model),
            Oem = DetectOem(ascii),
            Sections = DetectSections(data),
            Media = DetectMedia(data),
            CodeBank = DetectCodeBank(data),
            Content = DumpAnalyzer.Analyze(data, maxStrings: 120),
        };
    }

    /// <summary>Undo a 16-bit word-swap (bytes stored high/low reversed per 16-bit word).</summary>
    public static byte[] Deswap(ReadOnlySpan<byte> src)
    {
        var outp = src.ToArray();
        for (int i = 0; i + 1 < outp.Length; i += 2) (outp[i], outp[i + 1]) = (outp[i + 1], outp[i]);
        return outp;
    }

    // Identity is a SCSI-INQUIRY layout: vendor(8) product(16) rev(4) then a yyyy/mm/dd build stamp.
    // We anchor on the date and slice the fixed-width fields sitting immediately before it. If the
    // plain copy isn't present, retry against a word-swapped copy of the buffer.
    private static (string?, string?, string?, string?) DetectIdentity(byte[] data, string ascii)
    {
        var r = MatchIdentity(ascii);
        if (r.Item2 is not null) return r;
        // fall back to the word-swapped shadow
        string swapped = ToAsciiShadow(Deswap(data));
        return MatchIdentity(swapped);
    }

    private static (string?, string?, string?, string?) MatchIdentity(string ascii)
    {
        var m = Regex.Match(ascii, @"(19|20)\d{2}/\d{2}/\d{2}([ \d:]{0,6})");
        if (!m.Success || m.Index < 28) return (null, null, null, null);
        int s = m.Index - 28;
        string vendor = ascii.Substring(s, 8).Trim();
        string product = ascii.Substring(s + 8, 16).Trim();
        string rev = ascii.Substring(s + 24, 4).Trim();
        string build = m.Value.Trim();
        // sanity: vendor/product should be mostly printable identifier chars
        if (product.Length < 2 || !vendor.All(c => c is >= ' ' and < (char)127)) return (null, null, null, null);
        return (Nz(vendor), Nz(product), Nz(rev), Nz(build));
    }

    // The drive serial appears (plain and word-swapped) as a 10–14 char alnum token, repeated,
    // and distinct from the model. Pick the most frequent such token.
    private static string? DetectSerial(string ascii, string? model)
    {
        var counts = new Dictionary<string, int>();
        foreach (Match m in Regex.Matches(ascii, @"[0-9A-Z]{10,14}"))
        {
            string t = m.Value;
            if (model is not null && model.Replace(" ", "").Contains(t)) continue;
            counts[t] = counts.GetValueOrDefault(t) + 1;
        }
        return counts.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value)
                     .Select(kv => kv.Key).FirstOrDefault();
    }

    private static string? DetectOem(string ascii)
    {
        var m = Regex.Match(ascii, @"[A-Z][A-Z ]{2,20}CORPORATION");
        if (!m.Success) return null;
        string t = m.Value.Trim();
        // The dump stores "SPLDS CORPORATION" (a stray leading field byte); normalise to "PLDS".
        return t.StartsWith("SPLDS ", StringComparison.Ordinal) ? "PLDS" + t[5..] : t;
    }

    private static List<RamSection> DetectSections(byte[] data)
    {
        var found = new List<RamSection>();
        foreach (var tag in SectionTags)
        {
            int p = IndexOf(data, Encoding.ASCII.GetBytes(tag), 0);
            if (p >= 0) found.Add(new RamSection(tag, p));
        }
        return found.OrderBy(s => s.Offset).ToList();
    }

    private static List<string> DetectMedia(byte[] data)
    {
        // Search the KEYPARA table window; require a non-alnum boundary so "CDR" != "CDROM".
        int keypara = IndexOf(data, "KEYPARA"u8.ToArray(), 0);
        int from = keypara >= 0 ? keypara : 0;
        int to = Math.Min(data.Length, from + 0x1000);
        var media = new List<string>();
        foreach (var name in MediaNames)
        {
            var needle = Encoding.ASCII.GetBytes(name);
            // A prefix like "CDR" also matches inside "CDROM"; keep scanning until a real record
            // boundary (non-alnum byte follows) so every media type is found, not just the first hit.
            for (int p = IndexOf(data, needle, from, to); p >= 0; p = IndexOf(data, needle, p + 1, to))
            {
                int after = p + needle.Length < data.Length ? data[p + needle.Length] : 0;
                bool boundary = !(after is (>= 48 and <= 57) or (>= 65 and <= 90) or (>= 97 and <= 122));
                if (boundary) { media.Add(name); break; }
            }
        }
        return media;
    }

    // Signature-opcode density locates the 8051 code bank; the runtime delta is chosen by which
    // mapping lands the most LJMP/LCALL targets on instruction boundaries (0 vs -0x4000 for MT62xx).
    private static readonly HashSet<byte> SigOps =
        [0x02, 0x12, 0x22, 0x74, 0x75, 0x90, 0xA3, 0xE0, 0xF0, 0xE5, 0xF5, 0x85, 0xC0, 0xD0];

    private static CodeBankInfo? DetectCodeBank(byte[] data)
    {
        const int win = 0x1000;
        int bestStart = -1; double bestD = 0;
        for (int off = 0; off + win <= data.Length; off += win)
        {
            int sig = 0;
            for (int i = off; i < off + win; i++) if (SigOps.Contains(data[i])) sig++;
            double d = 100.0 * sig / win;
            if (d > bestD) { bestD = d; bestStart = off; }
        }
        if (bestStart < 0 || bestD < 15.0) return null;

        // grow the region left/right while density stays high
        int start = bestStart, end = bestStart + win;
        while (start - win >= 0 && WindowDensity(data, start - win, win) >= 15.0) start -= win;
        while (end + win <= data.Length && WindowDensity(data, end, win) >= 15.0) end += win;

        var (delta, aligned, total) = BestRuntimeDelta(data, start, end);
        return new CodeBankInfo
        {
            Offset = start,
            Length = end - start,
            SignatureDensity = bestD,
            RuntimeDelta = delta,
            TargetsAligned = aligned,
            TargetsTotal = total,
        };
    }

    private static double WindowDensity(byte[] data, int off, int win)
    {
        int sig = 0, end = Math.Min(off + win, data.Length);
        for (int i = off; i < end; i++) if (SigOps.Contains(data[i])) sig++;
        return 100.0 * sig / Math.Max(1, end - off);
    }

    private static (int delta, int aligned, int total) BestRuntimeDelta(byte[] data, int start, int end)
    {
        // instruction-start offsets over [start,end) via a linear length sweep
        var starts = new HashSet<int>();
        int i = start;
        while (i < end) { starts.Add(i); i += OpLen(data[i]); }
        // absolute LJMP/LCALL targets (runtime addresses)
        var targets = new List<int>();
        i = start;
        while (i < end)
        {
            byte op = data[i];
            if ((op == 0x02 || op == 0x12) && i + 2 < end)
                targets.Add((data[i + 1] << 8) | data[i + 2]);
            i += OpLen(op);
        }
        (int d, int a, int t) best = (0, 0, 0);
        foreach (int delta in new[] { 0, -0x4000 })
        {
            int aligned = 0, tot = 0;
            foreach (int tgt in targets)
            {
                // runtime = bankRelativeOffset + delta  =>  bankRel = (tgt - delta) mod 64K.
                // Only targets whose absolute offset lands inside this bank are in scope; the rest
                // point at the un-captured 0x6000-0xBFFF bank and are simply not counted.
                int bankRel = (tgt - delta) & 0xFFFF;
                int absOff = start + bankRel;
                if (absOff < end)
                {
                    tot++;
                    if (starts.Contains(absOff)) aligned++;
                }
            }
            if (aligned > best.a) best = (delta, aligned, tot);
        }
        return best;
    }

    // Compact 8051 instruction length table (bytes per opcode).
    private static int OpLen(byte op)
    {
        switch (op)
        {
            case 0x02: case 0x12: case 0x90: case 0x75: case 0x85: case 0x43: case 0x53:
            case 0x63: case 0xB4: case 0xB5: case 0xB6: case 0xB7: case 0x10: case 0x20:
            case 0x30: case 0xD5:
                return 3;
        }
        if (op is 0x00 or 0x03 or 0x04 or 0x06 or 0x07 or 0x13 or 0x14 or 0x16 or 0x17 or 0x22
            or 0x23 or 0x32 or 0x33 or 0x73 or 0x83 or 0x84 or 0x93 or 0xA3 or 0xA4 or 0xA5
            or 0xB3 or 0xC3 or 0xC4 or 0xC6 or 0xC7 or 0xD3 or 0xD4 or 0xD6 or 0xD7 or 0xE0
            or 0xE2 or 0xE3 or 0xE4 or 0xE6 or 0xE7 or 0xF0 or 0xF2 or 0xF3 or 0xF4 or 0xF6 or 0xF7)
            return 1;
        int hi = op & 0xF0, lo = op & 0x0F;
        if (lo >= 0x08 && hi is 0x00 or 0x10 or 0x20 or 0x30 or 0x40 or 0x50 or 0x60
            or 0x90 or 0xC0 or 0xE0 or 0xF0) return 1;
        if (op is >= 0xB8 and <= 0xBF) return 3;
        if (op is >= 0xD8 and <= 0xDF) return 2;
        return 2;
    }

    // ---- helpers ----
    private static string ToAsciiShadow(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (byte b in data) sb.Append(b is >= 0x20 and < 0x7f ? (char)b : '\n');
        return sb.ToString();
    }

    private static string? Nz(string s) { s = s.Trim(); return s.Length == 0 ? null : s; }

    private static int IndexOf(byte[] hay, byte[] needle, int start, int end = -1)
    {
        if (end < 0) end = hay.Length;
        end = Math.Min(end, hay.Length - needle.Length);
        for (int i = Math.Max(0, start); i <= end; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}

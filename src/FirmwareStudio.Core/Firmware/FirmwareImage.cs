using System.Text;
using FirmwareStudio.Core.Analysis;

namespace FirmwareStudio.Core.Firmware;

/// <summary>One contiguous span of a firmware image classified by what it holds.</summary>
public readonly record struct FirmwareRegion(long Start, long End, double Entropy, double NonZeroPercent, string Kind)
{
    public long Length => End - Start;
}

/// <summary>
/// The result of parsing a PLDS/Lite-On (Plextor PX-8xx) firmware update image (a <c>.1KN</c>/<c>.1JN</c>
/// file — the payload the vendor updater extracts from its password-protected ZIP and flashes as-is). The
/// field offsets are the exact ones the official <c>891SAFPLUSPCDriveUpdater</c> reads (model @0x414/24B,
/// date @0xE25A4/10B, version @size-4/4B), recovered by decompiling it. Read-only characterisation — this is
/// the <b>byte-exact flashable image</b>, but its body is encrypted/compressed by the controller's own scheme
/// (the PC side flashes it verbatim; there is no PC-side decryptor), so it is not a plaintext ROM.
/// </summary>
public sealed class FirmwareImageInfo
{
    public long Size { get; init; }
    /// <summary>True if the file carries the <c>VPD_update_file</c> marker at 0x400 (a PLDS VPD update image).</summary>
    public bool IsVpdUpdateImage { get; init; }
    public string? Magic { get; init; }        // ASCII tag at offset 0 (e.g. "PLEXTORPX891SAFPLUS")
    public string? Model { get; init; }        // 0x414, 24 bytes
    public string? Version { get; init; }      // last 4 bytes
    public string? DateCode { get; init; }     // 0xE2564, 10 bytes (best-effort; model-specific offset)
    public IReadOnlyList<FirmwareRegion> Regions { get; init; } = [];
    public DumpAnalysis Content { get; init; } = null!;
    /// <summary>How the high-entropy body appears to be protected (e.g. ECB-mode block cipher), or null.</summary>
    public string? BodyCipherHint { get; init; }

    /// <summary>Honest one-paragraph account of what the image is and what can/can't be done with it.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        if (IsVpdUpdateImage)
        {
            sb.Append($"PLDS/Lite-On VPD firmware update image");
            if (!string.IsNullOrWhiteSpace(Model)) sb.Append($" for '{Model.Trim()}'");
            if (!string.IsNullOrWhiteSpace(Version)) sb.Append($", version '{Version.Trim()}'");
            if (!string.IsNullOrWhiteSpace(DateCode)) sb.Append($" (build {DateCode.Trim()})");
            sb.Append($", {Size:N0} bytes. ");
        }
        else
        {
            sb.Append($"{Size:N0}-byte image (no VPD_update_file marker — not a recognised PLDS update file). ");
        }

        var body = Regions.Where(r => r.Kind.StartsWith("encrypted", StringComparison.Ordinal)).ToList();
        if (body.Count > 0)
        {
            long enc = body.Sum(r => r.Length);
            sb.Append($"This is the byte-exact flashable payload, but {enc:N0} bytes ({100.0 * enc / Math.Max(1, Size):F0}%) " +
                      $"are high-entropy{(BodyCipherHint is null ? " (encrypted/compressed)" : $" — {BodyCipherHint}")}. " +
                      "The vendor updater flashes this verbatim and the drive decrypts it internally — there is no PC-side " +
                      "decryptor, so this is not a plaintext ROM. A readable image would require the controller key or hardware SoC access.");
        }
        else
        {
            sb.Append(Content.Verdict());
        }
        return sb.ToString();
    }
}

/// <summary>
/// Parses/characterises a PLDS/Lite-On firmware update image. Pure and read-only (no SCSI). Complements
/// <see cref="DumpAnalyzer"/>: where that describes an arbitrary dump, this understands the vendor VPD
/// wrapper and classifies the image into wrapper / encrypted-body / config / padding by Shannon entropy.
/// </summary>
public static class FirmwareImage
{
    private const int BlockSize = 64 * 1024;
    private const int ModelOffset = 0x414;      // updater: Array.Copy(src, 1044, model, 0, 24)
    private const int DateOffset = 927140;      // 0xE25A4 — updater: Array.Copy(src, 927140, date, 0, 10)
    private static readonly byte[] VpdMarker = "VPD_update_file"u8.ToArray();

    public static FirmwareImageInfo Parse(byte[] data)
    {
        bool isVpd = data.Length >= 0x400 + VpdMarker.Length &&
                     data.AsSpan(0x400, VpdMarker.Length).SequenceEqual(VpdMarker);
        string? magic = AsciiAt(data, 0, 19);
        string? model = data.Length >= ModelOffset + 24 ? AsciiAt(data, ModelOffset, 24) : null;
        string? version = data.Length >= 4 ? AsciiAt(data, data.Length - 4, 4) : null;
        string? date = data.Length >= DateOffset + 10 ? AsciiAt(data, DateOffset, 10) : null;
        var regions = MapRegions(data);

        return new FirmwareImageInfo
        {
            Size = data.Length,
            IsVpdUpdateImage = isVpd,
            Magic = Clean(magic),
            Model = Clean(model),
            Version = Clean(version),
            DateCode = isVpd && date is not null && date.Any(char.IsDigit) ? Clean(date) : null,
            Regions = regions,
            BodyCipherHint = DetectBodyCipher(data, regions),
            Content = DumpAnalyzer.Analyze(data, maxStrings: 120),
        };
    }

    /// <summary>
    /// Inspect the high-entropy body for a cipher tell. Truly random data (a good stream/CBC cipher, or
    /// compression) has ~no repeated 16-byte blocks; a large excess of exact 16-byte block duplicates means
    /// identical plaintext blocks map to identical ciphertext — i.e. an <b>ECB-mode block cipher</b> with no
    /// chaining. (This only leaks block equality, never the key — which lives in the controller.)
    /// </summary>
    private static string? DetectBodyCipher(byte[] data, List<FirmwareRegion> regions)
    {
        var body = regions.FirstOrDefault(r => r.Kind.StartsWith("encrypted", StringComparison.Ordinal));
        if (body.Length < 0x10000) return null;

        var seen = new HashSet<(ulong, ulong)>();
        long total = 0, dup = 0;
        for (long p = body.Start; p + 16 <= body.End; p += 16)
        {
            ulong a = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)p, 8));
            ulong b = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)p + 8, 8));
            if (!seen.Add((a, b))) dup++;
            total++;
        }
        // A 128-bit block space makes even one random collision astronomically unlikely, so any real excess
        // (well above 0) is structural. Require a clear margin to avoid false positives on small bodies.
        if (total >= 4096 && dup >= 64)
            return $"ECB-mode block cipher (16-byte blocks; {dup:N0} repeated ciphertext blocks reveal no chaining) — key resident in the controller";
        return null;
    }

    /// <summary>Classify each 64 KiB block by entropy/position, then merge consecutive same-kind blocks.</summary>
    private static List<FirmwareRegion> MapRegions(byte[] data)
    {
        var merged = new List<FirmwareRegion>();
        for (int off = 0; off < data.Length; off += BlockSize)
        {
            int end = Math.Min(off + BlockSize, data.Length);
            var span = data.AsSpan(off, end - off);
            double h = Entropy(span);
            long nz = 0;
            foreach (byte b in span) if (b != 0) nz++;
            double nzPct = 100.0 * nz / span.Length;
            string kind = Classify(off, h, nzPct);

            if (merged.Count > 0 && merged[^1].Kind == kind)
                merged[^1] = merged[^1] with { End = end };   // extend
            else
                merged.Add(new FirmwareRegion(off, end, h, nzPct, kind));
        }
        // Recompute entropy/non-zero across each merged span for an accurate summary.
        for (int i = 0; i < merged.Count; i++)
        {
            var r = merged[i];
            var span = data.AsSpan((int)r.Start, (int)r.Length);
            long nz = 0;
            foreach (byte b in span) if (b != 0) nz++;
            merged[i] = r with { Entropy = Entropy(span), NonZeroPercent = 100.0 * nz / span.Length };
        }
        return merged;
    }

    private static string Classify(int offset, double entropy, double nonZeroPct)
    {
        if (offset < 0x10000) return "header / VPD wrapper";
        if (entropy >= 7.5) return "encrypted / compressed body";
        if (entropy <= 1.0 || nonZeroPct < 2) return "padding / fill";
        return "config / tables";
    }

    private static double Entropy(ReadOnlySpan<byte> d)
    {
        if (d.Length == 0) return 0;
        Span<int> freq = stackalloc int[256];
        foreach (byte b in d) freq[b]++;
        double h = 0, n = d.Length;
        foreach (int f in freq)
        {
            if (f == 0) continue;
            double p = f / n;
            h -= p * Math.Log2(p);
        }
        return h;
    }

    private static string? AsciiAt(byte[] data, int offset, int len)
    {
        if (offset < 0 || offset + len > data.Length) return null;
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte b = data[offset + i];
            sb.Append(b is >= 0x20 and < 0x7f ? (char)b : (b == 0 ? ' ' : '.'));
        }
        return sb.ToString();
    }

    private static string? Clean(string? s)
    {
        if (s is null) return null;
        string t = s.Trim();
        return t.Length == 0 ? null : t;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start, int end)
    {
        end = Math.Min(end, haystack.Length - needle.Length);
        for (int i = Math.Max(0, start); i <= end; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}

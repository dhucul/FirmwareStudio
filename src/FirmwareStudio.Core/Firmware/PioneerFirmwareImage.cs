using System.Security.Cryptography;
using System.Text;

namespace FirmwareStudio.Core.Firmware;

/// <summary>
/// One Pioneer "microcode" image (a kernel or normal part) parsed from a firmware updater. It is a 512-byte
/// plaintext ASCII header (newline-delimited <c>Key : Value</c> lines) followed by a body the drive decodes
/// internally. Read-only characterisation — the body is not decrypted here (the updater ships it verbatim via
/// SCSI WRITE BUFFER mode 7; there is no PC-side decryptor). See <c>docs/pioneer-firmware-update-protocol.md</c>.
/// </summary>
public sealed class PioneerImagePart
{
    public string Source { get; init; } = "";           // "resource BINARY/131 lang=0x411" or "(raw image)"
    public string? ResourceType { get; init; }           // "BINARY", or null for a raw file
    public int ResourceId { get; init; }                 // 131 = kernel, 132 = normal
    public bool SignatureValid { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
    public string? PartLabel { get; init; }              // e.g. "S9201430.105"
    public int PartSelector { get; init; } = -1;         // 0 = Kernel, 1 = Normal, -1 = unknown
    public int VersionMajor { get; init; }               // ASCII digit @ +0x90
    public int VersionMinor { get; init; }               // BCD of ASCII digits @ +0x92/+0x93
    public long BodyOffset { get; init; } = 0x200;
    public long BodyLength { get; init; }
    public double BodyEntropy { get; init; }
    public string Sha256 { get; init; } = "";
    public long TotalSize { get; init; }

    public string? Id => Field("ID");
    public string? RevisionLevel => Field("Revision Level");
    public string? HardwareVersion => Field("Hardware Version");
    public string? FileType => Field("File Type");        // "Kernel" | "Normal"
    public string? GeneratedDate => Field("Generated Date");

    public string PartName => FileType ?? PartSelector switch { 0 => "Kernel", 1 => "Normal", _ => "Unknown" };
    public string Version => RevisionLevel ?? $"{VersionMajor}.{VersionMinor:X2}";

    private string? Field(string key) => Fields.TryGetValue(key, out var v) ? v : null;
}

/// <summary>The result of inspecting a Pioneer firmware updater (or a single extracted image).</summary>
public sealed class PioneerUpdateInfo
{
    /// <summary>How the input was recognised: "raw microcode image", "Updater.exe (PE)", or "WinRAR SFX".</summary>
    public string DetectedKind { get; init; } = "";
    public IReadOnlyList<PioneerImagePart> Parts { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>Honest one-paragraph account of what the file is.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        if (Parts.Count == 0)
        {
            sb.Append("No Pioneer microcode image found in this file.");
            return sb.ToString();
        }
        sb.Append($"Pioneer firmware update ({DetectedKind})");
        var id = Parts[0].Id;
        if (!string.IsNullOrWhiteSpace(id)) sb.Append($" for '{id.Trim()}'");
        sb.Append($", version {Parts[0].Version} — {Parts.Count} part(s): ");
        sb.Append(string.Join(", ", Parts.Select(p => $"{p.PartName} ({p.BodyLength:N0} B body, H={p.BodyEntropy:F2})")));
        sb.Append(". The header is plaintext but each part's body is decoded inside the drive (the updater flashes " +
                  "it verbatim via SCSI WRITE BUFFER mode 7); there is no PC-side decryptor, so this is not a plaintext ROM.");
        return sb.ToString();
    }
}

/// <summary>
/// Parses a Pioneer firmware updater and its embedded microcode images. Accepts the outer WinRAR-SFX
/// updater <c>.exe</c> (unwrapped to the inner <c>Updater.exe</c>), the inner <c>Updater.exe</c> (a PE whose
/// custom <c>"BINARY"</c> resources 131/132 are the images), or an already-extracted raw <c>.bin</c>. Pure and
/// read-only (no SCSI). C# port of the standalone PioneerFwTool.
/// </summary>
public static class PioneerFirmwareImage
{
    // "********  Copyright(c) 2000 Pioneer" — the signature that begins every microcode image.
    private static readonly byte[] Signature = "********  Copyright(c) 2000 Pioneer"u8.ToArray();
    private const int HeaderSize = 0x200;
    private const int LabelOffset = 0x1F0;

    /// <summary>True if <paramref name="data"/> begins with the Pioneer microcode signature.</summary>
    public static bool LooksLikeImage(byte[] data)
        => data.Length >= Signature.Length && data.AsSpan(0, Signature.Length).SequenceEqual(Signature);

    /// <summary>Cheap-ish detector for <see cref="FirmwareFile.Identify"/>: raw image, PE with BINARY images, or a Pioneer SFX.</summary>
    public static bool Looks(byte[] data)
    {
        if (LooksLikeImage(data)) return true;
        if (!PeResourceReader.IsPe(data)) return false;
        if (HasPioneerBinaryResource(data)) return true;
        // Only pay for the SFX inflate when the file actually looks like a WinRAR SFX.
        if (ZipSfxExtractor.LooksLikeWinRarSfx(data))
        {
            var inner = ZipSfxExtractor.ExtractInnerExe(data);
            if (inner is not null && HasPioneerBinaryResource(inner)) return true;
        }
        return false;
    }

    public static PioneerUpdateInfo Parse(byte[] data)
    {
        var diag = new List<string>();

        // 1) Raw image.
        if (LooksLikeImage(data))
        {
            diag.Add("Detected: raw Pioneer microcode image");
            return new PioneerUpdateInfo
            {
                DetectedKind = "raw microcode image",
                Diagnostics = diag,
                Parts = [ParsePart(data, "(raw image)", null, 0, 0)],
            };
        }

        if (!PeResourceReader.IsPe(data))
        {
            diag.Add("Input is neither a Pioneer microcode image nor a PE file.");
            return new PioneerUpdateInfo { DetectedKind = "unrecognised", Diagnostics = diag };
        }

        // 2) PE — walk resources; if none are Pioneer, try unwrapping a WinRAR SFX first.
        string kind = "Updater.exe (PE)";
        byte[] pe = data;
        var res = PeResourceReader.Read(pe);
        if (!res.Any(r => LooksLikeImage(r.Data)))
        {
            diag.Add("Detected: PE with no firmware resources — probing for a WinRAR-SFX inner .exe ...");
            var inner = ZipSfxExtractor.ExtractInnerExe(data);
            if (inner is not null)
            {
                diag.Add($"        unwrapped inner PE ({inner.Length:N0} bytes)");
                kind = "WinRAR SFX";
                pe = inner;
                res = PeResourceReader.Read(pe);
            }
        }

        int pioneer = res.Count(r => LooksLikeImage(r.Data));
        diag.Add($"Detected: PE image — {res.Count} resource(s), {pioneer} Pioneer firmware image(s)");
        foreach (var g in res.GroupBy(r => r.Type).OrderBy(g => g.Key))
            diag.Add($"        type '{g.Key}': {g.Count()} resource(s)");

        var parts = new List<PioneerImagePart>();
        foreach (var r in res)
        {
            if (!LooksLikeImage(r.Data)) continue;
            string src = $"resource {r.Type}/{r.Name} lang=0x{r.Lang:X}";
            parts.Add(ParsePart(r.Data, src, r.Type, int.TryParse(r.Name, out int id) ? id : 0, r.Lang));
        }

        return new PioneerUpdateInfo { DetectedKind = kind, Diagnostics = diag, Parts = parts };
    }

    private static bool HasPioneerBinaryResource(byte[] pe)
        => PeResourceReader.Read(pe).Any(r => LooksLikeImage(r.Data));

    private static PioneerImagePart ParsePart(byte[] raw, string source, string? resType, int resId, int resLang)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ASCII "Key : Value" lines within the header, up to the Ctrl-Z (0x1A) EOF marker.
        int limit = Math.Min(raw.Length, HeaderSize);
        int textEnd = Array.IndexOf(raw, (byte)0x1A, 0, limit);
        if (textEnd < 0) textEnd = limit;
        string text = Encoding.Latin1.GetString(raw, 0, textEnd);
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Replace("\r", "").Replace('\0', ' ').Trim();
            int sep = line.IndexOf(" : ", StringComparison.Ordinal);
            if (sep <= 0) continue;
            string key = line[..sep].Trim();
            string val = line[(sep + 3)..].Trim();
            if (key.Length > 0) fields.TryAdd(key, val);
        }

        // Part label at 0x1F0 ("S920143{n}.105").
        string? label = null;
        int selector = -1;
        if (raw.Length >= LabelOffset + 16)
        {
            string s = Encoding.Latin1.GetString(raw, LabelOffset, 16).TrimEnd('\0', ' ');
            if (s.StartsWith('S'))
            {
                label = s;
                if (s.Length >= 8 && char.IsDigit(s[7])) selector = s[7] - '0';
            }
        }

        int major = raw.Length > 0x90 ? Digit(raw[0x90]) : 0;
        int minor = raw.Length > 0x93 ? (Digit(raw[0x92]) << 4) | Digit(raw[0x93]) : 0;
        long bodyLen = raw.Length > HeaderSize ? raw.Length - HeaderSize : 0;

        return new PioneerImagePart
        {
            Source = source,
            ResourceType = resType,
            ResourceId = resId,
            SignatureValid = LooksLikeImage(raw),
            Fields = fields,
            PartLabel = label,
            PartSelector = selector,
            VersionMajor = major,
            VersionMinor = minor,
            BodyOffset = HeaderSize,
            BodyLength = bodyLen,
            BodyEntropy = Entropy(raw.AsSpan(HeaderSize)),
            Sha256 = Convert.ToHexString(SHA256.HashData(raw)),
            TotalSize = raw.Length,
        };
    }

    private static int Digit(byte b) => b is >= (byte)'0' and <= (byte)'9' ? b - '0' : 0;

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
}

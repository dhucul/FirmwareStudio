using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
namespace FirmwareStudio.Core.Firmware;

public sealed record FirmwareSection(string Label, uint? Address, byte[] Data, FirmwareFileAnalysis Analysis);
public sealed record CompositeFirmwareInfo(IReadOnlyList<FirmwareSection> Sections)
{
    public FirmwareSection? Primary => Sections.FirstOrDefault(s => s.Label.StartsWith("PRIMARY @", StringComparison.Ordinal) && s.Address == 0);
    public string Describe() => $"FirmwareStudio composite capture: {Sections.Count} separately addressed region(s). " +
        "Container headers are excluded from payload analysis.\n" +
        string.Join("\n", Sections.Select(s => $"{s.Label}: {s.Data.Length:N0} bytes ({s.Analysis.Kind})"));
}
internal static class CompositeFirmwareImage
{
    public static bool Looks(byte[] data) => data.AsSpan().StartsWith("FirmwareStudio 0xF1 cache composite dump"u8) ||
        data.AsSpan().StartsWith("FirmwareStudio PLDS 0xDF composite vendor dump"u8);
    public static CompositeFirmwareInfo Parse(byte[] data, CancellationToken ct)
    {
        int end = data.AsSpan(0, Math.Min(512, data.Length)).IndexOf("\r\n\r\n"u8);
        if (end < 0) throw new InvalidDataException("Incomplete composite file header.");
        var declared = Regex.Match(Encoding.ASCII.GetString(data, 0, end), @"(\d+) region\(s\) \[v2\]");
        if (!declared.Success || !int.TryParse(declared.Groups[1].Value, out int expected) || expected is < 1 or > 129)
            throw new InvalidDataException("Invalid composite region count or version.");
        int position = end + 4;
        var sections = new List<FirmwareSection>();
        while (position < data.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (sections.Count >= expected || data.Length - position < 128) throw new InvalidDataException("Incomplete or unexpected composite section.");
            var match = Regex.Match(Encoding.ASCII.GetString(data, position, 128), @"^=== (.+?) ===  size=(\d+)\b");
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out int size) || size < 0 || size > data.Length - position - 128)
                throw new InvalidDataException("Invalid composite payload length.");
            string label = match.Groups[1].Value;
            uint? address = null;
            var addressMatch = Regex.Match(label, @"^(?:PRIMARY|REGION) @0x([0-9A-Fa-f]{8})$");
            if (addressMatch.Success) address = uint.Parse(addressMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte[] payload = data.AsSpan(position + 128, size).ToArray();
            if (Looks(payload)) throw new InvalidDataException("Nested composite captures are not supported.");
            sections.Add(new FirmwareSection(label, address, payload, FirmwareFile.Analyze(payload, ct)));
            position += 128 + size;
        }
        if (sections.Count != expected) throw new InvalidDataException("Composite region count does not match its payloads.");
        return new CompositeFirmwareInfo(sections);
    }
}

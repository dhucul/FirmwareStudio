using FirmwareStudio.Core.Analysis;
namespace FirmwareStudio.Core.Firmware;

public enum FirmwareFileKind { Unknown, VpdImage, ControllerRam, PioneerUpdate, Composite }
public sealed record FirmwareFileAnalysis(FirmwareFileKind Kind, PioneerUpdateInfo? Pioneer = null,
    OpticalRamImageInfo? ControllerRam = null, FirmwareImageInfo? Vpd = null, CompositeFirmwareInfo? Composite = null);

public static class FirmwareFile
{
    public const int MaxInputBytes = 128 * 1024 * 1024;
    public static FirmwareFileKind Identify(byte[] data) => Analyze(data).Kind;
    public static byte[] ReadFile(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxInputBytes) throw new InvalidDataException("Firmware input exceeds the 128 MiB limit.");
        var bytes = new byte[(int)stream.Length];
        int position = 0;
        while (position < bytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            int read = stream.Read(bytes, position, Math.Min(81920, bytes.Length - position));
            if (read == 0) throw new EndOfStreamException("Firmware input changed while being read.");
            position += read;
        }
        ct.ThrowIfCancellationRequested();
        if (stream.ReadByte() != -1) throw new InvalidDataException("Firmware input changed while being read.");
        return bytes;
    }
    public static FirmwareFileAnalysis Analyze(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ct.ThrowIfCancellationRequested();
        if (data.Length > MaxInputBytes) throw new InvalidDataException("Firmware input exceeds the 128 MiB limit.");
        if (CompositeFirmwareImage.Looks(data)) return new(FirmwareFileKind.Composite, Composite: CompositeFirmwareImage.Parse(data, ct));
        var pioneer = PioneerFirmwareImage.Parse(data, ct);
        if (pioneer.Parts.Count > 0) return new(FirmwareFileKind.PioneerUpdate, Pioneer: pioneer);
        if (FirmwareImage.HasVpdMarker(data)) return new(FirmwareFileKind.VpdImage, Vpd: FirmwareImage.Parse(data, ct));
        if (OpticalRamImage.Looks(data)) return new(FirmwareFileKind.ControllerRam, ControllerRam: OpticalRamImage.Parse(data, ct));
        return new(FirmwareFileKind.Unknown, Vpd: FirmwareImage.Parse(data, ct));
    }
}

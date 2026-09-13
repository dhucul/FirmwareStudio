namespace FirmwareStudio.Core.Models;

public enum ExtractionStatus { Complete, Partial, Unsupported, Failed }

/// <summary>Complete means the requested address range was read, not that a flashable ROM was verified.</summary>
public sealed class ExtractionResult
{
    public required ExtractionStatus Status { get; init; }
    public bool Success => Status is ExtractionStatus.Complete or ExtractionStatus.Partial;
    public bool IsComplete => Status == ExtractionStatus.Complete;
    public required string MethodId { get; init; }
    public required string MethodName { get; init; }
    public byte[]? Firmware { get; init; }
    public string DataLabel { get; init; } = "captured bytes";
    public string? Reason { get; init; }
    public required string Summary { get; init; }
    public int ByteCount => Firmware?.Length ?? 0;
    public bool HasInformativeBytes => Firmware?.Any(b => b is not (0x00 or 0xFF)) == true;

    public static ExtractionResult Ok(string id, string name, byte[] data, string label, string summary)
        => new() { Status = ExtractionStatus.Complete, MethodId = id, MethodName = name,
            Firmware = data, DataLabel = label, Summary = summary };
    public static ExtractionResult Partial(string id, string name, byte[] data, string label, string reason)
        => new() { Status = ExtractionStatus.Partial, MethodId = id, MethodName = name,
            Firmware = data, DataLabel = label, Reason = reason, Summary = "Partial capture: " + reason };
    public static ExtractionResult Unsupported(string id, string name, string reason)
        => new() { Status = ExtractionStatus.Unsupported, MethodId = id, MethodName = name,
            Reason = reason, Summary = reason };
    public static ExtractionResult Failed(string id, string name, string error)
        => new() { Status = ExtractionStatus.Failed, MethodId = id, MethodName = name,
            Reason = error, Summary = "Failed: " + error };
}

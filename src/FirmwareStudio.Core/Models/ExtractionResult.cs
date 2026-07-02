namespace FirmwareStudio.Core.Models;

/// <summary>Outcome of running one extraction method against a drive.</summary>
public sealed class ExtractionResult
{
    public required bool Success { get; init; }
    public required string MethodId { get; init; }
    public required string MethodName { get; init; }

    /// <summary>The dumped bytes on success; null otherwise.</summary>
    public byte[]? Firmware { get; init; }

    /// <summary>Honest description of what the bytes actually are (never overclaims "flash ROM").</summary>
    public string DataLabel { get; init; } = "firmware image";

    /// <summary>Why the method did not produce a dump (unsupported / expected failure / error).</summary>
    public string? Reason { get; init; }

    public required string Summary { get; init; }

    public int ByteCount => Firmware?.Length ?? 0;

    public static ExtractionResult Ok(string id, string name, byte[] data, string label, string summary)
        => new() { Success = true, MethodId = id, MethodName = name, Firmware = data, DataLabel = label, Summary = summary };

    public static ExtractionResult Unsupported(string id, string name, string reason)
        => new() { Success = false, MethodId = id, MethodName = name, Reason = reason, Summary = reason };

    public static ExtractionResult Failed(string id, string name, string error)
        => new() { Success = false, MethodId = id, MethodName = name, Reason = error, Summary = $"Failed: {error}" };
}

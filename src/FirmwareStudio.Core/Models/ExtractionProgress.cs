namespace FirmwareStudio.Core.Models;

/// <summary>Streamed during an extraction run: overall percent, the current stage, and an optional log line.</summary>
public sealed record ExtractionProgress(int Percent, string? Stage = null, string? LogLine = null);

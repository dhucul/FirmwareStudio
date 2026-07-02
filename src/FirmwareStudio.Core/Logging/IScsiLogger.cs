namespace FirmwareStudio.Core.Logging;

/// <summary>
/// Sink for the audit trail. Every SCSI command that <c>ScsiDevice</c> issues is reported here before it
/// returns, so "every command is logged" is structural, not a matter of discipline at call sites.
/// </summary>
public interface IScsiLogger
{
    void OnCommand(CommandLogEntry entry);
    void Info(string message);
}

/// <summary>A logger that discards everything — handy for tests/probing where no audit trail is wanted.</summary>
public sealed class NullScsiLogger : IScsiLogger
{
    public static readonly NullScsiLogger Instance = new();
    public void OnCommand(CommandLogEntry entry) { }
    public void Info(string message) { }
}

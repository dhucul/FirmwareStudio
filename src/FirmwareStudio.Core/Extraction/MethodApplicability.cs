namespace FirmwareStudio.Core.Extraction;

public enum Applicability { Applicable, Maybe, NotApplicable }

/// <summary>Whether a method suits the current drive, with a one-line reason for the UI.</summary>
public sealed record MethodApplicability(Applicability Level, string Reason)
{
    public static MethodApplicability Yes(string reason) => new(Applicability.Applicable, reason);
    public static MethodApplicability Perhaps(string reason) => new(Applicability.Maybe, reason);
    public static MethodApplicability No(string reason) => new(Applicability.NotApplicable, reason);
}

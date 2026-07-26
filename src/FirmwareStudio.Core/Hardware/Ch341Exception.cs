namespace FirmwareStudio.Core.Hardware;

/// <summary>A CH341 adapter / SPI-flash operation failed, with a user-actionable message.</summary>
public sealed class Ch341Exception : Exception
{
    public Ch341Exception(string message) : base(message) { }
    public Ch341Exception(string message, Exception innerException) : base(message, innerException) { }
}

namespace FirmwareStudio.Core.Hardware;

public interface ISpiDevice
{
    byte[] SpiTransfer(byte[] data);
}

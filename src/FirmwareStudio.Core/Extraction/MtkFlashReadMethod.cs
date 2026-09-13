using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Extraction;

/// <summary>
/// Method — MediaTek / Lite-On flash read via READ BUFFER (0x3C) in the download-microcode-with-offsets
/// addressing mode (mode 6). This is the region the official Lite-On/MediaTek firmware updater talks to,
/// and the exact read redumper's MT1959 flasher issues to fingerprint a drive before flashing
/// (<c>drive/flash_mt1959.ixx</c>: <c>cmd_read_buffer(..., DOWNLOAD_MICROCODE_WITH_OFFSETS, ...)</c>).
///
/// <para>Unlike the two software methods that come back empty on Lite-On iHAS/iHBS drives — the 0xF1 cache
/// read (controller DRAM <i>disc</i>-cache, empty when idle) and the plain READ BUFFER "data" mode (the
/// controller's scratch buffer) — mode 6 addresses the firmware <i>flash</i> itself, so it can surface the
/// resident firmware/bootloader on MediaTek drives.</para>
///
/// <para><b>Strictly read-only.</b> Opcode 0x3C (READ BUFFER) is data-in; direction is fixed by the opcode,
/// not the mode byte, so this cannot write. The microcode <i>download/save</i> modes that flash a drive live
/// only under WRITE BUFFER (0x3B), for which this tool has no builder. redumper issues this same command as
/// a pre-flash read with no side effects.</para>
///
/// <para>Output is labelled as the controller's flash region — likely the firmware, but never guaranteed
/// byte-exact against a hardware programmer.</para>
/// </summary>
public sealed class MtkFlashReadMethod : IFirmwareExtractionMethod
{
    private const byte FlashBufferId = 0x00;   // redumper reads the microcode region with buffer id 0
    private const int ChunkSize = 0x4000;      // 16 KiB per READ BUFFER — matches redumper's MT flash block granularity
    private const int MaxSize = 0x400000; // bounded raw capture window

    public string Id => "mtk-flash";
    public string DisplayName => "MediaTek/Lite-On flash read (READ BUFFER 0x3C mode 6)";
    public string Description =>
        "Reads the MediaTek controller's firmware flash with READ BUFFER (0x3C) in the " +
        "download-microcode-with-offsets addressing mode (mode 6) — the region the official Lite-On/MediaTek " +
        "firmware updater accesses. Read-only: 0x3C is data-in; the write/save microcode modes are WRITE " +
        "BUFFER (0x3B) and are never issued. Walks the flash from offset 0 and returns the controller flash " +
        "contents — likely the resident firmware, but not a guaranteed byte-exact ROM.";

    public MethodApplicability Evaluate(DriveIdentity id, ChipsetInfo chipset)
    {
        // The newer MediaTek MT62xx generation (Plextor/PLDS PX-8xx, e.g. PX-891SAF) does NOT implement the
        // READ BUFFER microcode-offset read — it gates firmware behind the 0xDF vendor command instead
        // (confirmed: mode 6 returns ILLEGAL REQUEST on a PX-891SAF PLUS). Advertise it as only "maybe" so the
        // UI sets expectations up front, while still letting the user try it.
        if (IsMt62xxGeneration(id, chipset))
            return MethodApplicability.Perhaps(
                "Newer MediaTek MT62xx (Plextor/PLDS PX-8xx): this generation rejects the READ BUFFER " +
                "microcode-offset read and does not expose firmware through mode 6. The " +
                "PLDS/Lite-On 0xDF method can inspect registers, not a verified ROM.");

        return chipset.Family switch
        {
            ChipsetFamily.MediaTek => MethodApplicability.Yes(
                "MediaTek chipset — READ BUFFER microcode-offset mode is the Lite-On/MediaTek firmware-flash read path."),
            ChipsetFamily.Unknown => MethodApplicability.Perhaps(
                "Chipset unknown; the READ BUFFER microcode-offset read applies only to MediaTek firmware-download drives."),
            _ => MethodApplicability.No(
                $"Detected {chipset.Family}; READ BUFFER microcode-offset mode targets MediaTek (Lite-On) drives."),
        };
    }

    /// <summary>
    /// Recognises the MediaTek MT62xx generation (Plextor/PLDS PX-8xx) that gates firmware behind 0xDF and
    /// rejects mode 6. Matches the chipset name the detector assigns ("…MT62…") or a Plextor PX-8xx model.
    /// </summary>
    private static bool IsMt62xxGeneration(DriveIdentity id, ChipsetInfo chipset)
        => chipset.Name.Contains("MT62", StringComparison.OrdinalIgnoreCase)
           || (id.Vendor.StartsWith("PLEXTOR", StringComparison.OrdinalIgnoreCase)
               && id.Model.StartsWith("PX-8", StringComparison.OrdinalIgnoreCase));

    public ExtractionResult Extract(IScsiDevice device, DriveIdentity id, ChipsetInfo chipset,
        IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        device = device.WithCancellation(ct);
        using var output = new MemoryStream();
        string stop = "read the configured 4 MiB address window";
        progress.Report(new ExtractionProgress(0, "Reading raw MediaTek flash window"));
        while (output.Length < MaxSize)
        {
            int length = (int)Math.Min(ChunkSize, MaxSize - output.Length);
            var r = device.SendCommand(ScsiCommand.ReadBufferMicrocode(FlashBufferId, (int)output.Length, length),
                ScsiDirection.In, new byte[length], note: $"MTK flash offset=0x{output.Length:X} length={length}");
            if (!r.Good || !r.ValidTransferLength || r.TransferredLength == 0 || r.Data is null)
            {
                stop = !r.Good ? r.StatusText : !r.ValidTransferLength
                    ? $"invalid transfer length {r.TransferredLength}/{length}" : "zero-byte transfer";
                if (output.Length == 0)
                    return r.DeviceIoOk && r.SenseInfo.IllegalRequest
                        ? ExtractionResult.Unsupported(Id, DisplayName, stop)
                        : ExtractionResult.Failed(Id, DisplayName, stop);
                break;
            }
            output.Write(r.Data, 0, r.TransferredLength);
            progress.Report(new ExtractionProgress((int)(100 * output.Length / MaxSize)));
        }
        ct.ThrowIfCancellationRequested();
        byte[] data = output.ToArray();
        string summary = $"Captured raw addresses 0x000000..0x{data.Length:X6} ({data.Length:N0} bytes); {stop}. " +
            "Zero-filled and erased spans are preserved; this is not a verified full ROM.";
        const string label = "raw MediaTek controller flash window";
        return data.Length == MaxSize
            ? ExtractionResult.Ok(Id, DisplayName, data, label, summary)
            : ExtractionResult.Partial(Id, DisplayName, data, label, summary);
    }
}

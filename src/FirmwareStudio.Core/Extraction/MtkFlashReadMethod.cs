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
    // Backstop only — real reads end far earlier via error / zero-read / erased-0xFF run. 4 MiB clears any
    // optical-drive firmware image (largest known are ~2 MiB Blu-ray) and still fits the 24-bit offset field.
    private const int MaxSize = 0x400000;
    // Bail if this many bytes hold no firmware-like data. A real MediaTek image carries code/ID within the
    // first few KiB (redumper reads the drive ID at flash 0x3000), so 256 KiB of pure 0x00/0xFF reliably
    // means "no firmware exposed here" while leaving generous headroom for any blank/erased leading region.
    private const int EmptyExitThreshold = 0x40000;
    private const int EndFfRun = 0x10000;      // 64 KiB of 0xFF ⇒ past the image (erase-block granularity)

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
                "microcode-offset read and gates firmware behind the 0xDF vendor command — try it, but the " +
                "PLDS/Lite-On 0xDF method is the likelier path.");

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

    public ExtractionResult Extract(ScsiDevice device, DriveIdentity id, ChipsetInfo chipset,
        IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        progress.Report(new ExtractionProgress(0, "MediaTek flash read (READ BUFFER 0x3C mode 6)"));

        using var ms = new MemoryStream();
        long meaningful = 0;   // bytes that are neither 0x00 (empty) nor 0xFF (erased) — i.e. firmware-like
        int ffRun = 0;         // trailing run of 0xFF, to detect the erased tail past the firmware image
        int offset = 0;
        string stop = $"reached the {MaxSize / 1024} KiB safety cap";

        while (offset < MaxSize)
        {
            ct.ThrowIfCancellationRequested();

            int len = Math.Min(ChunkSize, MaxSize - offset);
            var r = device.SendCommand(ScsiCommand.ReadBufferMicrocode(FlashBufferId, offset, len),
                ScsiDirection.In, new byte[len],
                note: $"MTK flash read (0x3C/6) off=0x{offset:X} len=0x{len:X}");

            // The first read doubles as the support probe. ILLEGAL REQUEST (key 0x05) = the drive does not
            // implement the microcode-offset read; anything else means it understood the command.
            if (offset == 0)
            {
                var s = r.SenseInfo;
                if (r.DeviceIoOk && r.ScsiStatus != 0x00 && s.Key == 0x05)
                    return ExtractionResult.Unsupported(Id, DisplayName,
                        $"The drive rejected READ BUFFER microcode-offset mode (ILLEGAL REQUEST, asc={s.Asc:X2}/{s.Ascq:X2}). " +
                        "It does not expose the MediaTek/Lite-On firmware-download buffer over software; a hardware programmer may be required.");
                if (!r.Good || r.Data is null)
                    return ExtractionResult.Unsupported(Id, DisplayName,
                        $"READ BUFFER microcode-offset mode did not return data ({r.StatusText}).");
            }

            if (!r.Good || r.Data is null)
            {
                stop = $"drive stopped responding at 0x{offset:X} ({r.StatusText})";
                break;
            }

            // Trust the transferred length — Windows SPTI reports it accurately — and only fall back to the
            // full buffer if the reported value is out of range. A GOOD read of zero bytes means the readable
            // flash is exhausted, so stop rather than pad the dump with zeros. A short-but-non-zero transfer
            // is NOT end-of-data (some controllers cap a single READ BUFFER below the requested size), so keep
            // reading from offset+got instead of truncating.
            int got = r.TransferredLength;
            if (got < 0 || got > len) got = len;
            if (got == 0)
            {
                stop = $"empty read at 0x{offset:X} (end of readable flash)";
                break;
            }

            ms.Write(r.Data, 0, got);
            for (int i = 0; i < got; i++)
            {
                byte b = r.Data[i];
                if (b == 0xFF) ffRun++;
                else { ffRun = 0; if (b != 0x00) meaningful++; }
            }
            offset += got;
            progress.Report(new ExtractionProgress((int)(100L * offset / MaxSize)));

            if (meaningful == 0 && offset >= EmptyExitThreshold)
            {
                stop = $"no firmware-like data in the first {offset / 1024} KiB";
                break;
            }
            if (ffRun >= EndFfRun && ms.Length > ffRun)
            {
                stop = $"reached erased flash (0xFF run) at 0x{offset - ffRun:X}";
                break;
            }
        }

        byte[] raw = ms.ToArray();

        if (meaningful == 0)
            return ExtractionResult.Unsupported(Id, DisplayName, raw.Length == 0
                ? $"READ BUFFER microcode-offset mode was accepted but returned no data ({stop}). This drive does " +
                  "not expose firmware through this buffer; a hardware programmer may be required."
                : $"READ BUFFER microcode-offset mode is accepted, but the {raw.Length:N0} bytes read were all " +
                  "0x00 (empty) or 0xFF (erased) — this drive does not expose firmware through this buffer. " +
                  "A hardware programmer may be required for a true ROM dump.");

        // Trim the trailing erased-flash (0xFF) region so the dump ends at the last real byte. Only 0xFF (the
        // definitive erased state) is trimmed — 0x00 could be legitimate zero-filled image data — and the
        // amount removed is reported for transparency.
        int end = raw.Length;
        while (end > 0 && raw[end - 1] == 0xFF) end--;
        byte[] data = end == raw.Length ? raw : raw[..end];
        int trimmed = raw.Length - end;
        string trimNote = trimmed > 0 ? $"; trimmed {trimmed:N0} B trailing 0xFF (erased)" : "";

        return ExtractionResult.Ok(Id, DisplayName, data,
            "MediaTek controller flash region via READ BUFFER 0x3C mode 6 (likely resident firmware/bootloader; not a verified byte-exact ROM)",
            $"Read {data.Length:N0} bytes from the MediaTek flash via READ BUFFER 0x3C mode 6 " +
            $"({100.0 * meaningful / Math.Max(1, data.Length):F1}% firmware-like, non-0x00/0xFF); {stop}{trimNote}.");
    }
}

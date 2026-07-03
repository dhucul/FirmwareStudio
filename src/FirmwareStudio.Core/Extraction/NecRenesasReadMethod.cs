using System.Linq;
using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Extraction;

/// <summary>
/// Method — Renesas/NEC-family firmware read via the vendor <c>ReadRAM</c> (opcode 0xCC) and <c>ReadBoot</c>
/// (0xCD) commands, ported from <b>binflash</b> (Liggy/binflash). Covers NEC ND-*, Optiarc AD-* (incl.
/// Blu-ray/HD-DVD) and NEC-based Lite-On iHAS*/DH24A* drives — the family FirmwareStudio's other methods
/// (all MediaTek) do not touch.
///
/// <para>Flow: probe with 0xCD; read the 4-byte firmware signatures at the known addresses via 0xCC to
/// identify the drive (<see cref="NecDriveTable"/>); then dump the model's flash region(s) in 4 KiB blocks
/// with 0xCC. Unlike MediaTek drives, no unlock is needed for reading.</para>
///
/// <para><b>Read-only.</b> Only the data-in reads 0xCC/0xCD are issued, and 0xCC only ever with the bank bit
/// in byte 1 — never the 0x01/0x04/0x06 sub-modes (SetSafeMode/erase) or the 0xCB WriteRAM opcode. binflash
/// enters "safe mode" (a 0xCC state-change) to read some newer drives' full flash; this tool deliberately
/// does not, and reports honestly when a region can't be read without it.</para>
/// </summary>
public sealed class NecRenesasReadMethod : IFirmwareExtractionMethod
{
    private const int BlockSize = 0x1000;   // binflash DUMP_BLOCK_SIZE (4 KiB)

    public string Id => "nec";
    public string DisplayName => "NEC/Renesas RAM read (0xCC, binflash)";
    public string Description =>
        "Reads firmware from Renesas/NEC-family drives (NEC ND-*, Optiarc AD-*, NEC-based Lite-On iHAS) via " +
        "the binflash vendor commands ReadRAM (0xCC) and ReadBoot (0xCD). Identifies the drive from its " +
        "firmware signatures, then dumps the model's flash range(s). Read-only — no unlock, no write/erase, " +
        "and it never issues the 'safe mode' state change some newer drives need for a full dump.";

    public MethodApplicability Evaluate(DriveIdentity id, ChipsetInfo chipset) => chipset.Family switch
    {
        ChipsetFamily.Renesas => MethodApplicability.Yes(
            "Renesas/NEC chipset — the binflash 0xCC ReadRAM firmware read applies (NEC ND-*, Optiarc AD-*, NEC-based Lite-On iHAS)."),
        ChipsetFamily.Unknown => MethodApplicability.Perhaps(
            "Chipset unknown; the NEC 0xCC ReadRAM read applies only to Renesas/NEC-family drives."),
        _ => MethodApplicability.No(
            $"Detected {chipset.Family}; the NEC 0xCC ReadRAM read targets Renesas/NEC drives."),
    };

    public ExtractionResult Extract(ScsiDevice device, DriveIdentity id, ChipsetInfo chipset,
        IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        progress.Report(new ExtractionProgress(0, "NEC/Renesas RAM read (0xCC)"));

        // 1. Support probe = ReadBoot (0xCD). A non-NEC drive rejects it (ILLEGAL REQUEST); its sense gives a
        //    clean message. The authoritative gate, though, is whether 0xCC ReadRAM is accepted (below).
        var probe = device.SendCommand(ScsiCommand.NecReadBoot(false), ScsiDirection.In, new byte[0x40],
            note: "NEC ReadBoot 0xCD (probe)");
        var ps = probe.SenseInfo;
        bool bootRejected = probe.DeviceIoOk && probe.ScsiStatus != 0x00 && ps.Key == 0x05;

        // 2. Identify via ReadRAM (0xCC) signatures — this also confirms 0xCC works on the drive.
        var ident = NecDriveTable.Identify(device);

        if (!ident.AnyAccepted)
        {
            string why = bootRejected
                ? $"rejected the NEC vendor read (ILLEGAL REQUEST, asc={ps.Asc:X2}/{ps.Ascq:X2})"
                : probe.DeviceIoOk ? "did not accept the NEC vendor read" : probe.StatusText;
            return ExtractionResult.Unsupported(Id, DisplayName,
                $"The drive {why}. It is not a Renesas/NEC-family drive (NEC ND-*, Optiarc AD-*, NEC-based " +
                "Lite-On iHAS), so the 0xCC ReadRAM firmware read does not apply. Try a MediaTek method or a hardware programmer.");
        }

        string found = ident.Reads.Count > 0
            ? string.Join(", ", ident.Reads.Select(r => $"0x{r.Addr:X}='{Printable(r.Code)}'"))
            : "none";

        // 3a. Recognised model → dump its flash region(s).
        if (ident.Match is NecDriveTable.Signature sig)
        {
            var regions = NecDriveTable.RegionsFor(sig.Flash);
            if (regions.Length == 0)
                return ExtractionResult.Unsupported(Id, DisplayName,
                    $"Identified as {sig.Model} ({sig.Flash}), but binflash does not support dumping this flash " +
                    "class (Blu-ray). A hardware programmer is required for the full ROM.");

            var (needsId, idAddr) = NecDriveTable.IdInfo(sig);
            byte fwId = 0;
            if (needsId)
            {
                var ir = device.SendCommand(ScsiCommand.NecReadRam(false, idAddr, 4), ScsiDirection.In,
                    new byte[4], note: $"NEC ReadRAM 0xCC firmware-id off=0x{idAddr:X}");
                if (ir.Good && ir.Data is { Length: >= 1 }) fwId = ir.Data[0];
            }

            return DumpRegions(device, progress, ct, regions, fwId, needsId,
                $"NEC/Renesas firmware via 0xCC ReadRAM — {sig.Model} ({sig.Flash})",
                $"Identified {sig.Model} ({sig.Flash}) [{found}]");
        }

        // 3b. 0xCC works but the drive is not in the binflash table → best-effort dump of the common
        //     Optiarc 2 MiB range, clearly labelled. (Send me the signatures to extend the table.)
        var fallback = new[] { new NecDriveTable.FlashRegion(0x30000, 0x1F0000, false) };
        return DumpRegions(device, progress, ct, fallback, 0, needsId: false,
            "NEC/Renesas RAM via 0xCC ReadRAM — UNRECOGNISED model, best-effort 0x30000-0x1F0000 range",
            $"0xCC ReadRAM works but this drive is not in the binflash model table [signatures: {found}]. Best-effort range");
    }

    private ExtractionResult DumpRegions(ScsiDevice device, IProgress<ExtractionProgress> progress,
        CancellationToken ct, NecDriveTable.FlashRegion[] regions, byte fwId, bool needsId,
        string label, string summaryHead)
    {
        long total = regions.Sum(reg => (long)(reg.End - reg.Start));
        using var ms = new MemoryStream();
        long done = 0, meaningful = 0;
        string stop = "complete";
        bool truncated = false;

        foreach (var reg in regions)
        {
            for (uint addr = reg.Start; addr < reg.End; addr += BlockSize)
            {
                ct.ThrowIfCancellationRequested();
                var r = device.SendCommand(ScsiCommand.NecReadRam(reg.Bank, addr, BlockSize, fwId),
                    ScsiDirection.In, new byte[BlockSize],
                    note: $"NEC ReadRAM 0xCC off=0x{addr:X} bank={(reg.Bank ? 1 : 0)}");
                if (!r.Good || r.Data is null)
                {
                    stop = $"stopped at 0x{addr:X} ({r.StatusText})";
                    truncated = true;
                    break;
                }
                ms.Write(r.Data, 0, BlockSize);
                foreach (byte b in r.Data) if (b != 0x00 && b != 0xFF) meaningful++;
                done += BlockSize;
                progress.Report(new ExtractionProgress((int)(100L * done / Math.Max(1, total))));
            }
            if (truncated) break;
        }

        byte[] data = ms.ToArray();

        if (data.Length == 0 || meaningful == 0)
            return ExtractionResult.Unsupported(Id, DisplayName,
                $"{summaryHead}, but no firmware-like data was read ({stop})." +
                (needsId
                    ? " This drive gates its flash behind 'safe mode' — a state-changing vendor command this " +
                      "read-only tool does not issue — so the region is unreadable here. A hardware programmer may be required."
                    : " A hardware programmer may be required."));

        string note = truncated && needsId
            ? " Note: truncated — this drive likely needs 'safe mode' (a state change this read-only tool does not issue) to read the whole flash."
            : truncated ? " Note: the dump was truncated before the end of the range." : "";

        return ExtractionResult.Ok(Id, DisplayName, data,
            label + " (not a verified byte-exact ROM)",
            $"{summaryHead}: read {data.Length:N0} bytes via 0xCC ReadRAM " +
            $"({100.0 * meaningful / Math.Max(1, data.Length):F1}% firmware-like, non-0x00/0xFF); {stop}.{note}");
    }

    private static string Printable(string s)
        => new(s.Select(c => c >= 32 && c < 127 ? c : '.').ToArray());
}

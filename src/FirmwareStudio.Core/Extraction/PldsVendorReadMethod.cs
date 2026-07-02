using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Extraction;

/// <summary>
/// Method — PLDS / Lite-On vendor read (opcode 0xDF), the command the Plextor/PLDS firmware updater uses
/// to talk to its MediaTek controller. Read-only: it only ever issues <b>mode 0</b> (the confirmed data-in
/// direction), first reproducing the updater's known 12-byte drive-state read as a support probe, then
/// walking buffer IDs to surface whatever readable vendor buffers the controller exposes. What comes back
/// may be drive-state registers or controller RAM — potentially the drive's decrypted resident firmware —
/// but that is never guaranteed, so the output is labelled as raw vendor-buffer contents, not "firmware".
/// The updater flashes with 0xDF/WRITE BUFFER too; those write paths are deliberately omitted (this tool
/// never writes to a drive).
/// </summary>
public sealed class PldsVendorReadMethod : IFirmwareExtractionMethod
{
    private const byte ReadMode = 0x00;         // the only mode the updater used for data-in reads
    private const byte StateBufferId = 0x0F;    // the updater's drive-state register bank
    private const int MaxBufferId = 0x1F;       // sweep 0..31
    private const int ProbeLen = 4096;          // per-buffer probe read

    public string Id => "plds";
    public string DisplayName => "PLDS/Lite-On vendor read (0xDF)";
    public string Description =>
        "Issues the PLDS/Lite-On vendor command 0xDF (as used by the Plextor/PLDS firmware updater on its " +
        "MediaTek controller). Read-only — only the confirmed read mode is used. It confirms the command is " +
        "accepted, then walks buffer IDs to read out any exposed vendor buffers. The bytes may be drive " +
        "registers or controller RAM (possibly the decrypted firmware), and are labelled as such — never a " +
        "guaranteed flash ROM.";

    public MethodApplicability Evaluate(DriveIdentity id, ChipsetInfo chipset)
    {
        // 0xDF is specifically the PLDS/Lite-On (incl. Plextor PX-8xx, Slimtype) vendor command. Confirmed
        // lineage → applicable; other MediaTek OEMs (LG/HLDS, ASUS, TSSTcorp) share the chipset but may not
        // implement 0xDF, so they are only "maybe" — the probe will confirm or reject cleanly either way.
        bool pldsLineage =
            StartsCI(id.Vendor, "PLDS") || StartsCI(id.Vendor, "PLEXTOR") ||
            StartsCI(id.Vendor, "LITE-ON") || StartsCI(id.Vendor, "LITEON") || StartsCI(id.Vendor, "SLIMTYPE");

        if (pldsLineage)
            return MethodApplicability.Yes("PLDS/Lite-On/Plextor drive — 0xDF is the confirmed vendor command.");
        if (chipset.Family == ChipsetFamily.MediaTek)
            return MethodApplicability.Perhaps("MediaTek chipset, but 0xDF is a PLDS/Lite-On vendor command; this OEM may not implement it.");
        if (chipset.Family == ChipsetFamily.Unknown)
            return MethodApplicability.Perhaps("Chipset unknown; 0xDF is a MediaTek/PLDS vendor command that may not apply.");
        return MethodApplicability.No($"Detected {chipset.Family}; 0xDF targets PLDS/Lite-On (MediaTek) drives.");
    }

    public ExtractionResult Extract(ScsiDevice device, DriveIdentity id, ChipsetInfo chipset,
        IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        progress.Report(new ExtractionProgress(0, "PLDS vendor read (0xDF)"));

        // 1. Support probe = the updater's exact 12-byte drive-state read. An ILLEGAL REQUEST (sense key
        //    0x05) means the drive does not implement 0xDF; anything else means it understood the command.
        var probe = device.SendCommand(ScsiCommand.PldsVendor(ReadMode, StateBufferId, 0x20, 0x00),
            ScsiDirection.In, new byte[12], note: "PLDS 0xDF drive-state read (probe)");
        var s = probe.SenseInfo;
        if (probe.DeviceIoOk && probe.ScsiStatus != 0x00 && s.Key == 0x05)
            return ExtractionResult.Unsupported(Id, DisplayName,
                $"The drive rejected the PLDS 0xDF vendor command (ILLEGAL REQUEST, asc={s.Asc:X2}/{s.Ascq:X2}). " +
                "It is not a PLDS/Lite-On (MediaTek) vendor-command drive, or this firmware does not expose 0xDF.");
        if (!probe.DeviceIoOk)
            return ExtractionResult.Unsupported(Id, DisplayName,
                $"Could not issue the 0xDF vendor command ({probe.StatusText}).");

        // 2. Walk buffer IDs at the read mode; keep the responder with the most non-zero bytes.
        byte bestId = 0;
        byte[] bestData = Array.Empty<byte>();
        long bestNonZero = 0;
        var responders = new List<string>();
        for (int b = 0; b <= MaxBufferId; b++)
        {
            ct.ThrowIfCancellationRequested();
            var r = device.SendCommand(ScsiCommand.PldsVendor(ReadMode, (byte)b), ScsiDirection.In,
                new byte[ProbeLen], note: $"PLDS 0xDF read bufferId={b}");
            progress.Report(new ExtractionProgress((int)(100L * (b + 1) / (MaxBufferId + 1))));
            if (!r.Good || r.Data is null) continue;

            int len = r.TransferredLength is > 0 and <= ProbeLen ? r.TransferredLength : r.Data.Length;
            long nz = CountNonZero(r.Data, len);
            if (nz > 0) responders.Add($"#{b}({len}B,{nz}nz)");
            if (nz > bestNonZero) { bestNonZero = nz; bestId = (byte)b; bestData = r.Data[..len]; }
        }

        if (bestNonZero == 0)
            return ExtractionResult.Unsupported(Id, DisplayName,
                "The 0xDF vendor command is accepted, but every buffer ID returned empty (all zero). On this " +
                "drive it exposes only drive-state registers, not a readable firmware/RAM image. The decrypted " +
                "firmware may only be reachable through an undocumented mode/bufferId, or via a hardware programmer.");

        return ExtractionResult.Ok(Id, DisplayName, bestData,
            "PLDS 0xDF vendor-buffer contents (experimental — drive state/registers or controller RAM, not verified firmware)",
            $"0xDF accepted; buffer {bestId} returned {bestData.Length:N0} bytes " +
            $"({100.0 * bestNonZero / Math.Max(1, bestData.Length):F1}% non-zero). " +
            $"Responders: {(responders.Count > 0 ? string.Join(", ", responders) : "none")}.");
    }

    private static bool StartsCI(string s, string prefix)
        => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static long CountNonZero(byte[] buf, int len)
    {
        int c = len > 0 && len <= buf.Length ? len : buf.Length;
        long n = 0;
        for (int i = 0; i < c; i++) if (buf[i] != 0) n++;
        return n;
    }
}

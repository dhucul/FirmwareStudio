using System.Text;
using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Extraction;

/// <summary>
/// Method — PLDS / Lite-On vendor read (opcode 0xDF), the command the Plextor/PLDS firmware updater uses
/// to talk to its MediaTek controller. Read-only: it only ever issues <b>mode 0</b> (the confirmed data-in
/// direction), first reproducing the updater's known 12-byte drive-state read as a support probe, then
/// walking buffer IDs and arg3 values to surface whatever readable vendor buffers the controller exposes.
///
/// <para>Enhanced v2 — two-phase extraction:
/// <b>Phase 1 (probe)</b>: quick 256-byte scans across all 32 buffer IDs × 3 known arg3 values to discover
/// every responsive (non‑zero) vendor‑buffer slot.
/// <b>Phase 2 (deep read)</b>: for each discovered slot, issue one larger transfer (up to 64 KiB) to grab
/// the full vendor-buffer contents, then assemble all regions into a <b>composite dump</b> with a textual
/// section-header so the user can see which bytes came from which buffer/arg3 combination.
/// </para>
///
/// <para>What comes back may be drive-state registers or controller RAM — potentially the drive's decrypted
/// resident firmware — but that is never guaranteed, so the output is labelled as raw vendor-buffer
/// contents, not "firmware". The updater flashes with 0xDF/WRITE BUFFER too; those write paths are
/// deliberately omitted (this tool never writes to a drive).</para>
/// </summary>
public sealed class PldsVendorReadMethod : IFirmwareExtractionMethod
{
    private const byte ReadMode = 0x00;         // the only mode the updater used for data-in reads
    private const byte StateBufferId = 0x0F;    // the updater's drive-state register bank
    private const int MaxBufferId = 0x1F;       // sweep 0..31
    private const int ProbeLen = 256;           // per-slot quick probe (smaller = faster discovery)
    private const int MaxPerSlot = 0x10000;     // 64 KiB per responsive slot (firmware regions are small)

    // The three arg3 values the Plextor PX-891SAF updater used for different register banks
    // (observed: 0x20=dstate-1, 0x14=dstate-2, 0x11=counter-latch). Trying all three finds more.
    private static readonly byte[] Arg3Values = { 0x20, 0x14, 0x11, 0x00 };

    /// <summary>A discovered vendor-buffer slot with its data.</summary>
    private sealed record Slot(byte BufferId, byte Arg3, byte[] Data, long NonZero)
    {
        public string Label => $"0xDF buf=0x{BufferId:X2} arg3=0x{Arg3:X2}";
    }

    public string Id => "plds";
    public string DisplayName => "PLDS/Lite-On vendor read (0xDF)";
    public string Description =>
        "Issues the PLDS/Lite-On vendor command 0xDF (as used by the Plextor/PLDS firmware updater on its " +
        "MediaTek controller). Read-only — only the confirmed read mode is used. Two-phase: quick probe of " +
        "buffer IDs × arg3 values, then deep read of every responsive slot → composite dump with section " +
        "headers. The bytes may be drive registers or controller RAM (possibly the decrypted firmware).";

    public MethodApplicability Evaluate(DriveIdentity id, ChipsetInfo chipset)
    {
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
        progress.Report(new ExtractionProgress(0, "PLDS vendor read (0xDF) — probe phase"));

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

        // 2. PHASE 1 — quick probe of every (bufferId, arg3) combination.
        var discovered = new List<Slot>();
        int totalSlots = (MaxBufferId + 1) * Arg3Values.Length;
        int probed = 0;
        foreach (byte bufId in Enumerable.Range(0, MaxBufferId + 1).Select(i => (byte)i))
        {
            ct.ThrowIfCancellationRequested();
            foreach (byte arg3 in Arg3Values)
            {
                var r = device.SendCommand(ScsiCommand.PldsVendor(ReadMode, bufId, arg3), ScsiDirection.In,
                    new byte[ProbeLen], note: $"PLDS 0xDF probe buf=0x{bufId:X2} arg3=0x{arg3:X2}");
                probed++;
                progress.Report(new ExtractionProgress((int)(50L * probed / totalSlots),
                    null, $"Probe {probed}/{totalSlots}: buf=0x{bufId:X2} arg3=0x{arg3:X2}"));

                if (!r.Good || r.Data is null) continue;
                int len = r.TransferredLength is > 0 and <= ProbeLen ? r.TransferredLength : ProbeLen;
                long nz = CountNonZero(r.Data, len);
                if (nz > 0)
                {
                    discovered.Add(new Slot(bufId, arg3, r.Data[..len], nz));
                    progress.Report(new ExtractionProgress(0, null,
                        $"  → buf=0x{bufId:X2} arg3=0x{arg3:X2}: {len}B, {nz}nz — discovered"));
                }
            }
        }

        if (discovered.Count == 0)
            return ExtractionResult.Unsupported(Id, DisplayName,
                "The 0xDF vendor command is accepted, but every buffer ID × arg3 combination returned empty " +
                "(all zero). On this drive 0xDF exposes only drive-state registers, not readable firmware/RAM. " +
                "The decrypted firmware may only be reachable through an undocumented mode/bufferId, " +
                "or via a hardware programmer.");

        // 3. PHASE 2 — deep-read each discovered slot. The 0xDF CDB carries no offset field (the 12-byte
        //    CDB is just [0xDF, mode, bufferId, arg3, arg4, 0…0]), so issuing the same (bufferId, arg3)
        //    with a larger transfer buffer is the only way to read more bytes — there is no address walk.
        //    Each slot gets ONE read at MaxPerSlot bytes.
        progress.Report(new ExtractionProgress(50, $"Deep-reading {discovered.Count} responsive vendor-buffer slots…"));
        var deepResults = new List<Slot>();
        for (int si = 0; si < discovered.Count; si++)
        {
            ct.ThrowIfCancellationRequested();
            var slot = discovered[si];
            var r = device.SendCommand(ScsiCommand.PldsVendor(ReadMode, slot.BufferId, slot.Arg3),
                ScsiDirection.In, new byte[MaxPerSlot],
                note: $"PLDS 0xDF deep buf=0x{slot.BufferId:X2} arg3=0x{slot.Arg3:X2} len={MaxPerSlot}");

            if (!r.Good || r.Data is null) continue;
            int got = r.TransferredLength > 0 && r.TransferredLength <= MaxPerSlot ? r.TransferredLength : MaxPerSlot;
            if (got == 0) continue;

            // Trim trailing zeros
            int keep = got;
            while (keep > 0 && r.Data[keep - 1] == 0) keep--;
            if (keep == 0) continue;

            long nz = CountNonZero(r.Data, keep);
            if (nz > 0)
                deepResults.Add(new Slot(slot.BufferId, slot.Arg3, r.Data[..keep], nz));

            progress.Report(new ExtractionProgress(50 + (int)(49L * (si + 1) / discovered.Count),
                null, $"Slot {si + 1}/{discovered.Count}: buf=0x{slot.BufferId:X2} arg3=0x{slot.Arg3:X2} → {keep}B, {nz}nz"));
        }

        if (deepResults.Count == 0)
            return ExtractionResult.Unsupported(Id, DisplayName,
                "Probe found responsive slots, but deep reads returned only zeros. The drive may expose only " +
                "transient status registers — not persistent firmware/RAM. A hardware programmer may be required.");

        // 4. Assemble composite dump with section headers.
        byte[] composite = BuildComposite(deepResults);
        long totalNz = deepResults.Sum(d => d.NonZero);
        string sections = string.Join("\n  ", deepResults.Select(d =>
            $"{d.Label}: {d.Data.Length:N0} B ({100.0 * d.NonZero / Math.Max(1, d.Data.Length):F1}% non-zero)"));

        string label = deepResults.Count == 1
            ? $"PLDS 0xDF vendor-buffer contents — {deepResults[0].Label}"
            : $"PLDS 0xDF composite vendor dump — {deepResults.Count} responsive slots";

        return ExtractionResult.Ok(Id, DisplayName, composite, label,
            $"0xDF accepted; {discovered.Count} slots found in probe, {deepResults.Count} with real data after deep read.\n" +
            $"  Slots:\n  {sections}\n" +
            $"  Total composite dump: {composite.Length:N0} bytes ({100.0 * totalNz / Math.Max(1, composite.Length):F1}% non-zero). " +
            "Each region is prefixed with a 128-byte ASCII header identifying the buffer/arg3 source.");
    }

    /// <summary>Assemble a composite dump: 128-byte ASCII header per slot, then the slot's data.</summary>
    private static byte[] BuildComposite(List<Slot> slots)
    {
        // Estimate: header per slot (128 B) + data per slot
        long total = slots.Sum(s => 128L + s.Data.Length);
        if (total > int.MaxValue - 256) total = int.MaxValue - 256;   // safety
        using var ms = new MemoryStream((int)total);

        // Leading file identifier
        byte[] fileId = Encoding.ASCII.GetBytes(
            $"FirmwareStudio PLDS 0xDF composite vendor dump — {slots.Count} region(s) [v2]\r\n" +
            $"Created: {DateTime.UtcNow:O}\r\n\r\n");
        ms.Write(fileId);

        foreach (var slot in slots)
        {
            string header = $"=== {slot.Label} ===  size={slot.Data.Length}  non-zero={slot.NonZero}" +
                            $"  ({100.0 * slot.NonZero / Math.Max(1, slot.Data.Length):F1}%)".PadRight(127) + "\n";
            byte[] hdr = Encoding.ASCII.GetBytes(header[..Math.Min(header.Length, 128)]);
            // Pad to exactly 128 bytes
            var padded = new byte[128];
            Array.Copy(hdr, padded, Math.Min(hdr.Length, 128));
            ms.Write(padded);
            ms.Write(slot.Data);
        }

        return ms.ToArray();
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

using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Drives;

/// <summary>
/// Identifies the drive's controller family from INQUIRY vendor/model, then confirms with a harmless
/// read-only vendor probe. v1 focuses on reliably spotting MediaTek (the common modern chipset), which is
/// what routes the MediaTek cache-read extraction method. Ported from OptiScan's ScsiDrive.Chipset.cpp.
/// </summary>
public static class ChipsetDetector
{
    private readonly record struct Mapping(string VendorPrefix, string ModelPrefix, ChipsetFamily Family, string Name, int Confidence);

    // First match wins; entries with a model prefix precede the vendor catch-all.
    private static readonly Mapping[] Table =
    {
        new("LITE-ON",  "IHAS", ChipsetFamily.MediaTek, "MediaTek (LiteOn iHAS)",           90),
        new("LITE-ON",  "IHBS", ChipsetFamily.MediaTek, "MediaTek MT1959 (LiteOn iHBS)",    90),
        new("LITE-ON",  "",     ChipsetFamily.MediaTek, "MediaTek (LiteOn)",                75),
        new("LITEON",   "",     ChipsetFamily.MediaTek, "MediaTek (LiteOn)",                75),
        new("PLDS",     "",     ChipsetFamily.MediaTek, "MediaTek (Philips-LiteOn)",        75),
        new("ASUS",     "",     ChipsetFamily.MediaTek, "MediaTek (ASUS OEM)",              70),
        new("TSSTCORP", "",     ChipsetFamily.MediaTek, "MediaTek (Samsung/Toshiba OEM)",   70),
        new("HL-DT-ST", "",     ChipsetFamily.MediaTek, "MediaTek (LG/HLDS)",               70),
        new("HLDS",     "",     ChipsetFamily.MediaTek, "MediaTek (LG/HLDS)",               70),
        new("SLIMTYPE", "",     ChipsetFamily.MediaTek, "MediaTek (Slimtype/LiteOn)",       70),
        new("PIONEER",  "",     ChipsetFamily.Pioneer,  "Pioneer Custom",                   70),
        new("OPTIARC",  "",     ChipsetFamily.Renesas,  "Renesas/NEC (Optiarc)",            65),
        new("SONY",     "AD-",  ChipsetFamily.Renesas,  "Renesas/NEC (Sony Optiarc)",       65),
        new("_NEC",     "",     ChipsetFamily.Renesas,  "Renesas/NEC",                      65),
        new("NEC",      "",     ChipsetFamily.Renesas,  "Renesas/NEC",                      65),
        new("MATSHITA", "",     ChipsetFamily.Panasonic,"Panasonic MN103",                  65),
        // Modern Plextor PX-8xx (e.g. PX-891SAF/PLUS) are PLDS/Lite-On builds on a MediaTek MT62xx controller
        // (confirmed "MT62SA" in the PX-891SAF updater); the vendor 0xDF command applies. Older Plextor drives
        // (Premium, PX-7xxA) were custom, so the bare-vendor fallback stays "Other".
        new("PLEXTOR",  "PX-8", ChipsetFamily.MediaTek, "MediaTek MT62xx (Plextor/PLDS PX-8xx)", 80),
        new("PLEXTOR",  "",     ChipsetFamily.Other,    "Plextor (custom or MediaTek OEM)", 50),
    };

    public static ChipsetInfo Detect(ScsiDevice dev, DriveIdentity id)
    {
        var evidence = new List<string>();
        var (family, name, conf) = FromTable(id.Vendor, id.Model);
        evidence.Add(family == ChipsetFamily.Unknown
            ? $"No vendor/model table match for \"{id.Vendor} {id.Model}\"."
            : $"Vendor/model table: \"{id.Vendor} {id.Model}\" → {name} ({conf}%).");

        // Confirmation probe: MediaTek read-cache (0xF1) is read-only. Only INVALID COMMAND OPERATION CODE
        // (asc=0x20) means the opcode is genuinely absent → NOT a MediaTek cache-read drive. INVALID FIELD IN
        // CDB (asc=0x24) means the drive *understood* 0xF1 and only objected to a field, so it IS a MediaTek
        // drive (as does GOOD / not-ready / any other response). Confirmed on the PX-891SAF, whose MT62xx
        // controller answers asc=24 to several vendor probes it nonetheless implements.
        try
        {
            var probe = dev.SendCommand(ScsiCommand.MediaTekReadCache(0, 64), ScsiDirection.In,
                new byte[64], timeoutSec: 10, note: "chipset probe: MediaTek 0xF1 read-cache");
            var s = probe.SenseInfo;
            bool rejected = probe.DeviceIoOk && s.OpcodeUnsupported;

            if (rejected)
            {
                evidence.Add("MediaTek 0xF1 rejected (INVALID COMMAND OPERATION CODE, asc=20) → not a MediaTek cache-read drive.");
                if (family == ChipsetFamily.MediaTek) { conf = Math.Min(conf, 45); }
            }
            else if (probe.DeviceIoOk && s.FieldInvalid)
            {
                // asc=24: the drive recognised 0xF1 but rejected a field → it IS a MediaTek controller,
                // though the cache read itself may need different parameters. Confirm the family, but at
                // slightly lower confidence than a clean accept, and don't claim the read works.
                evidence.Add("MediaTek 0xF1 recognised (INVALID FIELD IN CDB, asc=24) → MediaTek controller.");
                family = ChipsetFamily.MediaTek;
                if (name.StartsWith("MediaTek", StringComparison.OrdinalIgnoreCase) == false)
                    name = "MediaTek (confirmed by vendor command)";
                conf = Math.Max(conf, 85);
            }
            else if (probe.Good)
            {
                // A GOOD completion proves the drive implements the MediaTek cache read. This is the
                // functional signal (e.g. an "Optiarc" AD-5290S+ that accepts 0xF1 is a MediaTek-based drive,
                // regardless of the vendor string). Auto no longer relies on this label anyway — RunAutoAsync
                // picks the method that actually returns data — so this just sets an accurate display family.
                evidence.Add("MediaTek 0xF1 completed with GOOD status → MediaTek cache-read supported.");
                family = ChipsetFamily.MediaTek;
                if (name.StartsWith("MediaTek", StringComparison.OrdinalIgnoreCase) == false)
                    name = "MediaTek (confirmed by vendor command)";
                conf = Math.Max(conf, 92);
            }
            else if (probe.DeviceIoOk)
            {
                // BUSY / NOT READY / UNIT ATTENTION / hardware errors can be raised before the CDB is decoded,
                // so they do not prove that the vendor opcode exists.
                evidence.Add($"MediaTek 0xF1 probe was inconclusive ({probe.StatusText}); leaving table result.");
            }
            else
            {
                evidence.Add("MediaTek 0xF1 probe could not be issued; leaving table result.");
            }
        }
        catch (Exception ex)
        {
            evidence.Add($"MediaTek probe skipped: {ex.Message}");
        }

        if (family == ChipsetFamily.Unknown) name = $"Unknown ({id.Vendor} {id.Model})".Trim();
        return new ChipsetInfo(family, name, conf, evidence);
    }

    private static (ChipsetFamily, string, int) FromTable(string vendor, string model)
    {
        string v = vendor.Trim();
        string m = model.Trim();
        foreach (var e in Table)
        {
            if (StartsWithCI(v, e.VendorPrefix) && StartsWithCI(m, e.ModelPrefix))
                return (e.Family, e.Name, e.Confidence);
        }
        return (ChipsetFamily.Unknown, "Unknown", 10);
    }

    private static bool StartsWithCI(string s, string prefix)
        => prefix.Length == 0 || s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}

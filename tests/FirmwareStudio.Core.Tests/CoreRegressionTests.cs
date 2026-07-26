using System.Buffers.Binary;
using System.Text;
using FirmwareStudio.Core.Analysis;
using FirmwareStudio.Core.Extraction;
using FirmwareStudio.Core.Firmware;
using FirmwareStudio.Core.Hardware;
using FirmwareStudio.Core.Logging;
using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;
using Xunit;

namespace FirmwareStudio.Core.Tests;

public sealed class CoreRegressionTests
{
    [Fact]
    public void Be24_RejectsOutOfRangeValues_AndEncodesBoundary()
    {
        var bytes = new byte[3];

        ScsiCommand.WriteBe24(bytes, 0, 0xFF_FFFF);

        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => ScsiCommand.WriteBe24(bytes, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScsiCommand.WriteBe24(bytes, 0, 0x100_0000));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScsiCommand.ReadBe24(bytes, 1));
    }

    [Fact]
    public void SenseData_ParsesMinimumDescriptorSense()
    {
        var sense = SenseData.Parse(new byte[] { 0x72, 0x05, 0x20, 0x01 });

        Assert.True(sense.Present);
        Assert.Equal((byte)0x05, sense.Key);
        Assert.Equal((byte)0x20, sense.Asc);
        Assert.Equal((byte)0x01, sense.Ascq);
    }

    [Fact]
    public void TruncatedPioneerSignature_IsNotParsedAsACompleteImage()
    {
        byte[] truncated = Encoding.ASCII.GetBytes("********  Copyright(c) 2000 Pioneer");

        Assert.False(PioneerFirmwareImage.LooksLikeImage(truncated));
        var analysis = FirmwareFile.Analyze(truncated);
        Assert.Equal(FirmwareFileKind.VpdImage, analysis.Kind);
    }

    [Fact]
    public void VpdMarker_MustBeAtExactDocumentedOffset()
    {
        byte[] misplaced = new byte[0x600];
        "VPD_update_file"u8.CopyTo(misplaced.AsSpan(0x500));
        Assert.False(FirmwareImage.Parse(misplaced).IsVpdUpdateImage);

        byte[] exact = new byte[0x600];
        "VPD_update_file"u8.CopyTo(exact.AsSpan(0x400));
        Assert.True(FirmwareImage.Parse(exact).IsVpdUpdateImage);
    }

    [Fact]
    public void Flat8051Image_DoesNotReadPastDeclaredBankLength()
    {
        byte[] source = new byte[32];
        source[10] = 0x11;
        source[11] = 0x22;
        source[12] = 0x33;
        var bank = new CodeBankInfo { Offset = 10, Length = 2, RuntimeDelta = 0 };

        byte[] flat = OpticalRamImage.BuildFlat8051Image(source, bank);

        Assert.Equal((byte)0x11, flat[0]);
        Assert.Equal((byte)0x22, flat[1]);
        Assert.Equal((byte)0x00, flat[2]);
    }

    [Fact]
    public void Orchestrator_UsesInjectedMethodsWithoutRequiringBuiltIns()
    {
        var custom = new FakeMethod("custom", Applicability.Applicable);
        var orchestrator = new ExtractionOrchestrator(new[] { custom });
        var id = new DriveIdentity('D', "V", "M", "1", null, "SATA");
        var chip = new ChipsetInfo(ChipsetFamily.Unknown, "unknown", 0, Array.Empty<string>());

        Assert.Same(custom, orchestrator.PickAuto(id, chip));
        Assert.Throws<ArgumentException>(() => new ExtractionOrchestrator(Array.Empty<IFirmwareExtractionMethod>()));

        var withFallback = new ExtractionOrchestrator(new IFirmwareExtractionMethod[]
        {
            new UniversalReadBufferMethod(),
            custom,
        });
        Assert.Same(custom, withFallback.PickAuto(id, chip));
    }

    [Fact]
    public void MixedEmptyJedecId_IsRejected()
    {
        var chip = new SpiFlashChip("empty", 0x00, 0xFF, 0x00, 1 << 20,
            new byte[] { 0x00, 0xFF, 0x00 }, FlashVoltage.Unknown);

        Assert.True(chip.LooksEmpty);
    }

    [Fact]
    public void SfxInflater_RejectsOversizedAdvertisedExecutable()
    {
        byte[] sfx = new byte[64];
        sfx[0] = 0x50; sfx[1] = 0x4B; sfx[2] = 0x03; sfx[3] = 0x04;
        BinaryPrimitives.WriteUInt16LittleEndian(sfx.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(sfx.AsSpan(18), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(sfx.AsSpan(22), 64u * 1024 * 1024 + 1);
        BinaryPrimitives.WriteUInt16LittleEndian(sfx.AsSpan(26), 5);
        Encoding.ASCII.GetBytes("x.exe").CopyTo(sfx, 30);

        Assert.Null(ZipSfxExtractor.ExtractInnerExe(sfx));
    }

    [Fact]
    public void DumpWriter_WritesOnlySuppliedCommands()
    {
        string dir = NewTempDirectory();
        try
        {
            string bin = Path.Combine(dir, "capture.bin");
            var command = new CommandLogEntry(
                DateTime.UtcNow, "3C 02", ScsiDirection.In, 4, 4,
                0, 0, 0, 0, true, 0, "GOOD", "current-run");

            var files = DumpWriter.Write(
                bin,
                ExtractionResult.Ok("test", "test", new byte[] { 1, 2, 3 }, "bytes", "ok"),
                Identity(),
                Chipset(),
                new[] { command },
                DateTime.UtcNow);

            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(files.BinPath));
            Assert.True(File.Exists(files.SidecarPath));
            Assert.NotNull(files.LogPath);
            string log = File.ReadAllText(files.LogPath!);
            Assert.Contains("current-run", log);
            Assert.DoesNotContain("previous-run", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DumpWriter_RestoresExistingOutputsWhenPromotionFails()
    {
        string dir = NewTempDirectory();
        try
        {
            string bin = Path.Combine(dir, "capture.bin");
            File.WriteAllBytes(bin, new byte[] { 9, 9 });
            Directory.CreateDirectory(Path.ChangeExtension(bin, ".json"));

            Assert.ThrowsAny<IOException>(() => DumpWriter.Write(
                bin,
                ExtractionResult.Ok("test", "test", new byte[] { 1, 2, 3 }, "bytes", "ok"),
                Identity(),
                Chipset(),
                Array.Empty<CommandLogEntry>(),
                DateTime.UtcNow));

            Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(bin));
            Assert.True(Directory.Exists(Path.ChangeExtension(bin, ".json")));
            Assert.False(File.Exists(Path.ChangeExtension(bin, ".log")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DriveIdentity Identity()
        => new('D', "VENDOR", "MODEL", "1.0", "SERIAL", "SATA");

    private static ChipsetInfo Chipset()
        => new(ChipsetFamily.Unknown, "unknown", 0, Array.Empty<string>());

    private static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "FirmwareStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeMethod(string id, Applicability applicability) : IFirmwareExtractionMethod
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public string Description => Id;

        public MethodApplicability Evaluate(DriveIdentity id, ChipsetInfo chipset)
            => new(applicability, "test");

        public ExtractionResult Extract(ScsiDevice device, DriveIdentity id, ChipsetInfo chipset,
            IProgress<ExtractionProgress> progress, CancellationToken ct)
            => throw new NotSupportedException();
    }
}

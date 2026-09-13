using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FirmwareStudio.Core.Analysis;
using FirmwareStudio.Core.Drives;
using FirmwareStudio.Core.Extraction;
using FirmwareStudio.Core.Firmware;
using FirmwareStudio.Core.Hardware;
using FirmwareStudio.Core.Logging;
using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;
using Xunit;

namespace FirmwareStudio.Core.Tests;

public sealed class AuditRegressionTests
{
    private static readonly DriveIdentity Identity = new('D', "VENDOR", "MODEL", "1.0", "SERIAL", "SATA");
    private static readonly ChipsetInfo Chipset = new(ChipsetFamily.Unknown, "unknown", 0, []);
    private static readonly IProgress<ExtractionProgress> Progress = new InlineProgress<ExtractionProgress>(_ => { });

    [Fact]
    public void Cache_PreservesDifferentCopies_ZeroGaps_AndLateSparseData()
    {
        var memory = new byte[8 * 1024 * 1024];
        Array.Fill(memory, (byte)0x11, 0, 0x80000);
        Array.Fill(memory, (byte)0x22, 0x40000, 26214);
        memory[0x84000] = 0xA5;
        memory[0x7F8000] = 0x5A;
        var device = MemoryDevice(memory, cache: true, transferCap: 8191);
        var result = Extract(new MediaTekCacheReadMethod(), device);
        Assert.True(result.IsComplete);
        Assert.Equal(memory, result.Firmware);
        Assert.Null(DumpAnalyzer.Analyze(memory).RepeatPeriod);
    }

    [Fact]
    public void Flash_PreservesErasedInteriorAndTrailingBytes()
    {
        var memory = Enumerable.Repeat((byte)0xFF, 4 * 1024 * 1024).ToArray();
        memory[0] = 0x12; memory[0x20000] = 0x34;
        var result = Extract(new MtkFlashReadMethod(), MemoryDevice(memory, cache: false));
        Assert.True(result.IsComplete);
        Assert.Equal(memory, result.Firmware);
    }

    [Theory]
    [InlineData(true, -1)]
    [InlineData(true, 16385)]
    [InlineData(false, -1)]
    [InlineData(false, 16385)]
    public void InvalidReadCount_PreservesOnlyEarlierValidatedBytes(bool cache, int invalid)
    {
        int calls = 0;
        var device = new FakeScsi((cdb, data) =>
        {
            Array.Fill(data!, (byte)0x5A);
            return Response(cdb, data, ++calls == 1 ? data!.Length : invalid);
        });
        var result = Extract(cache ? new MediaTekCacheReadMethod() : new MtkFlashReadMethod(), device);
        Assert.Equal(ExtractionStatus.Partial, result.Status);
        Assert.Equal(0x4000, result.ByteCount);
        Assert.All(result.Firmware!, b => Assert.Equal((byte)0x5A, b));
        Assert.Contains("invalid transfer", result.Summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plds_PreservesSupportAndDiscoveryBytes_WhenLargerReadsFail(bool discoveryWorks)
    {
        int calls = 0;
        var device = new FakeScsi((cdb, data) =>
        {
            calls++;
            if (calls == 1 || (discoveryWorks && data!.Length == 256))
            { data![0] = 0x5A; return Response(cdb, data, 12); }
            return Response(cdb, data, 0, good: false);
        });
        var result = Extract(new PldsVendorReadMethod(), device);
        Assert.Equal(ExtractionStatus.Partial, result.Status);
        var composite = FirmwareFile.Analyze(result.Firmware!).Composite!;
        Assert.Equal(discoveryWorks ? 128 : 1, composite.Sections.Count);
        Assert.All(composite.Sections, section =>
        {
            Assert.Equal(12, section.Data.Length);
            Assert.Equal((byte)0x5A, section.Data[0]);
        });
    }

    [Theory]
    [InlineData("nec", 1)]
    [InlineData("plds", 2)]
    [InlineData("mediatek", 3)]
    public void Cancellation_StopsBeforeAnotherCommand(string methodId, int cancelAt)
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        var device = new FakeScsi((cdb, data) =>
        {
            if (++calls == cancelAt) cts.Cancel();
            Array.Fill(data!, (byte)0x5A);
            return Response(cdb, data, data!.Length);
        });
        Assert.Throws<OperationCanceledException>(() => Extract(new ExtractionOrchestrator().ById(methodId)!, device, cts.Token));
        Assert.Equal(cancelAt, calls);
    }

    [Fact]
    public async Task Cancellation_AfterMethodReturns_PreventsResultCommit()
    {
        using var cts = new CancellationTokenSource();
        var method = new FakeMethod("custom", () =>
        {
            cts.Cancel();
            return ExtractionResult.Ok("custom", "custom", [1], "data", "done");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExtractionOrchestrator([method])
            .RunAsync(new FakeScsi((_, _) => throw new Exception()), Identity, Chipset, method, Progress, cts.Token));
    }

    [Fact]
    public void Universal_ShortCaptureIsExplicitlyPartial()
    {
        var device = new FakeScsi((cdb, data) =>
        {
            if (cdb[1] == 3)
            {
                if (cdb[2] == 0) ScsiCommand.WriteBe24(data!, 1, 0x10000);
                return Response(cdb, data, 4);
            }
            if (ScsiCommand.ReadBe24(cdb, 3) != 0) return Response(cdb, data, 0, false);
            Array.Fill(data!, (byte)0x5A);
            return Response(cdb, data, data!.Length);
        });
        var result = Extract(new UniversalReadBufferMethod(), device);
        Assert.True(result.Success);
        Assert.False(result.IsComplete);
        Assert.Equal(0x8000, result.ByteCount);
        Assert.Contains("before the advertised end", result.Summary);
    }

    [Fact]
    public async Task Auto_RetainsPreferredPartialCapture_AndExplainsSelection()
    {
        var wanted = ExtractionResult.Partial("nec", "NEC", [1, 2], "data", "interrupted at address 2");
        var first = new FakeMethod("nec", () => wanted);
        var second = new FakeMethod("mediatek", () => throw new Exception("Should not replace the preferred capture"));
        var result = await new ExtractionOrchestrator([first, second]).RunAutoAsync(
            new FakeScsi((_, _) => throw new Exception()), Identity, Chipset, Progress, CancellationToken.None);
        Assert.Same(wanted, result);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Auto_AggregatesAllFailures_WithoutConfusingUnsupportedAndFailed()
    {
        var first = new FakeMethod("a", () => ExtractionResult.Failed("a", "a", "transport error"));
        var second = new FakeMethod("b", () => ExtractionResult.Unsupported("b", "b", "no opcode"));
        var result = await new ExtractionOrchestrator([first, second]).RunAutoAsync(
            new FakeScsi((_, _) => throw new Exception()), Identity, Chipset, Progress, CancellationToken.None);
        Assert.Equal(ExtractionStatus.Failed, result.Status);
        Assert.Contains("transport error", result.Summary);
        Assert.Contains("no opcode", result.Summary);
    }

    [Fact]
    public void ExactVpdAndPioneerSignatures_WinOverRamTags()
    {
        var vpd = new byte[0x1000];
        "VPD_update_file"u8.CopyTo(vpd.AsSpan(0x400));
        AddRamTags(vpd);
        Assert.Equal(FirmwareFileKind.VpdImage, FirmwareFile.Analyze(vpd).Kind);
        byte[] pioneer = PioneerImage();
        AddRamTags(pioneer);
        "VPD_update_file"u8.CopyTo(pioneer.AsSpan(0x400));
        Assert.Equal(FirmwareFileKind.PioneerUpdate, FirmwareFile.Analyze(pioneer).Kind);
    }

    [Fact]
    public void UnknownHighEntropyFile_DoesNotAcquireFirmwareIdentityOrFlashability()
    {
        var data = new byte[0x20000];
        new Random(1234).NextBytes(data);
        var analysis = FirmwareFile.Analyze(data);
        Assert.Equal(FirmwareFileKind.Unknown, analysis.Kind);
        Assert.False(analysis.Vpd!.IsVpdUpdateImage);
        Assert.Null(analysis.Vpd.Model);
        Assert.Null(analysis.Vpd.Version);
        Assert.DoesNotContain("byte-exact flashable payload", analysis.Vpd.Describe());
        Assert.DoesNotContain("key resident", analysis.Vpd.Describe());
        Assert.DoesNotContain("real firmware content", DumpAnalyzer.Analyze("DVD CORPORATION"u8.ToArray()).Verdict());
    }

    [Fact]
    public void LegacyComposite_ExportsTheSamePrimaryCodeAsRawInput()
    {
        var raw = new byte[0x12000];
        new byte[] { 0xFF, 0x54, 0x54, 0x45 }.CopyTo(raw, 0);
        AddRamTags(raw);
        Array.Fill(raw, (byte)0x22, 0x1000, 0x10000);
        raw[0x1000] = 0x74;
        byte[] container = Composite(raw);
        var analysis = FirmwareFile.Analyze(container);
        Assert.Equal(FirmwareFileKind.Composite, analysis.Kind);
        var primary = analysis.Composite!.Primary!;
        Assert.Equal(raw, primary.Data);
        Assert.Equal(
            OpticalRamImage.BuildFlat8051Image(raw, OpticalRamImage.Parse(raw).CodeBank!),
            OpticalRamImage.BuildFlat8051Image(primary.Data, primary.Analysis.ControllerRam!.CodeBank!));
    }

    [Fact]
    public void Composite_RejectsTruncatedPayload()
        => Assert.Throws<InvalidDataException>(() => FirmwareFile.Analyze(Composite(new byte[4096])[..^1]));

    [Fact]
    public void RollbackFailure_PreservesAndReportsTheOriginalBackup()
    {
        using var temp = new TempDirectory();
        string bin = Path.Combine(temp.Path, "capture.bin");
        File.WriteAllBytes(bin, [9, 9]);
        var error = Assert.Throws<IOException>(() => DumpWriter.Write(bin,
            ExtractionResult.Ok("test", "test", [1, 2], "data", "done"), Identity, Chipset, [], DateTime.UtcNow,
            new FailedRecovery(bin)));
        string backup = Assert.Single(Directory.GetFiles(temp.Path, "*.bak"));
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(backup));
        Assert.Contains(backup, error.Message);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(0x08)]
    [InlineData(0x18)]
    [InlineData(0x02)]
    public void Accepted_RejectsNonGoodStatusWithMissingSense(int status)
        => Assert.False(StatusResult((byte)status, [], true).Accepted);

    [Fact]
    public void Accepted_AllowsOnlyGoodOrRecognizedRecoveredCheckCondition()
    {
        Assert.True(StatusResult(0, [], true).Accepted);
        Assert.True(StatusResult(2, [0x72, 1, 0, 0], true).Accepted);
        Assert.False(StatusResult(8, [0x72, 1, 0, 0], true).Accepted);
        Assert.False(StatusResult(2, [0x72, 5, 0x20, 0], true).Accepted);
        Assert.False(StatusResult(0, [], false).Accepted);
    }

    [Fact]
    public void TransportFailure_IsVisibleInStatusAndSavedMetadata()
    {
        var result = StatusResult(0, [], false);
        Assert.Contains("IOCTL FAILED", result.StatusText);
        using var temp = new TempDirectory();
        var command = new CommandLogEntry(DateTime.UtcNow, "12", ScsiDirection.In, 4, 0,
            0, 0, 0, 0, false, 5, result.StatusText, null);
        var files = DumpWriter.Write(Path.Combine(temp.Path, "dump.bin"),
            ExtractionResult.Partial("x", "x", [1], "data", "transport failed"), Identity, Chipset, [command], DateTime.UtcNow);
        using var json = JsonDocument.Parse(File.ReadAllText(files.SidecarPath));
        var saved = json.RootElement.GetProperty("commands")[0];
        Assert.False(saved.GetProperty("deviceIoOk").GetBoolean());
        Assert.Equal(5, saved.GetProperty("win32Error").GetInt32());
        Assert.Equal("Partial", json.RootElement.GetProperty("extraction").GetProperty("status").GetString());
    }

    [Fact]
    public void FailedLogSwitch_LeavesPreviousLogUsable()
    {
        using var temp = new TempDirectory();
        using var logger = new FileAndMemoryLogger();
        string log = Path.Combine(temp.Path, "first.log");
        logger.StartFile(log);
        Assert.ThrowsAny<IOException>(() => logger.StartFile(Path.Combine(temp.Path, "missing", "new.log")));
        logger.Info("still usable");
        logger.StartFile(Path.Combine(temp.Path, "second.log"));
        Assert.Contains("still usable", File.ReadAllText(log));
        logger.Info("replacement usable");
    }

    [Fact]
    public void LogFile_CanRestartAtTheSamePath()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "capture.log");
        using (var logger = new FileAndMemoryLogger())
        {
            logger.StartFile(path); logger.Info("old content");
            logger.StartFile(path); logger.Info("new content");
        }
        Assert.DoesNotContain("old content", File.ReadAllText(path));
        Assert.Contains("new content", File.ReadAllText(path));
    }

    [Fact]
    public void IdentityCheck_DetectsReplacementAtTheSameLetter()
    {
        Assert.True(DriveIdentifier.SameDevice(Identity, Identity));
        Assert.False(DriveIdentifier.SameDevice(Identity, Identity with { Serial = "NEW" }));
        Assert.False(DriveIdentifier.SameDevice(Identity, Identity with { Model = "NEW" }));
        Assert.False(DriveIdentifier.SameDevice(Identity, Identity with { Serial = null }));
    }

    [Fact]
    public void SpiRead_RevalidatesChipBeforeAnyReadCommand()
    {
        int calls = 0;
        var device = new FakeSpi(data => { calls++; Assert.Equal((byte)0x9F, data[0]); return [0, 0xEF, 0x40, 0x11]; });
        Assert.Throws<InvalidDataException>(() => SpiNorFlash.ReadAll(device, SpiChip(), null, null, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void SpiRead_CancellationDuringFinalTransferDiscardsResult()
    {
        using var cts = new CancellationTokenSource();
        int reads = 0;
        var device = new FakeSpi(data =>
        {
            if (data[0] == 0x9F) return [0, 0xEF, 0x40, 0x10];
            if (++reads == 16) cts.Cancel();
            return data;
        });
        Assert.Throws<OperationCanceledException>(() => SpiNorFlash.ReadAll(device, SpiChip(), null, null, cts.Token));
        Assert.Equal(16, reads);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public void Sfx_ValidatesStoredAndDeflatedExecutables(CompressionLevel level)
    {
        byte[] pe = PeImage(PioneerImage());
        byte[] zip = Zip(level, ("Updater.exe", pe));
        byte[] stub = PeImage(null);
        Assert.Equal(pe, ZipSfxExtractor.ExtractInnerExe(stub.Concat(zip).ToArray()));
    }

    [Fact]
    public void Sfx_SkipsAnUnrelatedExecutableToFindFirmware()
    {
        byte[] stub = PeImage(null);
        "RarSFX"u8.CopyTo(stub.AsSpan(0x1C0));
        byte[] data = stub.Concat(Zip(CompressionLevel.Optimal,
            ("helper.exe", PeImage(null)), ("Updater.exe", PeImage(PioneerImage())))).ToArray();
        Assert.Single(PioneerFirmwareImage.Parse(data).Parts);
    }

    [Fact]
    public void Sfx_RejectsDamagedPayloadAndInconsistentLocalLengths()
    {
        byte[] zip = Zip(CompressionLevel.NoCompression, ("Updater.exe", PeImage(PioneerImage())));
        int payload = 30 + U16(zip, 26) + U16(zip, 28);
        byte[] damaged = zip.ToArray();
        damaged[payload + 200] ^= 0x55;
        Assert.Null(ZipSfxExtractor.ExtractInnerExe(damaged));
        // Turn off the descriptor flag in both headers, then declare incompatible local sizes.
        byte[] inconsistent = zip.ToArray();
        int central = Find(inconsistent, [0x50, 0x4B, 0x01, 0x02]);
        W16(inconsistent, 6, 0); W16(inconsistent, central + 8, 0);
        W32(inconsistent, 18, 1);
        Assert.Null(ZipSfxExtractor.ExtractInnerExe(inconsistent));
    }

    [Fact]
    public void PeResources_EnforceEntryAndAllocationBudgets()
    {
        byte[] pe = PeImage(PioneerImage());
        Assert.Single(PeResourceReader.Read(pe));
        Assert.Throws<InvalidDataException>(() => PeResourceReader.Read(pe, maxTotalBytes: 16));
        Assert.Throws<InvalidDataException>(() => PeResourceReader.Read(pe, maxEntries: 2));
        Assert.Throws<InvalidDataException>(() => PeResourceReader.Read(pe, maxResources: 0));
        W32(pe, 0x260, 0x1F00); W32(pe, 0x264, 0x1000);
        Assert.Throws<InvalidDataException>(() => PeResourceReader.Read(pe));
    }

    [Fact]
    public void PeResources_DoNotRevisitSharedDirectories()
    {
        byte[] pe = PeImage(PioneerImage());
        W16(pe, 0x20E, 2);
        W32(pe, 0x218, 11); W32(pe, 0x21C, 0x80000020);
        Assert.Single(PeResourceReader.Read(pe));
    }

    [Fact]
    public void FileAnalysis_RejectsOversizedFilesBeforeAllocatingAndHonorsCancellation()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "large.bin");
        using (var file = File.Create(path)) file.SetLength(FirmwareFile.MaxInputBytes + 1L);
        Assert.Throws<InvalidDataException>(() => FirmwareFile.ReadFile(path));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => FirmwareFile.Analyze([1, 2], cts.Token));
        Assert.Throws<OperationCanceledException>(() => PeResourceReader.Read(PeImage(null), cts.Token));
    }

    private static ExtractionResult Extract(IFirmwareExtractionMethod method, IScsiDevice device, CancellationToken ct = default)
        => method.Extract(device, Identity, Chipset, Progress, ct);
    private static FakeScsi MemoryDevice(byte[] memory, bool cache, int transferCap = int.MaxValue) => new((cdb, data) =>
    {
        int offset = cache ? (int)BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(2, 4)) : ScsiCommand.ReadBe24(cdb, 3);
        int length = Math.Min(data!.Length, transferCap);
        Array.Copy(memory, offset, data, 0, length);
        return Response(cdb, data, length);
    });
    private static ScsiResult Response(byte[] cdb, byte[]? data, int count, bool good = true) => new()
    {
        Cdb = cdb, Direction = ScsiDirection.In, RequestedLength = data?.Length ?? 0,
        TransferredLength = count, ScsiStatus = (byte)(good ? 0 : 2), DeviceIoOk = true,
        SenseBytes = good ? [] : [0x72, 5, 0x24, 0], Data = data,
    };
    private static ScsiResult StatusResult(byte status, byte[] sense, bool ioOk) => new()
    {
        Cdb = [0x12], Direction = ScsiDirection.In, RequestedLength = 4, TransferredLength = 0,
        ScsiStatus = status, SenseBytes = sense, DeviceIoOk = ioOk, Win32Error = ioOk ? 0 : 5,
    };
    private static SpiFlashChip SpiChip() => new("chip", 0xEF, 0x40, 0x10, 65536, [0xEF, 0x40, 0x10], FlashVoltage.ThreeVolt);
    private static void AddRamTags(byte[] data) { "KEYPARA"u8.CopyTo(data.AsSpan(0x800)); "EXTRAINQ"u8.CopyTo(data.AsSpan(0x900)); }
    private static byte[] PioneerImage()
    {
        var data = new byte[0x1000];
        "********  Copyright(c) 2000 Pioneer"u8.CopyTo(data);
        return data;
    }
    private static byte[] Composite(byte[] primary)
    {
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("FirmwareStudio 0xF1 cache composite dump — 2 region(s) [v2]\r\nCreated: 2026-09-13T12:00:00.0000000Z\r\n\r\n"));
        foreach (var (name, bytes) in new[] { ("PRIMARY @0x00000000", primary), ("REGION @0x00100000", new byte[4096]) })
        {
            output.Write(Encoding.ASCII.GetBytes(($"=== {name} ===  size={bytes.Length}").PadRight(127) + "\n"));
            output.Write(bytes);
        }
        return output.ToArray();
    }
    private static byte[] PeImage(byte[]? payload)
    {
        var data = new byte[0x2000]; data[0] = (byte)'M'; data[1] = (byte)'Z';
        W32(data, 0x3C, 0x80); W32(data, 0x80, 0x4550); W16(data, 0x86, 1); W16(data, 0x94, 224);
        W16(data, 0x98, 0x10B); W32(data, 0xF4, 16);
        W32(data, 0x184, 0x1000); W32(data, 0x188, 0x1E00); W32(data, 0x18C, 0x200);
        if (payload is null) return data;
        W32(data, 0x108, 0x1000); W32(data, 0x10C, 0x1E00);
        W16(data, 0x20E, 1); W32(data, 0x210, 10); W32(data, 0x214, 0x80000020);
        W16(data, 0x22E, 1); W32(data, 0x230, 131); W32(data, 0x234, 0x80000040);
        W16(data, 0x24E, 1); W32(data, 0x250, 1033); W32(data, 0x254, 0x60);
        W32(data, 0x260, 0x1100); W32(data, 0x264, (uint)payload.Length); payload.CopyTo(data, 0x300);
        return data;
    }
    private static byte[] Zip(CompressionLevel level, params (string Name, byte[] Data)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var entry in entries) { using var stream = zip.CreateEntry(entry.Name, level).Open(); stream.Write(entry.Data); }
        return output.ToArray();
    }
    private static int Find(byte[] data, byte[] pattern) => data.AsSpan().IndexOf(pattern);
    private static ushort U16(byte[] data, int p) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));
    private static void W16(byte[] data, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(p), value);
    private static void W32(byte[] data, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p), value);
    private sealed class FakeScsi(Func<byte[], byte[]?, ScsiResult> handler) : IScsiDevice
    {
        public char DriveLetter => 'D';
        public ScsiResult SendCommand(byte[] cdb, ScsiDirection dir, byte[]? data, int timeoutSec = 15, string? note = null) => handler(cdb, data);
    }
    private sealed class FakeSpi(Func<byte[], byte[]> handler) : ISpiDevice { public byte[] SpiTransfer(byte[] data) => handler(data); }
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T> { public void Report(T value) => handler(value); }
    private sealed class FakeMethod(string id, Func<ExtractionResult> extract) : IFirmwareExtractionMethod
    {
        public int Calls { get; private set; }
        public string Id => id; public string DisplayName => id; public string Description => id;
        public MethodApplicability Evaluate(DriveIdentity identity, ChipsetInfo chipset) => MethodApplicability.Yes("test");
        public ExtractionResult Extract(IScsiDevice device, DriveIdentity identity, ChipsetInfo chipset, IProgress<ExtractionProgress> progress, CancellationToken ct)
        { Calls++; return extract(); }
    }
    private sealed class FailedRecovery(string bin) : FilePromotion
    {
        public override void Move(string source, string destination, bool overwrite = false)
        {
            if (destination.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new IOException("Sidecar promotion failed.");
            base.Move(source, destination, overwrite);
        }
        public override void Delete(string path)
        {
            if (path == bin) throw new IOException("Replacement is locked during recovery.");
            base.Delete(path);
        }
    }
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FirmwareStudio.Tests", Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}

using System.Text;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Extraction;

/// <summary>Flash-size class of a Renesas/NEC drive — selects the address range(s) that hold the firmware.</summary>
public enum NecFlashType
{
    Size640K, Size768K, Size1M, Size2M,
    Size2MOpti, Size2MOpti2, Size2MOpti3, Size2MOpti4,
    Size2MOptiBD, Size2MOptiBD2, Size2MHDDVD, Invalid,
}

/// <summary>
/// Drive-identification and flash-layout data for Renesas/NEC-family optical drives (NEC ND-*, Optiarc
/// AD-* incl. Blu-ray/HD-DVD, and NEC-based Lite-On iHAS*/DH24A*). Ported from <b>binflash</b>
/// (Liggy/binflash: <c>necinternal.cpp</c> <c>IdentifyDrive</c> / <c>GetFlashSizeFromDriveType</c> and
/// <c>necinterface.cpp</c> <c>DumpFirmware</c>). The <see cref="Signatures"/> table was generated directly
/// from that source to avoid transcription error.
///
/// <para><b>Read-only.</b> Identification reads 4-byte firmware signatures via <c>ReadRAM</c> (opcode 0xCC,
/// data-in). None of binflash's write/erase/safe-mode paths are represented here. Drives whose read binflash
/// gates behind "safe mode" (a 0xCC state-change command) are dumped best-effort <i>without</i> it — this
/// tool never issues that command — and the method reports if a region could not be read as a result.</para>
/// </summary>
public static class NecDriveTable
{
    /// <summary>A flash region to read: <c>[Start, End)</c> on the given bank.</summary>
    public readonly record struct FlashRegion(uint Start, uint End, bool Bank);

    /// <summary>
    /// A firmware signature: if the 4 bytes at <see cref="ProbeAddress"/> equal <see cref="Code"/>, the drive
    /// is <see cref="Model"/> with flash class <see cref="Flash"/>.
    /// </summary>
    public readonly record struct Signature(uint ProbeAddress, string Code, NecFlashType Flash, string Model);

    /// <summary>Result of probing a drive for the NEC read command and identifying it.</summary>
    public sealed class IdentifyResult
    {
        public Signature? Match { get; init; }
        public bool AnyAccepted { get; init; }
        public List<(uint Addr, string Code)> Reads { get; } = new();
    }

    /// <summary>Distinct signature-probe addresses, in binflash's identification order.</summary>
    public static readonly uint[] ProbeAddresses = { 0x4500, 0x4800, 0x10800, 0x7000, 0x5000 };

    /// <summary>(probeAddress, 4-byte code) → (flash class, model code). Generated from binflash source.</summary>
    public static readonly Signature[] Signatures =
    {
        new(0x4500, "K100", NecFlashType.Size640K, "K1"),
        new(0x4500, "K111", NecFlashType.Size640K, "K1"),
        new(0x4500, "K110", NecFlashType.Size640K, "K1"),
        new(0x4500, "K130", NecFlashType.Size640K, "K1"),
        new(0x4500, "A110", NecFlashType.Size640K, "K1"),
        new(0x4500, "L110", NecFlashType.Size640K, "L1"),
        new(0x4500, "L130", NecFlashType.Size640K, "L1"),
        new(0x4500, "L11R", NecFlashType.Size640K, "L1"),
        new(0x4500, "L13R", NecFlashType.Size640K, "L1"),
        new(0x4500, "L210", NecFlashType.Size768K, "L2"),
        new(0x4500, "L250", NecFlashType.Size768K, "L2"),
        new(0x4500, "L245", NecFlashType.Size768K, "L2"),
        new(0x4500, "L21R", NecFlashType.Size768K, "L2"),
        new(0x4500, "L25R", NecFlashType.Size768K, "L2"),
        new(0x4800, "K210", NecFlashType.Size1M, "K2"),
        new(0x4800, "K250", NecFlashType.Size1M, "K2"),
        new(0x4800, "K251", NecFlashType.Size1M, "K2"),
        new(0x4800, "K310", NecFlashType.Size1M, "K350"),
        new(0x4800, "K350", NecFlashType.Size1M, "K350"),
        new(0x4800, "K345", NecFlashType.Size1M, "K350"),
        new(0x4800, "K312", NecFlashType.Size1M, "K352"),
        new(0x4800, "K352", NecFlashType.Size1M, "K352"),
        new(0x4800, "K313", NecFlashType.Size1M, "K353"),
        new(0x4800, "K353", NecFlashType.Size1M, "K353"),
        new(0x4800, "K314", NecFlashType.Size1M, "K354"),
        new(0x4800, "K354", NecFlashType.Size1M, "K354"),
        new(0x4800, "L211", NecFlashType.Size1M, "L251"),
        new(0x4800, "L251", NecFlashType.Size1M, "L251"),
        new(0x4800, "LY1R", NecFlashType.Size1M, "L251"),
        new(0x4800, "LY5R", NecFlashType.Size1M, "L251"),
        new(0x4800, "K315", NecFlashType.Size2M, "K355"),
        new(0x4800, "K355", NecFlashType.Size2M, "K355"),
        new(0x4800, "D315", NecFlashType.Size2M, "D355"),
        new(0x4800, "D355", NecFlashType.Size2M, "D355"),
        new(0x4800, "K316", NecFlashType.Size2M, "K356"),
        new(0x4800, "K356", NecFlashType.Size2M, "K356"),
        new(0x4800, "K450", NecFlashType.Size2M, "K450"),
        new(0x4800, "K410", NecFlashType.Size2M, "K450"),
        new(0x4800, "K451", NecFlashType.Size2M, "K451"),
        new(0x4800, "K411", NecFlashType.Size2M, "K451"),
        new(0x4800, "L212", NecFlashType.Size2M, "L252"),
        new(0x4800, "L252", NecFlashType.Size2M, "L252"),
        new(0x4800, "L213", NecFlashType.Size2M, "L253"),
        new(0x4800, "L253", NecFlashType.Size2M, "L253"),
        new(0x4800, "L350", NecFlashType.Size2M, "L350"),
        new(0x4800, "L310", NecFlashType.Size2M, "L350"),
        new(0x4800, "L351", NecFlashType.Size2M, "L351"),
        new(0x4800, "L311", NecFlashType.Size2M, "L351"),
        new(0x4800, "K365", NecFlashType.Size2M, "K365"),
        new(0x4800, "K325", NecFlashType.Size2M, "K365"),
        new(0x4800, "K366", NecFlashType.Size2M, "K366"),
        new(0x4800, "K326", NecFlashType.Size2M, "K366"),
        new(0x4800, "K370", NecFlashType.Size2M, "K370"),
        new(0x4800, "K330", NecFlashType.Size2M, "K370"),
        new(0x4800, "K371", NecFlashType.Size2M, "K371"),
        new(0x4800, "K331", NecFlashType.Size2M, "K371"),
        new(0x4800, "K460", NecFlashType.Size2M, "K460"),
        new(0x4800, "K420", NecFlashType.Size2M, "K460"),
        new(0x4800, "K461", NecFlashType.Size2M, "K461"),
        new(0x4800, "K421", NecFlashType.Size2M, "K461"),
        new(0x4800, "K470", NecFlashType.Size2M, "K470"),
        new(0x4800, "K430", NecFlashType.Size2M, "K470"),
        new(0x4800, "K471", NecFlashType.Size2M, "K471"),
        new(0x4800, "K431", NecFlashType.Size2M, "K471"),
        new(0x4800, "D570", NecFlashType.Size2MOpti, "D570"),
        new(0x4800, "D530", NecFlashType.Size2MOpti, "D570"),
        new(0x4800, "K380", NecFlashType.Size2MOpti, "D570"),
        new(0x4800, "K340", NecFlashType.Size2MOpti, "D570"),
        new(0x4800, "D57D", NecFlashType.Size2MOpti, "D57D"),
        new(0x4800, "D53D", NecFlashType.Size2MOpti, "D57D"),
        new(0x4800, "K38D", NecFlashType.Size2MOpti, "D57D"),
        new(0x4800, "K34D", NecFlashType.Size2MOpti, "D57D"),
        new(0x4800, "D573", NecFlashType.Size2MOpti, "D573"),
        new(0x4800, "D533", NecFlashType.Size2MOpti, "D573"),
        new(0x4800, "K381", NecFlashType.Size2MOpti, "D573"),
        new(0x4800, "K341", NecFlashType.Size2MOpti, "D573"),
        new(0x4800, "D770", NecFlashType.Size2MOpti, "D770"),
        new(0x4800, "D730", NecFlashType.Size2MOpti, "D770"),
        new(0x4800, "K480", NecFlashType.Size2MOpti, "D770"),
        new(0x4800, "K440", NecFlashType.Size2MOpti, "D770"),
        new(0x4800, "D773", NecFlashType.Size2MOpti, "D773"),
        new(0x4800, "D733", NecFlashType.Size2MOpti, "D773"),
        new(0x4800, "K481", NecFlashType.Size2MOpti, "D773"),
        new(0x4800, "K441", NecFlashType.Size2MOpti, "D773"),
        new(0x4800, "L260", NecFlashType.Size2MOpti, "L260"),
        new(0x4800, "L220", NecFlashType.Size2MOpti, "L260"),
        new(0x4800, "L261", NecFlashType.Size2MOpti, "L261"),
        new(0x4800, "L221", NecFlashType.Size2MOpti, "L261"),
        new(0x4800, "L450", NecFlashType.Size2MOpti, "L450"),
        new(0x4800, "L410", NecFlashType.Size2MOpti, "L450"),
        new(0x4800, "L451", NecFlashType.Size2MOpti, "L451"),
        new(0x4800, "L411", NecFlashType.Size2MOpti, "L451"),
        new(0x4800, "Q175", NecFlashType.Size2MOpti, "Q175"),
        new(0x4800, "Q135", NecFlashType.Size2MOpti, "Q175"),
        new(0x4800, "Q178", NecFlashType.Size2MOpti, "Q178"),
        new(0x4800, "Q138", NecFlashType.Size2MOpti, "Q178"),
        new(0x4800, "G175", NecFlashType.Size2MOpti, "G175"),
        new(0x4800, "G135", NecFlashType.Size2MOpti, "G175"),
        new(0x4800, "G178", NecFlashType.Size2MOpti, "G178"),
        new(0x4800, "G138", NecFlashType.Size2MOpti, "G178"),
        new(0x4800, "Q630", NecFlashType.Size2MOpti, "Q630"),
        new(0x4800, "Q633", NecFlashType.Size2MOpti, "Q633"),
        new(0x4800, "G630", NecFlashType.Size2MOpti, "G630"),
        new(0x4800, "G633", NecFlashType.Size2MOpti, "G633"),
        new(0x4800, "Q200", NecFlashType.Size2MOpti2, "Q200"),
        new(0x4800, "Q201", NecFlashType.Size2MOpti2, "Q201"),
        new(0x4800, "Q203", NecFlashType.Size2MOpti2, "Q203"),
        new(0x4800, "Q205", NecFlashType.Size2MOpti2, "Q205"),
        new(0x4800, "Q206", NecFlashType.Size2MOpti2, "Q206"),
        new(0x4800, "Q208", NecFlashType.Size2MOpti2, "Q208"),
        new(0x4800, "G200", NecFlashType.Size2MOpti2, "G200"),
        new(0x4800, "G201", NecFlashType.Size2MOpti2, "G201"),
        new(0x4800, "G203", NecFlashType.Size2MOpti2, "G203"),
        new(0x4800, "G205", NecFlashType.Size2MOpti2, "G205"),
        new(0x4800, "G206", NecFlashType.Size2MOpti2, "G206"),
        new(0x4800, "G208", NecFlashType.Size2MOpti2, "G208"),
        new(0x4800, "Q590", NecFlashType.Size2MOpti2, "Q590"),
        new(0x4800, "Q591", NecFlashType.Size2MOpti2, "Q591"),
        new(0x4800, "Q593", NecFlashType.Size2MOpti2, "Q593"),
        new(0x4800, "Q595", NecFlashType.Size2MOpti2, "Q595"),
        new(0x4800, "Q596", NecFlashType.Size2MOpti2, "Q596"),
        new(0x4800, "Q598", NecFlashType.Size2MOpti2, "Q598"),
        new(0x4800, "G590", NecFlashType.Size2MOpti2, "G590"),
        new(0x4800, "G591", NecFlashType.Size2MOpti2, "G591"),
        new(0x4800, "G593", NecFlashType.Size2MOpti2, "G593"),
        new(0x4800, "G595", NecFlashType.Size2MOpti2, "G595"),
        new(0x4800, "G596", NecFlashType.Size2MOpti2, "G596"),
        new(0x4800, "G598", NecFlashType.Size2MOpti2, "G598"),
        new(0x4800, "Q670", NecFlashType.Size2MOpti2, "Q670"),
        new(0x4800, "Q673", NecFlashType.Size2MOpti2, "Q673"),
        new(0x4800, "Q675", NecFlashType.Size2MOpti2, "Q675"),
        new(0x4800, "Q678", NecFlashType.Size2MOpti2, "Q678"),
        new(0x4800, "G670", NecFlashType.Size2MOpti2, "G670"),
        new(0x4800, "G673", NecFlashType.Size2MOpti2, "G673"),
        new(0x4800, "G675", NecFlashType.Size2MOpti2, "G675"),
        new(0x4800, "G678", NecFlashType.Size2MOpti2, "G678"),
        new(0x4800, "Q910", NecFlashType.Size2MOpti, "Q910"),
        new(0x4800, "Q913", NecFlashType.Size2MOpti, "Q913"),
        new(0x4800, "Q915", NecFlashType.Size2MOpti, "Q915"),
        new(0x4800, "Q918", NecFlashType.Size2MOpti, "Q918"),
        new(0x4800, "G910", NecFlashType.Size2MOpti, "G910"),
        new(0x4800, "G913", NecFlashType.Size2MOpti, "G913"),
        new(0x4800, "G915", NecFlashType.Size2MOpti, "G915"),
        new(0x4800, "G918", NecFlashType.Size2MOpti, "G918"),
        new(0x4800, "Q960", NecFlashType.Size2MOpti, "Q960"),
        new(0x4800, "Q963", NecFlashType.Size2MOpti, "Q963"),
        new(0x4800, "Q965", NecFlashType.Size2MOpti, "Q965"),
        new(0x4800, "Q968", NecFlashType.Size2MOpti, "Q968"),
        new(0x4800, "G960", NecFlashType.Size2MOpti, "G960"),
        new(0x4800, "G963", NecFlashType.Size2MOpti, "G963"),
        new(0x4800, "G965", NecFlashType.Size2MOpti, "G965"),
        new(0x4800, "G968", NecFlashType.Size2MOpti, "G968"),
        new(0x10800, "Q230", NecFlashType.Size2MOpti3, "Q230"),
        new(0x10800, "Q231", NecFlashType.Size2MOpti3, "Q231"),
        new(0x10800, "Q233", NecFlashType.Size2MOpti3, "Q233"),
        new(0x10800, "Q235", NecFlashType.Size2MOpti3, "Q235"),
        new(0x10800, "Q236", NecFlashType.Size2MOpti3, "Q236"),
        new(0x10800, "Q238", NecFlashType.Size2MOpti3, "Q238"),
        new(0x10800, "G230", NecFlashType.Size2MOpti3, "G230"),
        new(0x10800, "G231", NecFlashType.Size2MOpti3, "G231"),
        new(0x10800, "G233", NecFlashType.Size2MOpti3, "G233"),
        new(0x10800, "G235", NecFlashType.Size2MOpti3, "G235"),
        new(0x10800, "G236", NecFlashType.Size2MOpti3, "G236"),
        new(0x10800, "G238", NecFlashType.Size2MOpti3, "G238"),
        new(0x10800, "Q240", NecFlashType.Size2MOpti3, "Q240"),
        new(0x10800, "Q241", NecFlashType.Size2MOpti3, "Q241"),
        new(0x10800, "Q243", NecFlashType.Size2MOpti3, "Q243"),
        new(0x10800, "Q245", NecFlashType.Size2MOpti3, "Q245"),
        new(0x10800, "Q246", NecFlashType.Size2MOpti3, "Q246"),
        new(0x10800, "Q248", NecFlashType.Size2MOpti3, "Q248"),
        new(0x10800, "G240", NecFlashType.Size2MOpti3, "G240"),
        new(0x10800, "G241", NecFlashType.Size2MOpti3, "G241"),
        new(0x10800, "G243", NecFlashType.Size2MOpti3, "G243"),
        new(0x10800, "G245", NecFlashType.Size2MOpti3, "G245"),   // LiteOn iHAS324 Y
        new(0x10800, "P245", NecFlashType.Size2MOpti3, "G245"),   // LiteOn iHAS324 Y
        new(0x10800, "G246", NecFlashType.Size2MOpti3, "G246"),   // LiteOn iHAS424 Y
        new(0x10800, "P246", NecFlashType.Size2MOpti3, "G246"),   // LiteOn iHAS424 Y
        new(0x10800, "G248", NecFlashType.Size2MOpti3, "G248"),
        new(0x10800, "P248", NecFlashType.Size2MOpti3, "G248"),
        new(0x10800, "G25A", NecFlashType.Size2MOpti3, "G25A"),
        new(0x10800, "G25B", NecFlashType.Size2MOpti3, "G25B"),
        new(0x10800, "G25D", NecFlashType.Size2MOpti3, "G25D"),
        new(0x10800, "Q260", NecFlashType.Size2MOpti3, "Q260"),
        new(0x10800, "Q261", NecFlashType.Size2MOpti3, "Q261"),
        new(0x10800, "Q263", NecFlashType.Size2MOpti3, "Q263"),
        new(0x10800, "Q265", NecFlashType.Size2MOpti3, "Q265"),
        new(0x10800, "Q266", NecFlashType.Size2MOpti3, "Q266"),
        new(0x10800, "Q268", NecFlashType.Size2MOpti3, "Q268"),
        new(0x10800, "G260", NecFlashType.Size2MOpti3, "G260"),
        new(0x10800, "G261", NecFlashType.Size2MOpti3, "G261"),
        new(0x10800, "G263", NecFlashType.Size2MOpti3, "G263"),
        new(0x10800, "G265", NecFlashType.Size2MOpti3, "G265"),
        new(0x10800, "G266", NecFlashType.Size2MOpti3, "G266"),
        new(0x10800, "G268", NecFlashType.Size2MOpti3, "G268"),
        new(0x10800, "G27A", NecFlashType.Size2MOpti4, "G27A"),
        new(0x10800, "G27B", NecFlashType.Size2MOpti4, "G27B"),
        new(0x10800, "G27D", NecFlashType.Size2MOpti4, "G27D"),
        new(0x10800, "Q280", NecFlashType.Size2MOpti4, "Q280"),
        new(0x10800, "Q281", NecFlashType.Size2MOpti4, "Q281"),
        new(0x10800, "Q283", NecFlashType.Size2MOpti4, "Q283"),
        new(0x10800, "Q285", NecFlashType.Size2MOpti4, "Q285"),
        new(0x10800, "Q286", NecFlashType.Size2MOpti4, "Q286"),
        new(0x10800, "Q288", NecFlashType.Size2MOpti4, "Q288"),
        new(0x10800, "G280", NecFlashType.Size2MOpti4, "G280"),
        new(0x10800, "G281", NecFlashType.Size2MOpti4, "G281"),
        new(0x10800, "G283", NecFlashType.Size2MOpti4, "G283"),
        new(0x10800, "G285", NecFlashType.Size2MOpti4, "G285"),
        new(0x10800, "G286", NecFlashType.Size2MOpti4, "G286"),
        new(0x10800, "G288", NecFlashType.Size2MOpti4, "G288"),
        new(0x10800, "G29A", NecFlashType.Size2MOpti4, "G29A"),
        new(0x10800, "G29B", NecFlashType.Size2MOpti4, "G29B"),
        new(0x10800, "G29D", NecFlashType.Size2MOpti4, "G29D"),
        new(0x10800, "Q300", NecFlashType.Size2MOpti4, "Q300"),
        new(0x10800, "Q301", NecFlashType.Size2MOpti4, "Q301"),
        new(0x10800, "Q303", NecFlashType.Size2MOpti4, "Q303"),
        new(0x10800, "Q305", NecFlashType.Size2MOpti4, "Q305"),
        new(0x10800, "Q306", NecFlashType.Size2MOpti4, "Q306"),
        new(0x10800, "Q308", NecFlashType.Size2MOpti4, "Q308"),
        new(0x10800, "G300", NecFlashType.Size2MOpti4, "G300"),
        new(0x10800, "G301", NecFlashType.Size2MOpti4, "G301"),
        new(0x10800, "G303", NecFlashType.Size2MOpti4, "G303"),
        new(0x10800, "G305", NecFlashType.Size2MOpti4, "G305"),
        new(0x10800, "G306", NecFlashType.Size2MOpti4, "G306"),
        new(0x10800, "G308", NecFlashType.Size2MOpti4, "G308"),
        new(0x10800, "Q325", NecFlashType.Size2MOpti4, "Q325"),
        new(0x10800, "Q326", NecFlashType.Size2MOpti4, "Q326"),
        new(0x10800, "Q328", NecFlashType.Size2MOpti4, "Q328"),
        new(0x10800, "G325", NecFlashType.Size2MOpti4, "G325"),
        new(0x10800, "G326", NecFlashType.Size2MOpti4, "G326"),
        new(0x10800, "G328", NecFlashType.Size2MOpti4, "G328"),
        new(0x10800, "Q700", NecFlashType.Size2MOpti3, "Q700"),
        new(0x10800, "Q701", NecFlashType.Size2MOpti3, "Q701"),
        new(0x10800, "Q703", NecFlashType.Size2MOpti3, "Q703"),
        new(0x10800, "Q705", NecFlashType.Size2MOpti3, "Q705"),
        new(0x10800, "Q706", NecFlashType.Size2MOpti3, "Q706"),
        new(0x10800, "Q708", NecFlashType.Size2MOpti3, "Q708"),
        new(0x10800, "Q70A", NecFlashType.Size2MOpti3, "Q70A"),
        new(0x10800, "Q70B", NecFlashType.Size2MOpti3, "Q70B"),
        new(0x10800, "Q70D", NecFlashType.Size2MOpti3, "Q70D"),
        new(0x10800, "G700", NecFlashType.Size2MOpti3, "G700"),
        new(0x10800, "G701", NecFlashType.Size2MOpti3, "G701"),
        new(0x10800, "G703", NecFlashType.Size2MOpti3, "G703"),
        new(0x10800, "G705", NecFlashType.Size2MOpti3, "G705"),
        new(0x10800, "G706", NecFlashType.Size2MOpti3, "G706"),
        new(0x10800, "G708", NecFlashType.Size2MOpti3, "G708"),
        new(0x10800, "G70A", NecFlashType.Size2MOpti3, "G70A"),
        new(0x10800, "G70B", NecFlashType.Size2MOpti3, "G70B"),
        new(0x10800, "G70D", NecFlashType.Size2MOpti3, "G70D"),
        new(0x10800, "Q800", NecFlashType.Size2MOpti4, "Q800"),
        new(0x10800, "Q801", NecFlashType.Size2MOpti4, "Q801"),
        new(0x10800, "Q803", NecFlashType.Size2MOpti4, "Q803"),
        new(0x10800, "Q805", NecFlashType.Size2MOpti4, "Q805"),
        new(0x10800, "Q806", NecFlashType.Size2MOpti4, "Q806"),
        new(0x10800, "Q808", NecFlashType.Size2MOpti4, "Q808"),
        new(0x10800, "Q80A", NecFlashType.Size2MOpti4, "Q80A"),
        new(0x10800, "Q80B", NecFlashType.Size2MOpti4, "Q80B"),
        new(0x10800, "Q80D", NecFlashType.Size2MOpti4, "Q80D"),
        new(0x10800, "G800", NecFlashType.Size2MOpti4, "G800"),
        new(0x10800, "G801", NecFlashType.Size2MOpti4, "G801"),
        new(0x10800, "G803", NecFlashType.Size2MOpti4, "G803"),
        new(0x10800, "G805", NecFlashType.Size2MOpti4, "G805"),
        new(0x10800, "G806", NecFlashType.Size2MOpti4, "G806"),
        new(0x10800, "G808", NecFlashType.Size2MOpti4, "G808"),
        new(0x10800, "G80A", NecFlashType.Size2MOpti4, "G80A"),
        new(0x10800, "G80B", NecFlashType.Size2MOpti4, "G80B"),
        new(0x10800, "G80D", NecFlashType.Size2MOpti4, "G80D"),
        new(0x10800, "Q68A", NecFlashType.Size2MOpti3, "Q68A"),
        new(0x10800, "Q68B", NecFlashType.Size2MOpti3, "Q68B"),
        new(0x10800, "Q68D", NecFlashType.Size2MOpti3, "Q68D"),
        new(0x10800, "Q97A", NecFlashType.Size2MOpti3, "Q97A"),
        new(0x10800, "Q97B", NecFlashType.Size2MOpti3, "Q97B"),
        new(0x10800, "Q97D", NecFlashType.Size2MOpti3, "Q97D"),
        new(0x10800, "G97A", NecFlashType.Size2MOpti3, "G97A"),
        new(0x10800, "G97B", NecFlashType.Size2MOpti3, "G97B"),
        new(0x10800, "G97D", NecFlashType.Size2MOpti3, "G97D"),
        new(0x10800, "Q69A", NecFlashType.Size2MOpti3, "Q69A"),
        new(0x10800, "Q69B", NecFlashType.Size2MOpti3, "Q69B"),
        new(0x10800, "Q69D", NecFlashType.Size2MOpti3, "Q69D"),
        new(0x10800, "G69A", NecFlashType.Size2MOpti3, "G69A"),
        new(0x10800, "G69B", NecFlashType.Size2MOpti3, "G69B"),
        new(0x10800, "G69D", NecFlashType.Size2MOpti3, "G69D"),
        new(0x10800, "G71A", NecFlashType.Size2MOpti3, "G71A"),
        new(0x10800, "G71B", NecFlashType.Size2MOpti3, "G71B"),
        new(0x10800, "G71D", NecFlashType.Size2MOpti3, "G71D"),
        new(0x10800, "G72A", NecFlashType.Size2MOpti3, "G72A"),
        new(0x10800, "G72B", NecFlashType.Size2MOpti3, "G72B"),
        new(0x10800, "G72D", NecFlashType.Size2MOpti3, "G72D"),
        new(0x10800, "G74A", NecFlashType.Size2MOpti4, "G74A"),
        new(0x10800, "G74B", NecFlashType.Size2MOpti4, "G74B"),
        new(0x10800, "G74D", NecFlashType.Size2MOpti4, "G74D"),
        new(0x10800, "G76A", NecFlashType.Size2MOpti4, "G76A"),
        new(0x10800, "G76B", NecFlashType.Size2MOpti4, "G76B"),
        new(0x10800, "G76D", NecFlashType.Size2MOpti4, "G76D"),
        new(0x10800, "G98A", NecFlashType.Size2MOpti4, "G98A"),
        new(0x10800, "G98B", NecFlashType.Size2MOpti4, "G98B"),
        new(0x10800, "G98D", NecFlashType.Size2MOpti4, "G98D"),
        new(0x10800, "G93A", NecFlashType.Size2MOpti4, "G93A"),
        new(0x10800, "G93B", NecFlashType.Size2MOpti4, "G93B"),
        new(0x10800, "G93D", NecFlashType.Size2MOpti4, "G93D"),
        new(0x7000, "C500", NecFlashType.Size2MOptiBD, "C500"),
        new(0x7000, "C505", NecFlashType.Size2MOptiBD, "C505"),
        new(0x7000, "C600", NecFlashType.Size2MOptiBD, "C600"),
        new(0x7000, "C605", NecFlashType.Size2MOptiBD, "C605"),
        new(0x7000, "C64A", NecFlashType.Size2MOptiBD2, "C64A"),
        new(0x7000, "B530", NecFlashType.Size2MOptiBD2, "B530"),
        new(0x7000, "B535", NecFlashType.Size2MOptiBD2, "B535"),
        new(0x7000, "B74A", NecFlashType.Size2MOptiBD2, "B74A"),
        new(0x5000, "M110", NecFlashType.Size2MHDDVD, "M110"),
    };

    /// <summary>
    /// The dump region(s) for a flash class (from binflash <c>DumpFirmware</c>). An empty result means
    /// binflash does not support dumping that class (Blu-ray / invalid).
    /// </summary>
    public static FlashRegion[] RegionsFor(NecFlashType ft) => ft switch
    {
        NecFlashType.Size640K => new[] { new FlashRegion(0x5000, 0x80000, false), new FlashRegion(0x240000, 0x260000, true) },
        NecFlashType.Size768K => new[] { new FlashRegion(0x8000, 0xC0000, false) },
        NecFlashType.Size1M => new[] { new FlashRegion(0x6000, 0x100000, false) },
        NecFlashType.Size2M => new[] { new FlashRegion(0x10000, 0x1F0000, false) },
        NecFlashType.Size2MOpti => new[] { new FlashRegion(0x30000, 0x1F0000, false) },
        NecFlashType.Size2MOpti2 => new[] { new FlashRegion(0x30000, 0x1F0000, false) },
        NecFlashType.Size2MOpti3 => new[] { new FlashRegion(0x30000, 0x1E0000, false) },
        NecFlashType.Size2MOpti4 => new[] { new FlashRegion(0x30000, 0x1E0000, false) },
        NecFlashType.Size2MHDDVD => new[] { new FlashRegion(0x40000, 0x200000, false) },
        _ => Array.Empty<FlashRegion>(),   // BD / BD2 / Invalid — no firmware dump in binflash
    };

    /// <summary>
    /// Whether the read must carry a firmware id (and where to read that id byte), mirroring binflash's
    /// <c>GetIDFromDriveType</c>. K370/K371/K470/K471 key on a byte at 0x10000; all Optiarc classes key on a
    /// byte at 0x30000; everything else needs no id. (binflash also enters "safe mode" for id drives — this
    /// tool does not, and reports if a region can't be read as a result.)
    /// </summary>
    public static (bool NeedsId, uint IdAddress) IdInfo(Signature s)
    {
        if (s.Model is "K370" or "K371" or "K470" or "K471") return (true, 0x10000);
        return s.Flash switch
        {
            NecFlashType.Size2MOpti or NecFlashType.Size2MOpti2 or
            NecFlashType.Size2MOpti3 or NecFlashType.Size2MOpti4 => (true, 0x30000),
            _ => (false, 0),
        };
    }

    /// <summary>
    /// Probes the drive for the NEC read command by reading the 4-byte firmware signatures at the probe
    /// addresses via <c>ReadRAM</c> (0xCC) and matching them. Read-only.
    /// </summary>
    public static IdentifyResult Identify(ScsiDevice device)
    {
        Signature? match = null;
        bool anyAccepted = false;
        var reads = new List<(uint Addr, string Code)>();

        foreach (uint addr in ProbeAddresses)
        {
            var r = device.SendCommand(ScsiCommand.NecReadRam(false, addr, 0x20), ScsiDirection.In,
                new byte[0x20], note: $"NEC ReadRAM 0xCC identify off=0x{addr:X}");
            if (!r.Good || r.Data is null || r.Data.Length < 4) continue;
            anyAccepted = true;
            string code = Encoding.ASCII.GetString(r.Data, 0, 4);
            reads.Add((addr, code));
            if (match is null)
                foreach (var s in Signatures)
                    if (s.ProbeAddress == addr && s.Code == code) { match = s; break; }
        }

        var result = new IdentifyResult { Match = match, AnyAccepted = anyAccepted };
        result.Reads.AddRange(reads);
        return result;
    }
}

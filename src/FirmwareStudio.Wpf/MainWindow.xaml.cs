using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using FirmwareStudio.Core.Drives;
using FirmwareStudio.Core.Extraction;
using FirmwareStudio.Core.Hardware;
using FirmwareStudio.Core.Logging;
using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;
using FirmwareStudio.Wpf.Services;

namespace FirmwareStudio.Wpf;

public partial class MainWindow : Window
{
    private readonly ExtractionOrchestrator _orch = new();
    private readonly FileAndMemoryLogger _logger = new();
    private DriveIdentity? _id;
    private ChipsetInfo? _chip;
    private ExtractionResult? _lastResult;
    private CancellationTokenSource? _cts;

    // Hardware (SPI flash) tab state.
    private SpiFlashChip? _hwChip;
    private byte[]? _hwDump;
    private readonly List<string> _hwLog = new();
    private CancellationTokenSource? _hwCts;

    private sealed record MethodChoice(string Label, string? Id);

    public MainWindow()
    {
        InitializeComponent();
        _logger.CommandLogged += e => Dispatcher.BeginInvoke(() => AppendLog(e.Text));
        _logger.InfoLogged += m => Dispatcher.BeginInvoke(() => AppendLog("# " + m));

        MethodCombo.ItemsSource = new[]
        {
            new MethodChoice("Auto (recommended)", null),
            new MethodChoice("Universal READ BUFFER probe", "universal"),
            new MethodChoice("MediaTek/Lite-On flash read (READ BUFFER 0x3C mode 6)", "mtk-flash"),
            new MethodChoice("MediaTek internal cache read (0xF1)", "mediatek"),
            new MethodChoice("PLDS/Lite-On vendor read (0xDF)", "plds"),
            new MethodChoice("NEC/Renesas RAM read (0xCC, binflash)", "nec"),
            new MethodChoice("UHD Blu-ray service mode (experimental)", "uhd-svc"),
        };
        MethodCombo.SelectedIndex = 0;

        // Right-click Copy / Select all / Copy all on every read-only console pane. (Mouse-drag select and
        // Ctrl+A / Ctrl+C already work on these boxes; this makes copying discoverable and one-click.)
        AttachCopyMenu(LogBox);
        AttachCopyMenu(HexBox);
        AttachCopyMenu(HwLogBox);
        AttachCopyMenu(HwHexBox);

        // The identity / chipset / result fields in both extract panels are read-only TextBoxes (not
        // TextBlocks) so their text can be selected and copied — same Copy menu, plus wheel-forwarding.
        foreach (var box in new[]
        {
            VendorText, ModelText, FwText, SerialText, BusText, ChipsetText, ChipEvidence, ResultText,
            HwAdapterText, HwChipNameText, HwChipIdText, HwChipSizeText, HwChipVoltageText, HwResultText,
        })
            AttachValueBox(box);

        RefreshDrives();
    }

    private static void AttachCopyMenu(TextBox box)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = box });
        menu.Items.Add(new MenuItem { Header = "Select all", Command = ApplicationCommands.SelectAll, CommandTarget = box });
        menu.Items.Add(new Separator());
        var copyAll = new MenuItem { Header = "Copy all" };
        copyAll.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(box.Text)) return;
            try { Clipboard.SetText(box.Text); } catch { /* clipboard busy — ignore */ }
        };
        menu.Items.Add(copyAll);
        box.ContextMenu = menu;
    }

    // A selectable read-only value label: themed Copy menu, plus wheel-forwarding so the mouse wheel still
    // scrolls the surrounding ScrollViewer when the pointer is over the (non-scrolling) field.
    private void AttachValueBox(TextBox box)
    {
        AttachCopyMenu(box);
        box.PreviewMouseWheel += ForwardWheelToParent;
    }

    private static void ForwardWheelToParent(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        if (((FrameworkElement)sender).Parent is UIElement parent)
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender,
            });
    }

    // Build a selectable read-only label for the dynamically-generated method rows.
    private TextBox MethodLabel(string text)
    {
        var box = new TextBox { Text = text, Style = (Style)FindResource("ValueText") };
        AttachValueBox(box);
        return box;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Match the Windows title bar to the dark theme (Win11 / late Win10). Cosmetic — ignore failures.
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));        // older build fallback
        }
        catch { /* cosmetic only */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshDrives();

    private void RefreshDrives()
    {
        var drives = DriveEnumerator.Scan();
        DriveCombo.ItemsSource = drives;
        if (drives.Count > 0) DriveCombo.SelectedIndex = 0;
        AppendLog($"# {drives.Count} optical drive(s) found.");
    }

    private async void OnIdentify(object sender, RoutedEventArgs e)
    {
        if (DriveCombo.SelectedItem is not OpticalDrive drive) return;
        SetControlsBusy(true);
        StageText.Text = "Identifying…";
        try
        {
            var (id, chip) = await Task.Run(() =>
            {
                string bus = DriveEnumerator.QueryBusType(drive.Letter);
                using var dev = ScsiDevice.Open(drive.Letter, _logger);
                var identity = DriveIdentifier.Identify(dev, bus);
                var chipset = ChipsetDetector.Detect(dev, identity);
                return (identity, chipset);
            });
            _id = id;
            _chip = chip;
            ShowIdentity(id, chip);
            ShowMethods(id, chip);
            ExtractButton.IsEnabled = true;
            StageText.Text = "Identified.";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetControlsBusy(false);
        }
    }

    private void ShowIdentity(DriveIdentity id, ChipsetInfo chip)
    {
        VendorText.Text = Blank(id.Vendor);
        ModelText.Text = Blank(id.Model);
        FwText.Text = Blank(id.FirmwareRevision);
        SerialText.Text = id.Serial ?? "(not reported)";
        BusText.Text = id.BusType;
        ChipsetText.Text = $"{chip.Family} — {chip.Name}  ({chip.ConfidencePercent}%)";
        ChipEvidence.Text = string.Join("\n", chip.Evidence);
    }

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "(none)" : s;

    private void ShowMethods(DriveIdentity id, ChipsetInfo chip)
    {
        MethodsPanel.Children.Clear();
        foreach (var m in _orch.Methods)
        {
            var a = m.Evaluate(id, chip);
            var (label, fg, bg) = a.Level switch
            {
                Applicability.Applicable => ("APPLICABLE", Palette.GreenBrush, Palette.SuccessSoftBrush),
                Applicability.Maybe => ("MAYBE", Palette.PeachBrush, Palette.WarnSoftBrush),
                _ => ("N/A", Palette.Overlay1Brush, Palette.Surface1Brush),
            };

            // Row: a coloured status chip, then the method name (wraps in the remaining width).
            var header = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var chipBadge = new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 1, 7, 2),
                Margin = new Thickness(0, 1, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = label, Foreground = fg, FontWeight = FontWeights.SemiBold, FontSize = 10.5 },
            };
            Grid.SetColumn(chipBadge, 0);
            header.Children.Add(chipBadge);

            var name = MethodLabel(m.DisplayName);
            name.Foreground = Palette.TextBrush;
            name.FontWeight = FontWeights.SemiBold;
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1);
            header.Children.Add(name);

            MethodsPanel.Children.Add(header);
            var reason = MethodLabel(a.Reason);
            reason.Foreground = Palette.Overlay1Brush;
            reason.FontSize = 11.5;
            reason.Margin = new Thickness(0, 0, 0, 12);
            MethodsPanel.Children.Add(reason);
        }
    }

    private IFirmwareExtractionMethod ResolveMethod()
    {
        var choice = MethodCombo.SelectedItem as MethodChoice;
        if (choice?.Id is null) return _orch.PickAuto(_id!, _chip!);
        return _orch.ById(choice.Id) ?? _orch.PickAuto(_id!, _chip!);
    }

    private async void OnExtract(object sender, RoutedEventArgs e)
    {
        if (_id is null || _chip is null || DriveCombo.SelectedItem is not OpticalDrive drive) return;

        var method = ResolveMethod();
        _cts = new CancellationTokenSource();
        SetControlsBusy(true);
        CancelButton.IsEnabled = true;
        SaveButton.IsEnabled = false;
        Progress.Value = 0;
        ResultText.Text = "";
        StageText.Text = $"Extracting via {method.DisplayName}…";
        AppendLog($"# Extract: {method.DisplayName}");

        var progress = new Progress<ExtractionProgress>(p =>
        {
            if (p.Percent > 0) Progress.Value = p.Percent;
            if (p.Stage is not null) StageText.Text = p.Stage;
            if (p.LogLine is not null) AppendLog("… " + p.LogLine);
        });

        ScsiDevice? dev = null;
        try
        {
            dev = await Task.Run(() => ScsiDevice.Open(drive.Letter, _logger));
            var result = await _orch.RunAsync(dev, _id, _chip, method, progress, _cts.Token);
            _lastResult = result;
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            StageText.Text = "Cancelled.";
            AppendLog("# Cancelled.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            dev?.Dispose();
            _cts?.Dispose();
            _cts = null;
            SetControlsBusy(false);
            CancelButton.IsEnabled = false;
        }
    }

    private void ShowResult(ExtractionResult result)
    {
        if (result.Success && result.Firmware is not null)
        {
            Progress.Value = 100;
            StageText.Text = "Done.";
            ResultText.Foreground = Palette.TextBrush;
            ResultText.Text = $"{result.Summary}\nData: {result.DataLabel}.";
            PreviewHeader.Text = $"Dump preview — {result.ByteCount:N0} bytes ({result.DataLabel})";
            HexBox.Text = HexDump.Format(result.Firmware);
            SaveButton.IsEnabled = result.ByteCount > 0;
        }
        else
        {
            StageText.Text = "No dump produced.";
            ResultText.Foreground = Palette.PeachBrush;
            ResultText.Text = result.Summary;
            HexBox.Text = "";
            PreviewHeader.Text = "Dump preview";
            SaveButton.IsEnabled = false;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_lastResult?.Firmware is null || _id is null || _chip is null) return;
        var now = DateTime.UtcNow;
        var dlg = new SaveFileDialog
        {
            FileName = DumpWriter.BuildStem(_id, now) + ".bin",
            Filter = "Firmware dump (*.bin)|*.bin|All files (*.*)|*.*",
            Title = "Save firmware dump",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var files = DumpWriter.Write(dlg.FileName, _lastResult, _id, _chip, _logger.Entries, now);
            string logPath = Path.ChangeExtension(dlg.FileName, ".log");
            _logger.WriteAllTo(logPath);
            AppendLog($"# Saved: {files.BinPath}");
            AppendLog($"# Saved: {files.SidecarPath}");
            AppendLog($"# Saved: {logPath}");
            StageText.Text = "Saved.";
        }
        catch (Exception ex)
        {
            ShowError($"Save failed: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        StageText.Text = "Error.";
        ResultText.Foreground = Palette.RedBrush;
        ResultText.Text = message;
        AppendLog("# ERROR: " + message);
    }

    private void SetControlsBusy(bool busy)
    {
        IdentifyButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        DriveCombo.IsEnabled = !busy;
        MethodCombo.IsEnabled = !busy;
        ExtractButton.IsEnabled = !busy && _id is not null;
    }

    // Serialize all hardware-tab actions: only one may touch the single CH341 adapter at a time.
    private void SetHwBusy(bool busy)
    {
        HwDetectButton.IsEnabled = !busy;
        HwIdentifyButton.IsEnabled = !busy;
        HwReadButton.IsEnabled = !busy && _hwChip?.SizeKnown == true;
    }

    private void AppendLog(string line) => AppendConsole(LogBox, line);

    // Append a line, following the tail only if the view is already at the bottom — so scrolling up to read
    // or select text isn't yanked away by incoming log lines.
    private static void AppendConsole(TextBox box, string line)
    {
        bool atBottom = box.ExtentHeight <= box.ViewportHeight
                        || box.VerticalOffset >= box.ExtentHeight - box.ViewportHeight - 4;
        box.AppendText(line + "\n");
        if (atBottom) box.ScrollToEnd();
    }

    // Clear buttons wipe only the on-screen panes; the saved .log sidecar (from the logger's own history)
    // and the last dump held for Save are left intact.
    private void OnClearLog(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void OnClearPreview(object sender, RoutedEventArgs e)
    {
        HexBox.Clear();
        PreviewHeader.Text = "Dump preview";
    }

    // ───────────────────────────── Hardware (SPI flash) tab ─────────────────────────────

    private async void OnHwDetect(object sender, RoutedEventArgs e)
    {
        SetHwBusy(true);
        try
        {
            var (ok, message) = await Task.Run(() =>
            {
                try
                {
                    using var d = Ch341Device.Open();
                    return (true, $"CH341 adapter connected (DLL v{d.DllVersion}). Ready for SPI.");
                }
                catch (Ch341Exception ex) { return (false, ex.Message); }
            });
            HwAdapterText.Text = message;
            HwAdapterText.Foreground = ok ? Palette.GreenBrush : Palette.PeachBrush;
            HwLog("# " + message);
        }
        finally
        {
            SetHwBusy(false);
        }
    }

    private async void OnHwIdentify(object sender, RoutedEventArgs e)
    {
        SetHwBusy(true);
        HwStageText.Text = "Identifying chip…";
        try
        {
            var chip = await Task.Run(() =>
            {
                using var d = Ch341Device.Open();
                return SpiNorFlash.ReadId(d, HwLogBg);
            });
            _hwChip = chip;
            HwChipNameText.Text = chip.Name;
            HwChipIdText.Text = chip.IdHex;
            HwChipSizeText.Text = chip.SizeText;
            HwChipVoltageText.Text = chip.VoltageText;
            HwChipVoltageText.Foreground = chip.Voltage switch
            {
                FlashVoltage.OneEightVolt => Palette.PeachBrush,
                FlashVoltage.ThreeVolt or FlashVoltage.Wide => Palette.GreenBrush,
                _ => Palette.Overlay1Brush,
            };

            if (chip.LooksEmpty)
            {
                HwStageText.Text = "No chip detected.";
                HwResultText.Foreground = Palette.PeachBrush;
                HwResultText.Text = "JEDEC ID came back all 0x00/0xFF — check the clip contact, chip orientation, and that the flash is powered.";
            }
            else if (!chip.SizeKnown)
            {
                HwStageText.Text = "Chip found; size unknown.";
                HwResultText.Foreground = Palette.PeachBrush;
                HwResultText.Text = "The chip responded but its capacity code is unrecognized, so automatic read is disabled.";
            }
            else
            {
                HwStageText.Text = "Chip identified.";
                HwReadButton.IsEnabled = true;
                if (chip.IsLikely1V8)
                {
                    HwResultText.Foreground = Palette.PeachBrush;
                    HwResultText.Text = "This looks like a 1.8 V part. A 3.3 V or 5 V programmer will damage it — put a 1.8 V adapter board " +
                                        "between the CH341A and the clip. Confirm the voltage from the chip marking before reading.";
                }
                else
                {
                    HwResultText.Foreground = Palette.TextBrush;
                    HwResultText.Text = "";
                }
            }
        }
        catch (Exception ex)
        {
            HwShowError(ex.Message);
        }
        finally
        {
            SetHwBusy(false);
        }
    }

    private async void OnHwRead(object sender, RoutedEventArgs e)
    {
        if (_hwChip is null || !_hwChip.SizeKnown) return;

        var chip = _hwChip;
        _hwCts = new CancellationTokenSource();
        SetHwBusy(true);
        HwCancelButton.IsEnabled = true;
        HwSaveButton.IsEnabled = false;
        HwProgress.Value = 0;
        HwResultText.Text = "";
        HwStageText.Text = $"Reading {chip.SizeText}…";

        var progress = new Progress<int>(p => HwProgress.Value = p);
        var ct = _hwCts.Token;
        try
        {
            var data = await Task.Run(() =>
            {
                using var d = Ch341Device.Open();
                return SpiNorFlash.ReadAll(d, chip, progress, HwLogBg, ct);
            });
            _hwDump = data;
            HwProgress.Value = 100;
            HwStageText.Text = "Done.";
            HwResultText.Foreground = Palette.TextBrush;
            HwResultText.Text = $"Read {data.Length:N0} bytes from {chip.Name}.";
            HwPreviewHeader.Text = $"Dump preview — {data.Length:N0} bytes";
            HwHexBox.Text = HexDump.Format(data);
            HwSaveButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            HwStageText.Text = "Cancelled.";
            HwLog("# Cancelled.");
        }
        catch (Exception ex)
        {
            HwShowError(ex.Message);
        }
        finally
        {
            _hwCts?.Dispose();
            _hwCts = null;
            HwCancelButton.IsEnabled = false;
            SetHwBusy(false);
        }
    }

    private void OnHwCancel(object sender, RoutedEventArgs e) => _hwCts?.Cancel();

    private void OnHwSave(object sender, RoutedEventArgs e)
    {
        if (_hwDump is null || _hwChip is null) return;
        var now = DateTime.UtcNow;
        var dlg = new SaveFileDialog
        {
            FileName = DumpWriter.BuildHardwareStem(_hwChip, now) + ".bin",
            Filter = "Firmware dump (*.bin)|*.bin|All files (*.*)|*.*",
            Title = "Save SPI flash dump",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var files = DumpWriter.WriteHardware(dlg.FileName, _hwDump, _hwChip, _hwLog.ToArray(), now);
            HwLog($"# Saved: {files.BinPath}");
            HwLog($"# Saved: {files.SidecarPath}");
            HwStageText.Text = "Saved.";
        }
        catch (Exception ex)
        {
            HwShowError($"Save failed: {ex.Message}");
        }
    }

    private void HwShowError(string message)
    {
        HwStageText.Text = "Error.";
        HwResultText.Foreground = Palette.RedBrush;
        HwResultText.Text = message;
        HwLog("# ERROR: " + message);
    }

    private void HwLog(string line)
    {
        _hwLog.Add(line);
        AppendConsole(HwLogBox, line);
    }

    private void OnClearHwLog(object sender, RoutedEventArgs e) => HwLogBox.Clear();

    private void OnClearHwPreview(object sender, RoutedEventArgs e)
    {
        HwHexBox.Clear();
        HwPreviewHeader.Text = "Dump preview";
    }

    // Log callback for background SPI work — marshals onto the UI thread.
    private void HwLogBg(string line) => Dispatcher.BeginInvoke(() => HwLog(line));
}

# third_party — CH341 SPI DLL

Drop **`CH341DLLA64.dll`** (the 64-bit WCH CH341 library) into this folder to have it bundled with
FirmwareStudio. When the file is present here, the build copies it next to `FirmwareStudio.exe`, `dotnet
publish` includes it in the publish output, and the Inno installer therefore ships it — so the Hardware
(SPI flash) tab works with no separate driver-DLL step for the user. When the file is absent, the build
and installer still work; the app just shows its "install the CH341 driver / place the DLL" message at
runtime.

## Where to get it

It ships in WCH's **CH341PAR** driver package (the parallel/I²C/SPI driver — *not* CH341SER, the serial
one). Download CH341PAR from WCH (wch-ic.com → Downloads → Driver), run its `SETUP.EXE`, and copy the
64-bit `CH341DLLA64.dll` it installs (typically under `C:\Windows\System32\` or the driver's install
folder) into this directory. It also ships bundled with many CH341A flashing tools.

## Licensing note

`CH341DLLA64.dll` is WCH's proprietary library. Redistributing it alongside an app is common practice
among CH341-based tools, but it is WCH's binary — bundling it here is a deliberate choice. That's why it
is **not** committed to the repo by default; you add it yourself. The end user still needs the CH341**PAR**
*kernel driver* installed (the .sys/.inf) for Windows to see the adapter — bundling the DLL only removes
the separate DLL-placement step, not the driver install.

## After adding the file

```
dotnet publish src/FirmwareStudio.Wpf -c Release -r win-x64 --self-contained true
"%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\FirmwareStudio.iss
```

The resulting `installer/Output/FirmwareStudio-1.0.0-Setup.exe` will contain `CH341DLLA64.dll` next to the
executable.

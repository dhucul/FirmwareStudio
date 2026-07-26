# third_party — CH341 SPI DLL

`CH341DLLA64.dll` (the 64-bit WCH CH341 library) is bundled from this folder. The build copies it next
to `FirmwareStudio.exe`, `dotnet publish` includes it in the publish output, and the installer ships it,
so the Hardware (SPI flash) tab requires no separate driver-DLL placement step. If the file is removed,
the build and installer still work; the app instead shows its install/place-the-DLL message at runtime.

## Source

The DLL ships in WCH's **CH341PAR** driver package (the parallel/I²C/SPI driver, not CH341SER). CH341PAR
is available from WCH's driver downloads and installs the 64-bit `CH341DLLA64.dll`; many CH341A flashing
tools also include it.

## Licensing note

`CH341DLLA64.dll` is WCH's proprietary library. This repository currently commits and bundles that
binary as a deliberate redistribution choice. The end user still needs the CH341**PAR** kernel driver
installed (the `.sys`/`.inf`) for Windows to see the adapter. Bundling the DLL removes only the separate
DLL-placement step, not the kernel-driver installation.

## Build

```powershell
dotnet publish src/FirmwareStudio.Wpf -c Release -r win-x64 --self-contained true
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\FirmwareStudio.iss
```

The resulting `installer/Output/FirmwareStudio-1.10.1-Setup.exe` contains `CH341DLLA64.dll` next to the
executable.

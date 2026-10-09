# Third-party notices

Toasty includes or redistributes the components below, all unmodified. Each is the property of its respective authors and is used under its own licence. Full licence texts are in the [`licenses`](licenses) folder (installed to `C:\Program Files\Toasty\licenses`).

Toasty's own code is licensed separately (see [`LICENSE`](LICENSE)); none of the licences below extend to it.

## Bundled in the app

### LibreHardwareMonitorLib 0.9.6
- Copyright: LibreHardwareMonitor contributors
- Licence: Mozilla Public License 2.0 ([`licenses/MPL-2.0.txt`](licenses/MPL-2.0.txt))
- Source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- Includes PawnIO modules from https://github.com/namazso/PawnIO.Modules (LGPL-2.1, [`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt))

### DiskInfoToolkit 1.1.2, RAMSPDToolkit-NDD 1.4.2, BlackSharp.Core 1.0.7
- Copyright: Blacktempel and contributors
- Licence: Mozilla Public License 2.0 ([`licenses/MPL-2.0.txt`](licenses/MPL-2.0.txt))
- Source: https://github.com/Blacktempel/DiskInfoToolkit, https://github.com/Blacktempel/RAMSPDToolkit, https://github.com/Blacktempel/BlackSharp

### HidSharp 2.6.4
- Copyright 2010-2025 James F. Bellinger
- Licence: Apache License 2.0 ([`licenses/Apache-2.0.txt`](licenses/Apache-2.0.txt), notice in [`licenses/HidSharp-NOTICE.txt`](licenses/HidSharp-NOTICE.txt))
- Source: https://software.seekye.com/hidsharp

### .NET 10 runtime, Windows Forms, System.Management, Mono.Posix.NETStandard
- Copyright (c) .NET Foundation and Contributors / Microsoft Corporation
- Licence: MIT ([`licenses/dotnet-MIT.txt`](licenses/dotnet-MIT.txt))
- Source: https://github.com/dotnet/runtime, https://github.com/dotnet/winforms
- The .NET runtime's own third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

## Redistributed alongside the app

### PresentMon 2.6.0 (`PresentMon.exe`)
- Copyright (C) 2017-2024 Intel Corporation
- Licence: MIT ([`licenses/PresentMon-MIT.txt`](licenses/PresentMon-MIT.txt))
- Source: https://github.com/GameTechDev/PresentMon (tag `v2.6.0`)
- Used to read frame timing for game sessions. It's run as a separate program; Toasty doesn't link to it.

### PawnIO 2.2.0 (`PawnIO_setup.exe`, run during setup)
- Copyright namazso, https://pawnio.eu
- **Driver:** GNU General Public License v2 ([`licenses/GPL-2.0.txt`](licenses/GPL-2.0.txt)); its modules are LGPL-2.1 ([`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt)).
  Source for the exact version shipped: https://github.com/namazso/PawnIO (tag `2.2.0`), also attached to each Toasty release as `PawnIO-2.2.0-source.zip`.
- **Setup program:** freeware by namazso, distributed unmodified from https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0 (listed as supporting offline distribution).
- PawnIO is installed as a separate system driver. Toasty communicates with it at run time and is not a derivative work of it, so the GPL does not apply to Toasty's code.

## Used to build, not redistributed

- **Inno Setup 6.7.3** (installer builder), Jordan Russell and Martijn Laan, https://jrsoftware.org/isinfo.php. Free for any use including commercial; installers it produces carry no licence obligations.

## Not bundled

Toasty draws text with the Segoe UI, Bahnschrift and Segoe Fluent Icons / Segoe MDL2 Assets fonts that ship with Windows. They're used at run time from the system and are not included.

# PsychoFlash

**PsychoFlash** is an open, from-scratch replacement for the "Spanish Oak Flash Tool" workflow:
it reads, erases and reprograms the flash memory of a Ford EEC-VI "Spanish Oak" 1024k PCM - the
computer in a **BF, FG, FG II or FG-X Falcon** (and the equivalent Ford/Aston applications) - over
any SAE J2534 interface.

It was written after reverse engineering the original tool; the full write-up is in
[docs/REVERSE_ENGINEERING.md](docs/REVERSE_ENGINEERING.md) and the wire protocol it speaks is in
[docs/PROTOCOL.md](docs/PROTOCOL.md).

```
   PsychoFlash.exe            WinForms UI (net48 / net8.0-windows)
   psychoflash.exe            console / scriptable front end (net8.0)
   PsychoFlash.Core.dll       J2534 interop, ISO-TP transport, Ford EEC-VI protocol, flashing engine
```

## Features

* **Read** the complete 1 MB flash image to a `.bin` file (with progress and SHA-256).
* **Write** an image: erase (`B2 AA`), request download (`34`), 0x400 byte blocks (`36`),
  transfer exit (`37`), optional read-back verification.
* **Erase** only, for recovery after an interrupted flash.
* **Verify** the PCM contents against a file.
* **Battery voltage** displayed and checked before every erase/write.
* Ford **FEPS 18 V** programming voltage with an operator prompt for the ignition cycle.
* Pluggable **seed/key** algorithms (`ISeedKeyAlgorithm`); the Falcon Spanish Oak algorithm
  ships built in and is covered by test vectors.
* Any J2534 v04.04 interface: OBDX Pro FT, Tactrix Openport, Ford VCM II, ... The DLL is
  discovered from `HKLM\SOFTWARE\PassThruSupport.04.04` or passed explicitly.
* No licence server, no dongle, no telemetry.

## Requirements

* Windows (J2534 drivers are Windows only).
* .NET Framework 4.8 **or** .NET 8 desktop runtime for the UI; .NET 8 for the CLI.
* A J2534 interface and its vendor driver.
* A car battery charger. Flashing is a multi-minute erase/write cycle.

## Build

```console
git clone <this repo>
cd Spanish-Oak
dotnet build PsychoFlash.sln -c Release
dotnet run --project tests/PsychoFlash.SelfTest      # 1000+ checks, no hardware needed
```

Outputs:

```
src/PsychoFlash.App/bin/Release/net8.0-windows/PsychoFlash.exe
src/PsychoFlash.App/bin/Release/net48/PsychoFlash.exe
src/PsychoFlash.Cli/bin/Release/net8.0/psychoflash.exe
```

Nothing outside the .NET SDK is required - there are no NuGet dependencies.

## Using the GUI

1. Plug the interface into the OBD-II port and into USB.
2. Start `PsychoFlash.exe`, pick your interface, press **Connect**.
3. Check the battery voltage. Below 11.8 V the tool warns before letting you flash.
4. Choose the PCM profile (Falcon Spanish Oak 1024k is the default).
5. **Read flash to file** first - keep a known-good stock image for every car you touch.
6. **Write flash from file** to program: confirm the image size and hash, then follow the
   ignition-cycle prompt when the FEPS voltage is applied.

The log pane records every frame that goes over the bus; **Save log** writes it to a text file
for support.

## Using the console

```console
psychoflash interfaces                     # list installed J2534 drivers
psychoflash info                           # firmware, battery voltage, VIN
psychoflash read  --out BF_stock.bin
psychoflash write --in  BF_tuned.bin --verify
psychoflash verify --in BF_tuned.bin
psychoflash erase                          # recovery only
psychoflash seed --seed 6A3F12             # offline key calculation (no hardware)
```

Options: `--dll <path>` (explicit J2534 DLL), `--device <name>`, `--profile falcon|blackoak`,
`--verify`, `--yes`.

## Safety and legal

* **Flashing can brick a PCM.** Keep a charger on the battery, do not interrupt a write, and
  never flash a car you cannot afford to recover.
* The PCM's first 64 kB (boot block) is never written by the Falcon profile, which is what makes
  recovery possible - but only if you have a valid image to write.
* 18 V on the FEPS pin is applied by the J2534 interface, not by PsychoFlash.
* PsychoFlash is an independent clean-room implementation. No code from the original tool is
  used; the protocol facts it relies on are documented in [docs/PROTOCOL.md](docs/PROTOCOL.md).
  It is not affiliated with Tester Present, Ford or Aston Martin.
* Provided under the MIT licence, without any warranty. You are responsible for what you flash.

## Repository layout

```
Spanish Oak Flash Tool.exe        the analysed artefact (unchanged)
powertrainControlModule.cs        decompiled partial class shipped with the repo (reference)
0x10 diagnosticSessionControl.cs  decompiled partial class shipped with the repo (reference)

src/PsychoFlash.Core/             J2534, transport, protocol, flashing engine
  J2534/                          V04.04 bindings, device wrapper, registry discovery
  Protocol/                       PcmProfile, FordPcmClient (services), SeedKey
  Flashing/                       FlashService (read/write/erase/verify), FlashSession
src/PsychoFlash.App/              WinForms UI
src/PsychoFlash.Cli/              console front end
tests/PsychoFlash.SelfTest/       dependency-free test runner (seed/key vectors etc.)
tools/                            reverse engineering helpers (PyInstaller unpacker, IL recovery,
                                  reference seed/key implementation)
docs/                             protocol and reverse engineering write-ups
```

## Adding a new PCM variant

1. Add a `PcmProfile` (CAN ids, memory map, erase payload, 0x34 parameter block).
2. If the unlock differs, implement `ISeedKeyAlgorithm` and drop the assembly into a `plugins`
   folder next to the executable - `SeedKeyAlgorithms.LoadPlugins` picks it up.
3. Add the seed/key vectors to `tests/PsychoFlash.SelfTest`.

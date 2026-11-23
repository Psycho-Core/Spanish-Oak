# PsychoFlash

**PsychoFlash** is an open, from-scratch replacement for the "Spanish Oak Flash Tool" workflow:
it reads, erases and reprograms the flash memory of a Ford EEC-VI "Spanish Oak" 1024k PCM - the
computer in a **BF, FG, FG II or FG-X Falcon** (and the equivalent Ford/Aston applications) - over
any SAE J2534 interface.

It was written after reverse engineering the original tool; the full write-up is in
[docs/REVERSE_ENGINEERING.md](docs/REVERSE_ENGINEERING.md) and the wire protocol it speaks is in
[docs/PROTOCOL.md](docs/PROTOCOL.md). If you are putting a Barra into another car, read
[docs/SWAP.md](docs/SWAP.md) - it lists what the flasher does and what the tune and the wiring
still have to provide.

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
* **`unlock --seed-only`** - reads the seed without sending a key, so you can check the key
  offline (`psychoflash seed`) before spending any of the PCM's security attempts.
* **Read and clear stored fault codes** (`0x19` / `0x14`) - no FEPS needed, so it works on a car
  that will not start. See [docs/SWAP.md](docs/SWAP.md) for what each code means in a conversion.
* **VIN** readout (`0x22 F190`).
* **Battery voltage** displayed and checked before every erase/write.
* Ford **FEPS 18 V** programming voltage with an operator prompt for the ignition cycle.
* Pluggable **seed/key** algorithms (`ISeedKeyAlgorithm`); the Falcon Spanish Oak algorithm
  ships built in and is covered by test vectors. When a PCM refuses it, a **keybag** file of
  candidate constants can be tried in order - see [docs/UNLOCK.md](docs/UNLOCK.md).
* **`--feps-pin` / `--feps-voltage`** overrides, for interfaces that want the real OBD pin 13
  instead of the reference tool's `0x80`.
* Any J2534 v04.04 interface: OBDX Pro FT, Tactrix Openport, Ford VCM II, ... The DLL is
  discovered from `HKLM\SOFTWARE\PassThruSupport.04.04` or passed explicitly.
* No licence server, no dongle, no telemetry.

## Download a build

Releases are built by GitHub Actions on `windows-latest` (net48, net8.0-windows and the CLI) and
verified by the self test on both Windows and Linux:

<https://github.com/Psycho-Core/Spanish-Oak/releases>

* `PsychoFlash-standalone.exe` - single self-contained file, no .NET install needed (easiest).
* `PsychoFlash-net48.zip` - ~300 KB, needs .NET Framework 4.8 (already on Windows 10/11).
* `psychoflash-cli-net8.0.zip` - the console front end.

Build status: `.github/workflows/build.yml` runs on every push; a failing self-test line shows up
as a check annotation on the run.

## Which PCMs this supports

| PCM | Vehicles | Part number family | Profile |
| --- | --- | --- | --- |
| Spanish Oak 1024k (EEC-VI) | BF Falcon 2005-2008 (XT, XR6, XR6 Turbo, Fairmont, Fairlane, Territory SY/SZ 4.0) | `5R29-12A650-xx`, `6R29-12A650-xx` (e.g. **5R29-12A650-CC** = BF / SY Territory 4.0 N/A, 4-speed auto) | `falcon` |
| Spanish Oak 1024k | FG / FG II / FG-X Falcon | `8R29-12A650-xx` | `falcon` |
| Black Oak dual bank | Aston Martin V8/V12 era | - | `blackoak` |

The part number tells you the *family*, not the calibration: turbo, N/A, manual and automatic
applications all share the protocol and the 1 MB layout, but the tune inside is different. Confirm
what you actually have before writing anything:

```console
psychoflash identify                    # 0x22 identifiers: part number, hardware, software
psychoflash read --out stock.bin
psychoflash info --in stock.bin         # size, SHA-256, blank check, strategy strings
```

`identify` prints the PCM's own part number - check it against the label on the box. `info --in`
refuses an image that is the wrong size, blank, or has no plausible calibration text.

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
psychoflash dtcs                           # stored fault codes (0x19), works on a no-start
psychoflash dtcs --clear                   # wipe them (0x14)
psychoflash vin                            # VIN from the PCM (0x22 F190)
psychoflash seed --seed 6A3F12             # offline key calculation (no hardware)
psychoflash diagnose                       # read-only health check: interface, battery, seed, VIN, codes
psychoflash unlock --seed-only             # read the seed without spending an unlock attempt
psychoflash keybag --file bag.txt          # parse/list seed-key constant sets
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
  used; the protocol facts it relies on are documented in [docs/PROTOCOL.md](docs/PROTOCOL.md). If you are putting a Barra into another car, read
[docs/SWAP.md](docs/SWAP.md) - it lists what the flasher does and what the tune and the wiring
still have to provide.
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
docs/                             protocol, reverse engineering, audit and swap write-ups
```

## Adding a new PCM variant

1. Add a `PcmProfile` (CAN ids, memory map, erase payload, 0x34 parameter block).
2. If the unlock differs, implement `ISeedKeyAlgorithm` and drop the assembly into a `plugins`
   folder next to the executable - `SeedKeyAlgorithms.LoadPlugins` picks it up.
3. Add the seed/key vectors to `tests/PsychoFlash.SelfTest`.

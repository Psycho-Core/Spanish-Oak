# PsychoFlash

A ground-up reimplementation of the *Spanish Oak Flash Tool* for Ford EEC-VI
**Spanish Oak 1024k** PCMs: BF, FG, FGII and FGX Falcon (Barra I6), plus the Black Oak dual-bank
layout. Talks to the PCM over any SAE J2534 pass-through interface (VCM II, OBDX Pro, Mongoose,
Tactrix Openport, etc.).

## What's in this release

| File | Use it when |
| --- | --- |
| `PsychoFlash-standalone.exe` | Easiest. Single file, self contained - no .NET install required. |
| `PsychoFlash-net48.zip` | Small (~300 KB). Needs .NET Framework 4.8, which ships with Windows 10/11. |
| `PsychoFlash-net8.0-windows.zip` | Framework dependent build for .NET 8 desktop. |
| `psychoflash-cli-net8.0.zip` | Command line version for scripting and batch work. |

## New in this release

* **`psychoflash live`** - stream standard OBD-II data (mode 01): rpm, coolant, throttle, MAF,
  speed, battery. `--pid 0C,05,11 --interval 250 --count 20 --csv`. Works while cranking, so
  "0 rpm" vs "180 rpm" answers the crank-sensor question immediately.
* **`psychoflash diff`** - byte-level comparison of two images with the changed regions marked as
  inside or outside the flash window.
* **`psychoflash checksums`** - candidate sums (sum32/sum16/crc32) per region, for deriving the
  Ford scheme from a stock/known-good pair.
* **`docs/CHECKSUMS.md`** and **`docs/PATS.md`** - what the tool does about checksums and the
  immobiliser, and what it deliberately refuses to do.

* **`psychoflash identify`** - reads the PCM's own identifiers (`0x22`: spare part number, ECU
  serial, hardware and software versions) so the box can be checked against the label.
* **`psychoflash info --in file.bin`** - offline image check: size against the profile, SHA-256,
  blank test and the calibration strings. Refuses an image that does not look real.
* README now lists the supported PCM part-number families (BF `5R29-12A650-xx`, FG `8R29-...`).

* **`psychoflash diagnose`** - read-only health check: interface versions, battery voltage, seed,
  VIN and stored codes. Nothing is written, nothing is erased.
* **`psychoflash unlock [--seed-only]`** - tests `0x27` without flashing anything. `--seed-only`
  reads the seed without sending a key, so it spends none of the PCM's security attempts.

* **Keybag support** - `--keybag bag.txt` tries candidate seed/key constant sets in order when a
  PCM refuses the built-in algorithm; `psychoflash keybag --file bag.txt` parses and lists one,
  `psychoflash seed --seed 123456 --keybag bag.txt` checks candidates offline. See
  [docs/UNLOCK.md](docs/UNLOCK.md).
* **`--feps-pin` / `--feps-voltage`** - choose the programming-voltage pin (default `0x80`, the
  reference tool's value; `13` for interfaces that want the real OBD pin).

* **Read stored trouble codes** - `psychoflash dtcs`, or the *Read codes* button in the GUI.
  No FEPS and no unlock needed, so it works on a car that cranks but will not run.
* **Clear trouble codes** - `psychoflash dtcs --clear`.
* **VIN readout** - `psychoflash vin`.
* `docs/SWAP.md`: what a Barra-into-something conversion still needs (PATS, checksums, FPDM,
  drive-by-wire) and what each no-start code means.

## Requirements

* Windows 10/11 (x64).
* A J2534 device with the vendor driver installed (`InstalledJ2534DLLs` in the registry).
* A **stable power supply** on the car battery, and FEPS (18 V programming voltage) support on
  the interface. The PCM is bricked if power drops mid-erase.

## Using the GUI (PsychoFlash.exe)

1. Pick the J2534 interface from the drop-down (the tool lists what the vendor driver registered).
2. Pick the profile - **Falcon Spanish Oak 1024k (BF/FG/FGII/FGX)** for a Barra.
3. `Read PCM` writes a 1 MB image you can back up.
4. `Write PCM` erases `0x010000-0x0FFFFF` and flashes the calibration area; the boot block is
   never touched.
5. `Verify PCM` reads the PCM back and compares it with the open file.

## Using the CLI

```
psychoflash interfaces
psychoflash info
psychoflash vin
psychoflash dtcs
psychoflash info --in falcon-stock.bin
psychoflash read --out backup.bin --yes
psychoflash write --in tuned.bin --verify --yes
psychoflash erase --yes
psychoflash seed --seed AABBCC
```

## Read this before the first flash

* This code has **never been run against a real PCM**. It was written from a protocol
  reconstruction of the original tool and validated with contract tests, but the first session on
  a car should be a **read only**. See `docs/AUDIT.md` for the full audit, the five known gaps
  and a first-session checklist.
* Flashing **does not fix Ford checksums**. If the calibration changed, apply the checksum in your
  tuning software first.
* Keep the original 1 MB read. That file is your recovery path.

## Verify what you downloaded

Every release is built by GitHub Actions from the tagged commit; the workflow is in
`.github/workflows/build.yml`. Build and test locally with `build.cmd` or:

```
dotnet build PsychoFlash.sln -c Release
dotnet run --project tests/PsychoFlash.SelfTest -c Release
```

## License

The PsychoFlash sources are MIT (`LICENSE-PsychoFlash`). The original
`Spanish Oak Flash Tool.exe` is **not** included and remains the property of its author; see
`README.md`.

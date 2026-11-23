# Audit: PsychoFlash vs a BF Falcon Barra Spanish Oak 1024k PCM

Date: 2026-11-23
Target: BF Falcon (2005-2008), Barra I6, Ford EEC-VI "Spanish Oak" 1024k PCM,
PTE/PTF/PTG style operating systems, 1 MB flash.

Method: line-by-line review of `PsychoFlash.Core` against the recovered reference protocol
(`docs/PROTOCOL.md`), plus contract tests that now pin every BF-specific constant.

## Verdict

**Protocol coverage: complete for read / erase / write / verify on a BF Barra PCM.**
Five defects were found and fixed - one of them would have made a write take about
30 minutes instead of about 35 seconds, and the fifth was caught by the first CI run on a real
.NET toolchain. Five capability gaps remain and are listed at the end; none of them blocks a
stock read or a tuned-image flash.

Continuous integration: `.github/workflows/build.yml` compiles the solution on
`windows-latest` (net48 + net8.0-windows + net8.0 CLI) and builds `PsychoFlash.Core` on
`ubuntu-latest`, then runs this self test on both. First run: both compilations clean, self test
failed on defect 5 below.

| Area | Reference tool | PsychoFlash | Status |
| --- | --- | --- | --- |
| CAN layer | ISO15765, 0x7E0/0x7E8, 500 kbit/s | identical | OK |
| Programming voltage | FEPS 18 V, pin 0x80 | identical | OK |
| Programming session | 0x10 0x85, 0x85 DTC off | 0x10 0x85, 0x85 DTC off | OK, now tolerant |
| Unlock | 0x27 01/02, MkI keygen | identical, verified on 29 seeds | OK |
| Read | 0x23, 0x400 blocks, 0x000000-0x0FFFFF | identical | OK |
| Erase | 0xB2 0xAA | identical | OK |
| Download | 0x34 with `00 01 00 00 00 0F 00 00` | identical (verbatim) | OK |
| Transfer | 0x36, 0x400 blocks, 0x010000-0x0FFFFF | identical | OK |
| Exit / cleanup | 0x37, 0x14 FF 00 | identical | OK |
| Verify | none | read-back compare | improved |
| Image handling | saves 1 MB, strips one byte post-hoc | strips the 0x63 byte in memory | improved |

## Defects found and fixed

### 1. Every 0x36 block waited ~2 s for a response that had already arrived (major)

`WaitForResponse` treated the second argument as "bytes expected" and kept reading until that
many bytes had accumulated. The positive answer to `0x36` is a single byte (`0x76`), so the
remaining reads timed out at 2 s each. A BF write is 960 blocks, which would have added roughly
half an hour to every flash and, worse, would have blown the PCM's session timer.

Fixed: the parameter is now a *minimum* response length (`1` for single byte answers, `0x401`
for a 0x23 read), and the loop returns the first complete message.

### 2. Programming-mode entry aborted on any rejection (major)

`EnterProgrammingMode` called `0x10 0x85`, `0x85 02 FF` and `0x3E` unguarded. On an EEC-VI the
FEPS edge is what opens the PCM; if the PCM rejects a session request (NRC 0x12 / 0x7E, which
happens depending on the operating system revision) the exception escaped and the operation
died before the unlock.

Fixed: each optional service is wrapped in `TryService`, logs the NRC and continues.

### 3. The GUI "Verify PCM" button never reached a PCM (major)

It called `ControlDtcSetting` and `Unlock` without applying FEPS, so security access was always
refused. Fixed: it now enters programming mode and always leaves it in a `finally`.

### 4. The "Measure" button stayed disabled after an operation (minor)

`SetBusy(false, ...)` did not re-enable the battery button. Fixed.

### 5. `ImageTools.FindPrintableStrings` dropped the last string in an image (minor)

A printable run is only emitted when a non-printable byte closes it, so a run that reaches the
end of the buffer was never added - the strategy/calibration identifier printed by `psychoflash
info` was missing whenever it sat at the end of a block. Caught by the first CI run, because the
self test asserts a run that ends at the end of the image.

Fixed: the trailing run is flushed after the loop (respecting the string limit). The test now
covers a terminated run, a trailing run and the limit.

## Contract tests added

`tests/PsychoFlash.SelfTest` now fails the build if any BF Barra constant drifts:

```
BF Barra / Spanish Oak 1024k profile contract
  CAN 0x7E0 / 0x7E8, 500 kbit/s, session 0x85, FEPS 18 V on pin 0x80
  read 0x000000 + 0x100000 in 0x400 blocks
  write 0x010000 + 0x0F0000 in 0x400 blocks
  erase B2 AA, download parameters 00 01 00 00 00 0F 00 00
J2534 message framing
  00 00 07 E0 | 27 01        (request)
  00 00 07 E8 | 67 01 12 34 56 (response parsing)
  multi-frame 0x36 frame length 0x405
```

## Remaining gaps (documented, not blocking)

1. **No checksum correction.** The 1 MB image is written verbatim. If a changed calibration needs
   a new Ford checksum, that has to be done in the tuning software before flashing. The reference
   tool has the same limitation.
2. **No DTC or live-data screens.** Service 0x19 / 0x22 are not exposed beyond the VIN read.
3. **No bench/harness mode** (28 V on pin 13 for off-vehicle programming) - the reference tool
   does not offer it either.
4. **Single-interface assumption.** The GUI connects to whichever J2534 driver is selected, one
   channel at a time; simultaneous dual-PCM flashing (Aston V12 layout) is a profile, not a
   second channel.
5. **Never tested against a live PCM.** Everything above is static analysis plus unit tests. The
   first run on a real BF should be a *read*, compared against a known-good dump, before any
   write.

## Recommended first session on the car (checklist)

1. Battery charger on, ignition on, engine off.
2. `psychoflash interfaces` -> driver visible.
3. `psychoflash info` -> voltage above 12.0 V, VIN (a locked PCM may show none - that is fine).
4. `psychoflash read --out BF_stock.bin` -> expect exactly 1,048,576 bytes, not all 0x00/0xFF.
5. Keep that file; it is the only way back.
6. Only then `psychoflash write --in <tuned>.bin --verify` (or the GUI).
7. If a write is interrupted: re-run `write`, or `erase` then `write`. The boot block at
   0x000000-0x0FFFFF is never touched, so the PCM still answers on the bus.

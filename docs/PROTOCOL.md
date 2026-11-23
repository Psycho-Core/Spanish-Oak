# Ford EEC-VI (Spanish Oak) flashing protocol, as implemented by PsychoFlash

This is the wire-level description used by `PsychoFlash.Core`. It matches what the reference
tool does; the differences are noted where they exist.

## 1. Physical layer

| Item | Value |
| --- | --- |
| Bus | ISO 15765-4 CAN, 11-bit identifiers |
| Bit rate | 500 kbit/s |
| Tester -> PCM | `0x7E0` |
| PCM -> tester | `0x7E8` |
| Programming voltage | Ford FEPS, 18 V, J2534 pin `0x80` |
| Session for programming | `0x10 0x85` (Ford ECU programming / EOL extended) |

PsychoFlash talks to the interface with SAE J2534-1 (v04.04). Every `PassThruMsg` payload begins
with the four byte CAN identifier, and ISO-TP segmentation is performed by the interface driver
because the channel is opened with `ProtocolID = ISO15765`:

```
00 00 07 E0 | 27 01
^^^^^^^^^^^   ^^^^^^ service payload
CAN id (4 bytes, network order)
```

Responses arrive the same way: four identifier bytes (`00 00 07 E8`) followed by the assembled
payload. `FordPcmClient.StripCanId` removes the prefix.

## 2. Services used

| Service | Name | Parameters used here |
| --- | --- | --- |
| `0x10` | DiagnosticSessionControl | `0x85` programming session |
| `0x11` | ECUReset | `0x01` hard reset (after a flash) |
| `0x14` | ClearDiagnosticInformation | `FF 00` |
| `0x22` | ReadDataByIdentifier | `F1 90` = VIN |
| `0x23` | ReadMemoryByAddress | `00` + 3-byte address + 2-byte length |
| `0x27` | SecurityAccess | `01` seed, `02` + 3-byte key |
| `0x34` | RequestDownload | 8 fixed parameter bytes (below) |
| `0x36` | TransferData | 0x400 byte blocks |
| `0x37` | TransferExit | none |
| `0x3E` | TesterPresent | `00` |
| `0x85` | ControlDtcSetting | `02 FF` (off) / `01 FF` (on) |
| `0xB2` | EraseFlash (Ford) | `AA` |

Positive responses are the request service id + `0x40` (`0x23` -> `0x63`, `0x27` -> `0x67`,
`0x34` -> `0x74`, `0x36` -> `0x76`, `0x37` -> `0x77`, `0xB2` -> `0xF2`). A negative response is
`7F <service> <NRC>`; `NRC 0x78` ("response pending") restarts the timeout and the tool keeps
reading.

## 3. Sequence for a full flash write

```
tester                                                    PCM
  | PassThruSetProgrammingVoltage(pin 0x80, 18 V)           |
  | operator: ignition OFF 3 s, then ON                     |
  | 10 85                                  ------------->  | 50 85 ...
  | 85 02 FF                               ------------->  | C5 02 FF
  | 3E 00                                  ------------->  | 7E 00
  | 27 01                                  ------------->  | 67 01 ss ss ss
  | 27 02 kk kk kk                         ------------->  | 67 02
  | B2 AA                                  ------------->  | F2     (or silence)
  | ... wait 5 s (60 s for the dual bank profile) ...       |
  | 34 00 01 00 00 00 0F 00 00             ------------->  | 74 20 04
  | 36 <0x400 bytes>                       ------------->  | 76
  | 36 <0x400 bytes>                       ------------->  | 76
  | ...                                                    |
  | 37                                     ------------->  | 77
  | 14 FF 00                               ------------->  | 54
  | PassThruSetProgrammingVoltage(pin 0x80, 0 V)            |
```

### The 0x34 parameter block

Reproduced verbatim from the reference tool, because Ford's EEC-VI framing does not follow
ISO 14229's `dataFormatIdentifier / addressAndLengthFormatIdentifier` layout cleanly:

| Profile | Payload after `0x34` |
| --- | --- |
| Falcon Spanish Oak 1024k (BF/FG/FGII/FGX) | `00 01 00 00 00 0F 00 00` |
| Dual-bank Black Oak (1.5 MB image) | `00 50 00 00 00 00 10 00` |

The 9-byte payload fits a single ISO-TP frame: the driver emits
`10 09 34 ...`, waits for the PCM's `30 ...` flow control frame, and sends `21 ...` with the
remainder - exactly the trace documented in the reference source:

```
can0 RX 7E0 [8] 10 09 34 00 50 00 00 00
can0 RX 7E8 [8] 30 00 00 00 00 00 00 00
can0 RX 7E0 [8] 21 10 00 00 00 00 00 00
```

## 4. Memory map

### Falcon Spanish Oak 1024k

```
read   0x000000 .. 0x0FFFFF   (1 MB, 0x400 byte blocks)
write  0x010000 .. 0x0FFFFF   (0xF0000 bytes; the first 64 kB boot block is never written)
erase  0xB2 0xAA              (whole flash)
```

The reference tool reads the full 1 MB, then discards one byte from the start of the file: the
0x23 response carries a `0x63` response byte in front of the data. PsychoFlash strips the prefix
while the data is still in memory (`FordPcmClient.ExtractReadData`), and derives it from the
answer length so that an operating system which also inserts a counter byte after the `0x63`
reads out correctly too - a fixed one or two byte strip would shift or reject the block.

### Dual-bank Black Oak (Aston Martin V8/V12 applications)

```
image  0x170000 bytes
read   0x000000 .. 0x06FFFF   -> image 0x00000
read   0x500000 .. 0x5FFFFF   -> image 0x70000
write  0x010000 .. 0x06FFFF
write  0x500000 .. 0x5FFFFF
```

`PcmProfile.MapAddressToImageOffset` performs that translation so callers always index the file
directly.

## 5. Security access

```
27 01                    ->  67 01 <seed0> <seed1> <seed2>
seed = (seed0 << 16) | (seed1 << 8) | seed2
key  = KeyGenMkI(seed, 0x08, 0x30, 0x61, 0xA4, 0xC5)
27 02 <k0> <k1> <k2>     ->  67 02                (granted)
                             7F 27 35             (invalid key)
                             7F 27 36             (too many attempts -> ignition cycle)
```

The key generator is a 24-bit Galois LFSR (mask `0xEF6FD7`, taps 20/15/12/5/3) seeded with
`0xC541A9`, fed 32 bits of `0x08 | mirror(seed)` and then 32 bits of `0xC5A46130`, with the
final state permuted into three bytes. `PsychoFlash.Core/Protocol/SeedKey.cs` implements it and
`docs/REVERSE_ENGINEERING.md` explains how it was recovered and verified.

## 6. Timing and recovery behaviour

| Situation | PsychoFlash behaviour |
| --- | --- |
| Erase answered with silence | Treated as normal; waits the profile's settle time (5 s / 60 s) |
| `NRC 0x78` response pending | Timeout restarts, up to 60 reads |
| Block write failure | Retried 3 times, then the flash is aborted with a clear message |
| `NRC 0x36` (too many key attempts) | Prompts for an ignition cycle and re-enters programming mode |
| Battery below 11.8 V | Warning before any erase/write |
| Cancellation (Ctrl+C / closing the UI) | Checked between blocks and after the erase |

## 7. Differences from the reference tool

* No licence/subscription check.
* Blocks are re-checked and retried instead of silently continuing.
* Reading strips the `0x63` response byte in memory instead of editing the saved file.
* A verification pass (`0x23` read-back of every written block) is offered after a write.
* The key algorithm is pluggable (`ISeedKeyAlgorithm`), so other EEC-VI variants can be added
  without touching the transport code.

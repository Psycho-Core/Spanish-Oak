# From a Barra PCM to a running engine (Patrol / any swap)

PsychoFlash moves a 1 MB calibration image between your PC and a Ford EEC-VI **Spanish Oak 1024k**
PCM. That is one step of a swap, not the whole thing. This page separates the parts so you know
what the tool does, what your tuner has to do, and what is wiring.

## Is this PCM supported?

A BF Falcon / SY Territory PCM such as **5R29-12A650-CC** (4.0L Barra, N/A, 4-speed auto) is the
Spanish Oak 1024k family this tool targets: CAN `0x7E0`/`0x7E8` at 500 kbit/s, FEPS 18 V, 1 MB
image, calibration area `0x010000-0x0FFFFF`, erase `B2 AA`. The same profile covers `6R29-` and
`8R29-` (FG/FG II/FG-X) parts, N/A and turbo, manual and automatic - the protocol does not care,
the *calibration* does.

The part number labels the family, so verify the individual PCM before writing to it:

1. `psychoflash identify` - the PCM reports its own part number, hardware and software identifiers
   (`0x22` DIDs). Compare with the label. Some PCMs only answer after the unlock, which is another
   reason to run `unlock --seed-only` first.
2. `psychoflash read --out stock.bin` - must be exactly 1,048,576 bytes, and
3. `psychoflash info --in stock.bin` - must show a non-blank image with calibration strings and no
   size mismatch.

If all three pass, the tool is talking to the PCM the way it expects. If the read comes back a
different size or all `00`/`FF`, stop: that means the profile is wrong for that PCM, and the same
mistake during a write is how boot blocks get erased.

## What PsychoFlash does

| Job | Command | Notes |
| --- | --- | --- |
| Dump the PCM to a `.bin` | `psychoflash read --out stock.bin` | Your backup. Do this before anything else. |
| Write a `.bin` to the PCM | `psychoflash write --in tuned.bin --verify` | Erases and reprograms `0x010000-0x0FFFFF`, never the boot block. |
| Read stored fault codes | `psychoflash dtcs` | `0x19`. Key on is enough - no FEPS, no unlock. |
| Clear fault codes | `psychoflash dtcs --clear` | `0x14 FF 00`. |
| Read the VIN | `psychoflash vin` | `0x22 F190`. Also proves the PCM is talking. |
| Battery voltage | `psychoflash info` | From the interface, not the PCM. |

## What PsychoFlash does **not** do (and no flasher does)

1. **It does not tune anything.** A Barra in a Patrol needs a calibration for *your* engine: MAF
   or map setup, injector size, boost control, rev limit, VCT, idle. That comes from a tuning
   package (HP Tuners / SCT and similar), not from a byte mover.
2. **It does not correct Ford checksums.** If your tuner's software recalculates them, the image
   is ready to flash. If you hex-edited a raw `.bin`, it probably is not, and the PCM may refuse
   to run it (or set `P0601`/`P0602`).
3. **It does not disable PATS.** The immobiliser decision lives inside the calibration. A tune
   with PATS active will crank and die even with perfect wiring - `P1260` is the code you will
   see. Buy the PCM with a known PATS-off tune, or have your tuner produce one.
4. **It has never run against real hardware.** Read `docs/AUDIT.md` first; the first session
   should be a read.

## Hardware the Barra PCM expects

The PCM is a computer expecting a whole car. The ones that stop a swap are, in the order people
hit them:

* **Fuel Pump Driver Module (FPDM).** BF/FG Barra runs a PWM fuel pump through a separate module
  on the CAN/ discrete line. No FPDM, no fuel pressure, and a `P1233` code. Bring the donor's
  FPDM and its pump wiring, or a suitable aftermarket controller.
* **Drive by wire.** Spanish Oak controls the throttle body motor (`P2100`/`P2101`/`P2135` if it
  is missing or unhappy) and reads the accelerator pedal. Use the Barra throttle body **and**
  pedal, and do not mix pedal types - the two-track correlation is what `P2135` is about.
* **Trigger signals.** Crank and cam sensors from the Barra, correct flexplate/flywheel pattern
  and sensor gap. `P0335`/`P0340` means the PCM is not seeing them.
* **Power and grounds.** Permanent 12 V, ignition feed, solid engine-to-body grounds. A voltage
  dip to 9 V during cranking resets the PCM (`P0562`).
* **CAN bus.** Not strictly needed to run, but the cluster, ABS and (on some calibrations) the
  FPDM expect the network. Keep the bus terminated with 120 Ω at both ends.
* **PATS hardware, if the tune keeps PATS.** Either the donor BEM plus a matched key, or a
  PATS-removed calibration.
* **FEPS for flashing.** 18 V on the programming pin needs a J2534 interface that really
  implements `PassThruSetProgrammingVoltage` - VCM II, OBDX Pro FT, Openport, Mongoose. Cheap
  clones often fake it and the unlock is then refused.

## First-start order of operations

```
1.  psychoFlash info                      battery 12.4 V+ with a charger connected
2.  psychoFlash vin                       PCM answers at all? (0x22 F190)
3.  psychoFlash read --out stock.bin      your backup, verify the SHA-256
4.  psychoFlash write --in tuned.bin --verify --yes     PATS-off, checksum-correct tune
5.  psychoFlash dtcs                      after the first crank attempt
```

### Reading the codes on a no-start

| Code | In a swap, it almost always means |
| --- | --- |
| `P1260` | PATS still active in the calibration, or the key/BEM handshake is missing |
| `P0601` / `P0602` | Calibration checksum or programming error - the tune is not checksum-correct |
| `P1233` / `P1235` | FPDM missing, unpowered, or not talking |
| `P0335` / `P0340` | Crank or cam signal missing (sensor, gap, wiring, wrong flywheel) |
| `P2100` / `P2101` / `P2112` / `P2135` | Throttle body or pedal missing, unplugged, or mismatched |
| `P0102` / `P0103` | MAF not wired or the intake is not what the tune expects |
| `P0562` / `P0563` | Supply voltage - charging system or grounds |
| `P1000` | Normal straight after a flash; readiness tests have not run yet |

## Where the tool still stops short

* **No live data yet.** You can read which code is set, but not watch RPM, coolant, throttle or
  fuel pressure while cranking. That is the next thing to add; a `0x22` data identifier stream is
  the mechanism, the Ford EEC-VI identifiers are the work.
* **No checksum calculation.** Deliberate: guessing a checksum algorithm and silently writing a
  wrong one is worse than refusing.
* **No immobiliser bypass.** If you need one, it is a calibration change, and it is your tuner's
  call, not a flashing tool's.

## Practical answer

If the engine cranks but will not fire: read the codes with `psychoflash dtcs` before touching
the wiring. `P1260` means the tune, `P1233` means the fuel pump module, `P0335` means the trigger,
`P2101` means the throttle. Those four cover most Barra swaps.

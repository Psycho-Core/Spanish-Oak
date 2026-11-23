# The immobiliser (PATS) and why PsychoFlash will not switch it off

If a converted Barra cranks but will not fire, `P1260` ("theft detected - engine disabled") is one
of the four usual causes - the others are the fuel pump module (`P1233`), the trigger signal
(`P0335`) and the throttle (`P2101`).

PsychoFlash reads that code. It will not edit a calibration to disable the immobiliser. That is a
deliberate limit, not a missing feature, and it has two separate reasons.

## 1. It is the one capability that changes what the tool *is*

A flasher that can read, write and verify is a maintenance tool. A flasher that can strip an
immobiliser from an arbitrary PCM is a theft tool: the same command, on a car park rather than a
project car, defeats the only anti-theft device the vehicle has. There is no way to publish that
capability and control where it goes - it would be in the same downloadable binary this project
hands out for free.

So: no immobiliser defeat, no key learning, no BEM emulation.

## 2. The data does not exist here, and guessing at it bricks PCMs

PATS on this generation is a calibration-level decision plus a key/BEM handshake. The specific
bytes that change are OS-revision dependent, and a wrong edit in that area of the calibration does
not throw a tidy error - it produces a PCM that will not run at all. Even if I wanted to build it,
"probably these bytes" is not a basis for writing flash.

## How it is actually done on a BA/BF (the 2026 reality)

PATS is switched off **in the calibration, by tuning software**, and the calibration is then
flashed with a tool like this one. On BF the community-standard path is
[PCMTec](https://www.pcmtec.com/), whose own how-to ("Run PCM standalone (eg engine swap). PATS
disable and Speed Source Setup") documents the whole procedure:

* It needs the **Professional edition or higher**, because the switches live in the VID block.
* Two parameters are involved: `auF16527` (PATS Alternate Switch, factory `0`) and `auF16595` (PATS
  Switch, factory `1`).
* It is documented for **BA/BF and earlier**. FG/FG-X have a different CAN layout, the speed-source
  setting is ignored there, and without the ABS module the PCM will not behave - so an FG
  conversion is a much bigger job than a BF one.
* Disabling PATS stops the BCM/ICC working, which is exactly why it is recommended **only** for
  swaps that no longer have that hardware - your Patrol, in other words.

### PATS is not the only thing that has to change

The same guide lists what else a standalone Barra PCM needs, and it is the part people miss:

* **Vehicle speed source.** With no ABS module there is no wheel-speed signal; the PCM derives
  speed from it, and speed-related limiters/lockouts follow. A manual conversion needs an
  aftermarket speed sensor wired to the VSS input.
* **Engine configuration word** (`auF2531`): upper nibble `0x10` N/A, `0x20` turbo; lower nibble
  `0x01` petrol.
* **Transmission and axle word** (`auF2193`): upper nibble `0x40` RWD (`0x80` 4WD, `0x20` AWD);
  lower byte `0x08` auto, PCM controlled (BTR).

So the job is "make this PCM a standalone engine controller", of which PATS is one switch.

### Who will do it for you

* A **tuner flash service** - several Australian shops advertise exactly this: "Barra conversion,
  PATS/Smartshield disable, manual conversion" as a stage-1 remap
  ([example](https://www.tiperformance.com.au/products/stage-1-ba-bf-fg-falcon-pcm-remap/),
  [another](https://mexicanhooncartel.com/product/stage-2-tune-pcm-remap-ba-bf-fg-fgx-falcon-fairmont-fairlane-naturally-aspirated-only/)).
  Fastest route: send or take the PCM, get it back able to run standalone.
* **Do it yourself with PCMTec** plus a supported interface and the right edition. More cost and
  learning, but you can iterate - and every calibration PCMTec writes is just a `.bin` that
  PsychoFlash can also carry if you prefer.

Either way PsychoFlash's part is unchanged: read the original, flash the resulting file, verify it,
and read the codes afterwards.

## The legitimate ways to get a started engine

| Route | What it involves | Notes |
| --- | --- | --- |
| **PATS-off calibration** | PCMTec (Professional+) or a tuner's remap service produces an image with PATS and the speed source set up for a standalone install; PsychoFlash flashes it like any other file | The normal route for BA/BF conversions. The tool's job is to transfer that file faithfully - which it does |
| **Keep the PATS hardware** | The donor BEM / smart junction box plus a matched key, wired as Ford intended | Most faithful, most wiring |
| **PATS module emulator** | Third-party module that answers the PCM's key handshake | Common in swaps; the PCM sees a valid key. Out of scope for this project but nothing here blocks it |

Whichever route you take, the check afterwards is the same:

```console
psychoflash dtcs
```

`P1260` present = the immobiliser is still active in whatever is in the PCM.
No codes, engine runs = sorted.

## What PsychoFlash *will* do around this

* Read and clear the codes that tell you PATS is the problem (`psychoflash dtcs`, `--clear`).
* Confirm the PCM is the right one and is answering (`identify`, `diagnose`).
* Flash a PATS-off calibration provided by someone who is licensed and equipped to produce one,
  and refuse one that is obviously wrong (wrong size, blank, no calibration text).
* Verify, byte for byte, that what is in the PCM is what you asked for.

That boundary is where a flashing tool should stop.

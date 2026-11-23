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

## The legitimate ways to get a started engine

| Route | What it involves | Notes |
| --- | --- | --- |
| **PATS-off calibration** | Your tuner's software produces an image with PATS disabled; PsychoFlash flashes it like any other file | The normal route for conversions. The tool's job is to transfer that file faithfully - which it does |
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

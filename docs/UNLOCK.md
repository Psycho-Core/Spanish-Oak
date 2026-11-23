# Unlocking a PCM (service `0x27`) - and what to do when it refuses

Everything PsychoFlash can flash needs security access first. This page explains the handshake,
what each refusal means, and the two escape hatches when the built-in Falcon algorithm is not the
one your PCM wants: a **keybag** of candidate constants, or an **algorithm plugin**.

## The handshake

```
tester                                            PCM
  27 01                              --------->   67 01 <seed 3 bytes>
  27 02 <key 3 bytes>                --------->   67 02            (granted)
                                                  7F 27 <NRC>      (refused)
```

Spanish Oak 1024k (BF / FG / FG II / FG-X Barra): **3 byte seed, 3 byte key**. The key comes from a
24-bit Galois LFSR seeded with `0xC541A9`, fed two 32-bit words built from the seed and five
per-application constants. The Falcon constants are `08 30 61 A4 C5`
(`FordSeedKeyMkI` in `src/PsychoFlash.Core/Protocol/SeedKey.cs`).

Verified against the reference implementation three ways: 12 known vectors in the self test, 29
random seeds against an emulation of the original IL, and 1012 checks in the Python reference.

## What a refusal means

| NRC | Text | What it actually tells you |
| --- | --- | --- |
| `0x35` | invalid key | The constants are wrong, **or** the PCM uses the other key shape (3-byte seed, 5-byte key) |
| `0x36` | exceeded number of attempts | Too many wrong keys. Ignition OFF, wait ~10 s, ON again |
| `0x37` | required time delay not expired | Same, just the timed version |
| `0x33` | security access denied | Not a key problem: FEPS, session or conditions are wrong |
| `0x22` | conditions not correct | Wrong session or no programming voltage - do `0x10 85` with FEPS applied |
| `0x12` | sub-function not supported | Wrong sub-function set for this PCM |

So `0x35` is the only NRC that means "your key generator is wrong". Everything else is a
sequencing or wiring problem, and retrying with another keybag candidate will not help.

## Escape hatch 1: a keybag

A keybag is a plain text file of candidate constant sets, tried in order:

```
# name          k0 k1 k2 k3 k4
falcon-mki      08 30 61 A4 C5
ba-xr6          11 22 33 44 55
fg-turbo        0xAA 0xBB 0xCC 0xDD 0xEE
compact         083061A4C5
```

Both forms are accepted (five separated constants or ten hex digits), `#` starts a comment, and
`0x` prefixes are allowed.

```console
psychoflash keybag --file bag.txt                 # parse and list, no hardware
psychoflash seed --seed 123456 --keybag bag.txt   # what every candidate would answer
psychoflash read --out stock.bin --keybag bag.txt # try them until one unlocks
```

**Every wrong candidate costs the PCM one security attempt.** A PCM typically allows 10 before a
lockout, and a lockout needs an ignition cycle. So: put the likely entry first, keep the bag short,
and use `psychoflash seed` to check candidates offline against a seed/key pair you already know
before touching the car.

Where a known-good pair comes from: any tool or log that has unlocked *your* PCM. If you have
`seed 0x123456 -> key 0x1DADBF` from a working session, `psychoflash seed --seed 123456 --keybag
bag.txt` tells you in one second which candidate is right - no attempts burned.

## Escape hatch 2: an algorithm plugin

If the PCM uses a different *shape* of key - the common other one is 3-byte seed, 5-byte key,
used by clusters, audio modules and many pre-2011 Ford modules - constants alone cannot express
it. Implement `ISeedKeyAlgorithm` in your own assembly and drop it next to the executable in
`plugins/`; `SeedKeyAlgorithms.LoadPlugins(directory)` picks up every public implementation with a
parameterless constructor:

```csharp
public sealed class FiveByteKey : ISeedKeyAlgorithm
{
    public string Name { get { return "My 5 byte key"; } }
    public int SeedLength { get { return 3; } }
    public int KeyLength { get { return 5; } }
    public uint ComputeKey(uint seed) { /* returns the low 4 bytes; see below */ }
}
```

One limitation worth knowing before you start: `ISeedKeyAlgorithm.ComputeKey` returns a `uint`
because the Falcon key is 3 bytes. A 5-byte key does not fit, so that interface needs widening
before 5-byte algorithms can be plugged in - that is a small, deliberate change to
`FordPcmClient.SecurityAccess` (and its tests), not something a plugin can work around today.

## The programming voltage pin

Two conventions are in circulation:

* The original Spanish Oak tool passed **`0x80`** to `PassThruSetProgrammingVoltage` (what the
  recovered code does), and that is the PsychoFlash default.
* Ford's FEPS is physically **OBD pin 13**, and several interfaces (the OBDX Pro family, for
  example) want the real pin number.

If applying FEPS fails or the unlock comes back `0x33`/`0x22` with everything else correct, try:

```console
psychoflash read --out stock.bin --feps-pin 13 --feps-voltage 18
```

`--feps-pin` takes decimal or `0x` hex; `--feps-voltage` is in volts.

## Prior art

[jakka351/Ford-ECU-Bruteforcer](https://github.com/jakka351/Ford-ECU-Bruteforcer) (C#, J2534,
same author as the original Spanish Oak tool) does the keybag approach in a GUI, with FEPS on OBD
pin 13, aimed at modules with a 3-byte seed and a **5-byte** key - a different shape from the
Spanish Oak PCM. It is prior art for the *idea* of a candidate list; no code or key data was taken
from it, because its licence requires the copyright holder's written consent to redistribute.

## Honest limits

* Acquiring constant sets is field work: from logs, from your own PCM's operating system, or from
  published research. PsychoFlash ships exactly one set - the Falcon one it can prove.
* PsychoFlash will not brute force the 3-byte key space (`2^24` candidates). At CAN turnaround
  times that is days, and the PCM locks out after ten tries.
* Nothing here has run against real hardware yet - the sequencing, NRC handling and lockout
  recovery are reviewed but unproven on a car. See `docs/AUDIT.md`.

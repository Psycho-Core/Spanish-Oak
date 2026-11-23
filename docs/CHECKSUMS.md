# Checksums: what PsychoFlash does, what it refuses to fake, and what would change that

Short version: **PsychoFlash does not rewrite Ford checksums, on purpose.** It reports checksum
candidates, it tells you exactly which bytes changed between two images, and it refuses to guess.
This page explains why that is the responsible choice and what evidence would let the tool do it
correctly.

## Why a wrong checksum is worse than none

An EEC-VI PCM validates its calibration after every power-up. If the checksum does not match, the
usual outcomes are:

* the PCM refuses to run the calibration (`P0601` memory checksum, `P0602` programming error),
* it runs in a limp or "no-start" mode,
* or, worst case, it accepts a value that it later flags intermittently, which reads as a wiring
  fault that does not exist.

A tool that "fixes" checksums by adding up bytes and writing the total somewhere plausible is
producing a number the PCM will disagree with. That is not a fix; it is a silent brick with a
green tick next to it.

## What the tool does instead

```console
psychoflash diff --a stock.bin --b tuned.bin      # which bytes changed, and are they in the write window
psychoflash checksums --in tuned.bin              # candidate sums for the whole image and each region
```

`diff` answers the question people actually have - *did this tune touch anything outside the
calibration area, and where?* `checksums` prints, per region:

```
sum32  (sum of bytes, mod 2^32)
sum16  (sum of bytes, mod 2^16)
crc32  (IEEE 802.3)
```

Compare a **stock** image and a **known-good tuned** image from the same operating system. Whichever
column changes consistently across several known-good tunes is the likely Ford scheme, and the
4 bytes it disagreed with tell you where the stored value lives. That is a two-image problem you
can solve with evidence instead of guesswork.

## Deriving the scheme yourself from two images

If you can get hold of **one** image from the same operating system that a known-good tool flashed
and the PCM accepted - a tuner's file, a mate's tune, a second-hand PCM you read out - the scheme
can be measured instead of bought:

```console
psychoflash checksums --in stock.bin --b known-good.bin
```

The scanner looks at the bytes that differ between the two, and reports any 2- or 4-byte field
whose stored value equals a candidate scheme (sum16, crc32, sum32) computed over the whole image,
the boot block or the calibration area - **in both images**. Matching twice is what makes a hit
credible: a random 4-byte word will not reproduce two different sums.

What you get back looks like:

```
0x000104  32-bit big endian  =  sum32 over calibration   (0x3F2281A4 -> 0x3F2287C6)
```

That single line tells you the algorithm, the region, the width and endianness and where the field
lives - everything a checksum fixer needs - and it came from evidence, not a guess. Pairs that
differ in more than 1% of the image are rejected: at that point 16-bit schemes match by chance and
the output would be noise.

Two honest limits: the scanner covers the three schemes above (a Ford-specific table-driven CRC is
not among them), and a PCM that excludes the checksum field from its own calculation needs the
region adjusted by hand. Both are visible the moment the candidate fails to reproduce the value.

## What would let PsychoFlash correct checksums properly

Any one of these, in order of usefulness:

1. **A pair of images** for the same OS: the stock read, and a version of it that a known-good tool
   flashed and the PCM accepted. Two pairs are better. From those, the checksum region, the
   algorithm and the field location can be derived and then *verified* against the second pair.
2. **The disassembly of the PCM's own checksum routine** (the EEC-VI boot/verification code), so the
   algorithm is known rather than inferred.
3. **A documented algorithm** for the specific OS family, from Ford service material or the tuning
   tool's documentation.

With (1) PsychoFlash can be taught the scheme for *that* OS and refuse to write any file whose
checksum does not match - which is the correct outcome: a flashing tool should be able to say
"this image is self-consistent" before it erases anything.

## The safe workflow today

```
1. psychoflash read  --out stock.bin            (from the PCM)
2. tuning software produces tuned.bin with correct checksums
3. psychoflash diff --a stock.bin --b tuned.bin (sanity: only calibration bytes changed)
4. psychoflash info --in tuned.bin              (right size, not blank, looks real)
5. psychoflash write --in tuned.bin --verify --yes
6. psychoflash dtcs                             (P0601/P0602 here means the checksum was wrong)
```

If step 6 shows `P0601` or `P0602`, the image was not self-consistent - that is the signal to stop
and go back to the tuning software, not to re-flash and hope.

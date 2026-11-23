# Reverse engineering "Spanish Oak Flash Tool.exe"

This document records how the reference tool was taken apart, what was learned, and what the
limits of that analysis are. Everything here is the result of static analysis of the published
binary; no live vehicle or PCM was used.

* Artefact: `Spanish Oak Flash Tool.exe` (9,111,221 bytes)
  SHA-256 `61f797f35859591b2a25ba22ef2fb2f1128eb68091c9543fcb6e2b5854e34a53`
* The tool that came out of it (the real program) is a 26,917,376 byte .NET assembly
  SHA-256 `797bd61468725effbe96a05c7ee5d0a072c2f86427deb691f8d44b0831d01879`
* Date of analysis: 2026-11-23

---

## 1. Executive summary

The file called `Spanish Oak Flash Tool.exe` is **not** the .NET program it presents itself as.
It is a three-layer sandwich:

```
Spanish Oak Flash Tool.exe          native Win32 launcher (PyInstaller bootloader)
   +-- PKG archive (Python 3.10)    overlay after the last PE section
        +-- script "Spanish"        ~35 MB code object holding one base64 constant
             +-- base64 blob        26.9 MB PE - the actual .NET WinForms application
```

The Python layer is a **dropper**: it decodes the embedded .NET executable into a temporary
file, launches it with `subprocess`, and deletes it afterwards. That is why the outer binary has
no `mscoree.dll` import, no CLR header and no readable application strings - and why a naive
"decompile the .NET assembly" workflow fails.

Once the inner assembly was recovered, the interesting parts fell into place:

| Question | Answer |
| --- | --- |
| CAN identifiers | request `0x7E0`, response `0x7E8`, ISO 15765, 500 kbit/s |
| Read method | service `0x23`, 3-byte address + 2-byte length, 0x400 byte blocks, 1 MB total |
| Erase | service `0xB2` with parameter `0xAA` |
| Download | `0x34` with a fixed 8 byte parameter block, then `0x36` blocks of 0x400 bytes, then `0x37` |
| Programming voltage | Ford FEPS, 18 V, pin `0x80`, applied with `PassThruSetProgrammingVoltage` |
| Unlock | `0x27 0x01` -> 3 byte seed, `0x27 0x02` + 3 byte key computed with a 24-bit Galois LFSR |
| Falcon image | 1 MB: read `0x000000-0x0FFFFF`, write `0x010000-0x0FFFFF` (the first 64 kB is left alone) |

The seed/key generator was recovered exactly and is reimplemented in
[`tools/seedkey_reference.py`](../tools/seedkey_reference.py) and in
`PsychoFlash.Core/Protocol/SeedKey.cs`.

---

## 2. Layer 1 - the outer executable

The PE is a native x64 image (`machine 0x8664`, PE32+), 300 kB of code with an 8.6 MB overlay.
Its import table is a giveaway: `CreateFileW`, `CreateProcessW`, `GetTempPathW`,
`ExpandEnvironmentStringsW`, `WriteFile`, `Sleep`, plus `DialogBoxIndirectParamW` and
`SendMessageW` for the error dialog. There is no managed entry point at all.

The overlay has entropy 8.0 and ends with the 88-byte PyInstaller cookie:

```
MEI 0c 0b 0a 0b 0e            magic
00 86 41 80                   package length  = 8,800,437
00 86 3e dd                   TOC offset
00 00 0d 40                   TOC length      = 3,392
00 00 01 36                   Python version  = 310
"python310.dll"               runtime DLL name
```

Parsing the table of contents yields 61 entries: the Python modules (`pyimod01_archive`,
`pyimod02_importers`, `pyimod03_ctypes`, ...), the runtime (`python310.dll`, `VCRUNTIME140.dll`,
`ucrtbase.dll`, the API set shims), `base_library.zip`, and a single script named **`Spanish`**
which decompresses to 35,890,206 bytes.

`tools/pyinstaller_extract.py` in this repository does that parsing with the standard library
only:

```console
$ python3 tools/pyinstaller_extract.py "Spanish Oak Flash Tool.exe" --list-only
archive start : 0x4BE00
python        : 3.10    runtime: python310.dll
entries       : 61
  [s] Spanish                                    35890206 bytes
  ...
```

## 3. Layer 2 - the Python dropper

The marshalled code object for `Spanish` contains exactly one large constant: a base64 string
starting `TVqQAAMAAAAEAAAA//8AALg...` - the classic base64 of an `MZ` header. Decoding it gives
the 26.9 MB .NET assembly.

The module's code is minimal and is best described by its name table:

```
names: base64 os sys tempfile subprocess exe mkstemp fd path b64decode code write close call result remove
```

In pseudo-source:

```python
blob = base64.b64decode(BIG_CONSTANT)
fd, path = tempfile.mkstemp(suffix=".exe")
os.write(fd, blob); os.close(fd)
result = subprocess.call([path])
os.remove(path)
```

Nothing else. The Python layer exists to hide the managed payload from scanners and to hide the
application strings from casual inspection - it contains no flashing logic of its own.

> Decompiling the 3.10 bytecode is not required for this analysis; the code object's names,
> constants and tiny size are enough to see what it does. The `xdis` package loads 3.10 code
> objects from Python 3.11 if you want to confirm it yourself.

## 4. Layer 3 - the .NET application

The recovered assembly identifies itself as `Spanish Oak Flash Tool`, version `0.0.0.0`, and
references `mscorlib`, `System.Windows.Forms`, `MetroFramework`, `DarkUI`, `System.Drawing`,
`System.Configuration`.

Obfuscation characteristics:

* 73 types, 378 methods; type and method names are replaced with unprintable characters
  (`?57?.?58?`, `?98?`, ...) that render as `?N?` in most tools.
* Every method header claims a body of roughly 100 kB of `0x00` padding, while the **real IL
  is stored at the tail of the slot**, immediately before the next method header. Recovering it
  is therefore a matter of taking the bytes between two headers and stripping the zero padding
  (see `tools/recover_il.py`).
* 253 methods carry a body RVA; 249 decode cleanly once the padding is stripped (the
  remaining four are `finally`-heavy bodies whose exception-handling sections were stripped).
* The `#US` (user string) heap is only 14 kB, so UI strings are decrypted at run time. Type,
  field and method *references* to framework APIs survive, which is what made the protocol
  recoverable.
* Costura is used to embed dependencies (`costura.metroframework.dll.compressed`,
  `costura.darkui.dll.compressed`, ...), and the manifest is a plain .NET WinForms one.

Surviving names that matter, taken from the `#Strings` heap:

```
PassThruOpen   PassThruClose      PassThruConnect   PassThruDisconnect
PassThruReadMsgs                 PassThruWriteMsgs
PassThruStartMsgFilter           PassThruStopMsgFilter
PassThruSetProgrammingVoltage    PassThruIoctl
PassThruReadVersion              PassThruGetLastError
ReadBatteryVoltage  ReadProgrammingVoltage  ClearRxBuffer  ClearTxBuffer
SW_CAN_BusSpeed     FiveBaudInit   FastInit   ReadAllMessages
ConfigApplication   DeviceName     UnlockKey  UnlockTimestamp
```

So the shipping build carries the full SAE J2534 v04.04 wrapper (the same surface the repo's
decompiled C# uses) plus a small licensing layer: `UnlockKey` / `UnlockTimestamp` /
`DeviceName` / `ConfigApplication` are the classic fields of a time-limited, per-device
subscription check (the product is sold as a 24-hour rental). **The licensing logic was not
analysed and is deliberately not reproduced in PsychoFlash** - the new tool has no licence
check at all.

### Recovering method bodies

`tools/recover_il.py` implements the algorithm described above and disassembles 249 of the 378
methods, resolving metadata tokens back to names. Example of a recovered property accessor:

```text
===== [39] set_Vendor ... =====
   19 ldarg.0
   20 ldarg.1
   21 stfld field 0x0400015E
   25 ret
```

## 5. What the program actually sends

The repo already contained two ILSpy dumps (`powertrainControlModule.cs` and
`0x10 diagnosticSessionControl.cs`) of an *unobfuscated* build of the same application. Those
files name the services and show the message construction, and the obfuscated binary confirms
them. The full frame level detail is in [PROTOCOL.md](PROTOCOL.md); the highlights:

* The application's messages are SAE J2534 `PassThruMsg` payloads: four bytes of CAN identifier
  (`00 00 07 E0`) followed by the diagnostic payload.
* The class constructor of the main form sets the CAN identifiers
  (`this.id1 = 0x07; this.id2 = 0xE0;` -> `0x7E0`), the erase message
  (`new byte[] { 0x00, 0xB2, 0xAA }`) and the programming voltage pair (`0x80`, `18`).
* Read: `23 00 <addr24> <len16>`, 0x400 bytes at a time over 0x000000..0x100000.
* Write: erase `B2 AA`, wait, `34 <8 bytes>`, then `36` blocks of 0x400 from 0x10000 to 0xFFC00,
  then `37`.
* The download parameter block differs per PCM: Falcon `00 01 00 00 00 0F 00 00`, dual-bank
  Black Oak `00 50 00 00 00 00 10 00`.
* Security access: `27 01`, seed = `(r[0]<<16)|(r[1]<<8)|r[2]`, key formatted as six hex digits
  and sent as `27 02 k0 k1 k2`.

## 6. The seed/key generator

The call site was found by looking for the five constants the tool documents in its own source
(`requestSecurityAccess0x27(0x08, 0x30, 0x61, 0xA4, 0xC5)`):

```text
   19 ldarg.0
   20 ldc.i4.8        ; 0x08
   21 ldc.i4.s 48     ; 0x30
   23 ldc.i4.s 97     ; 0x61
   25 ldc.i4   164    ; 0xA4
   30 ldc.i4   197    ; 0xC5
   35 call    0x06000108   ; -> requestSecurityAccess(seedKey0..4)
```

Method token `0x06000108` (RID 264, method 263 in table order) is `requestSecurityAccess0x27`;
inside it the key generator is called with the seed and the same five constants
(`0x06000109`, RID 265). Its 615-byte body is a straight-line implementation of a 24-bit
Galois LFSR:

```
seed bytes b0 b1 b2  ->  word1 = 0x08 | (b2 << 16) | (b1 << 8) | b0
word2               =  0xC5A46130
state = 0xC541A9
state = 32 rounds with word1      (bit i of the word XORed into the LSB each round)
state = 32 rounds with word2
key   = bit-permutation of the final state (nibbles 4/12/16/20/8 mixed)
```

Both loops use the same tap mask `0xEF6FD7` and feedback taps at bits 20, 15, 12, 5 and 3; the
shifted-out bit (bit 23 of the temporary) is XORed into every tap.

### Verification

Rather than trusting a hand translation, the recovered IL was executed by a small interpreter
(`tools/recover_il.py emulate`) and compared with the C#/Python port over 29 seeds - 29/29 match:

```console
$ python3 tools/recover_il.py emulate SpanishOak.dll --method 264 --args 0,0x123456,0x08,0x30,0x61,0xA4,0xC5
method [264] ?98? -> 0x1DADBF

$ python3 tools/seedkey_reference.py 123456
seed 0x123456  ->  key 0x1DADBF   (send: 27 02 1D AD BF)
```

The same vectors are baked into `tests/PsychoFlash.SelfTest` so a regression in the C# port
fails the build.

## 7. Limits of this analysis

* The `#US` string heap is encrypted; UI text, error strings and the licensing prompt were not
  recovered. Their content is irrelevant to flashing.
* `requestDownload`'s eight parameter bytes are reproduced **verbatim** from the reference tool
  because their internal layout (data format / address-and-length format / address / size) does
  not follow ISO 14229 cleanly for Ford's EEC-VI. Replaying the exact bytes is the safe choice.
* Timing constants (the 5 s settle after erase, the 60 s for the dual-bank variant) come from
  the reference implementation's own delays, not from a datasheet.
* Nothing here was validated against a real PCM; see the safety notes in the main README.

## 8. Notes on the reimplementation

PsychoFlash is a clean-room implementation written from the *protocol facts* above (CAN
identifiers, service numbers, block sizes, frame shapes) and from the public SAE J2534-1 and
ISO 14229 specifications. No source from the reference tool is copied, no obfuscated code is
reused, and the proprietary licensing system is not reproduced. The original application
header in the repository states that its code may only be redistributed with the author's
written permission - that header is not carried over here, and none of its code is used.

# Spanish Oak Tool (BF Barra PCM)

An original, clean-room J2534/UDS flash read-write utility for the Ford BF Falcon
Barra "Spanish Oak" PCM (1024KB flash EEC-V/VI style module), written from the
public SAE J2534-1 pass-thru API and ISO 14229 (UDS) / ISO 15765 (CAN transport)
specifications.

This is **not** a decompiled or copied version of any existing commercial tool.
It independently implements the same standards-based diagnostic protocol that
any J2534 tool must use to talk to this ECU family. No code, binary data, or
algorithm was copied from any third-party product.

## What it does

- `info`   — open a J2534 device, connect to the PCM on CAN, read ECU identification
             (VIN, part number, OS ID, etc. via UDS ReadDataByIdentifier where supported)
- `read`   — read the full 1MB flash image from the PCM to a `.bin` file
- `write`  — erase and write a `.bin` image back to the PCM flash
- `probe`  — basic connectivity / session-control smoke test, no flash access

## What's intentionally NOT included

The PCM's `0x27` SecurityAccess seed→key algorithm is **not implemented**. That
algorithm is reverse-engineered, proprietary knowledge belonging to whoever
derived it — it is not part of any public standard, and copying it from a
commercial tool would just be copying their IP under a different file name,
not "writing your own version."

`src/Pcm/ISecurityKeyAlgorithm.cs` defines the interface the tool expects.
You need to supply your own implementation (e.g. from your own bench-testing /
seed-key capture, from documentation you're licensed to use, or from the
original author directly) and drop it in before `write`/full `read` will work
past the security-access gate. Everything else — session handling, framing,
block transfer, erase sequencing, progress reporting, CLI — is original and
functions without it (e.g. `probe` and `info` work with no security access).

## Build

Requires the .NET 8 SDK and a vendor J2534 DLL (e.g. from your adapter's
Windows driver install, such as the OBDxPro FT or Ford VCM2 driver package).
This only runs on Windows (J2534 is a Windows DLL API).

```
dotnet build
dotnet run -- probe --dll "C:\Program Files (x86)\...\J2534.dll"
```

## Protocol facts this implementation uses

These are standard/observable facts about the vehicle network, not proprietary
code — the same facts any J2534 tool for this ECU needs:

- ISO 15765 CAN transport, 500 kbps, 11-bit IDs
- PCM request ID `0x7E0`, PCM response ID `0x7E8`
- UDS services: `0x10` DiagnosticSessionControl, `0x27` SecurityAccess,
  `0x23` ReadMemoryByAddress, `0x34` RequestDownload, `0x36` TransferData,
  `0x37` RequestTransferExit, `0x3E` TesterPresent, `0x14`
  ClearDiagnosticInformation
- Flash size 0x100000 (1,048,576 bytes), read in 0x400-byte blocks

Always confirm these against your own captured bus traffic before relying on
them — ECU firmware/calibration variants can differ.

## Safety

- `write` always reads back and verifies, and refuses to proceed if the
  supplied image size doesn't match the expected flash size.
- Low battery voltage is not currently monitored by this tool (the original
  has a voltage-check UI step) — **use a battery support/charger during any
  write**, a bricked PCM from a low-voltage write is a real risk on these
  modules.
- There is no dry-run/simulated mode yet; treat `write` as irreversible until
  you've verified `read` + `info` work cleanly on your bench setup first.

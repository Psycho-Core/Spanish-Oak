namespace SpanishOakTool.Pcm;

/// <summary>
/// Computes the UDS SecurityAccess (0x27) key from the seed the ECU returns.
///
/// Deliberately NOT implemented here. The seed-to-key transform for this PCM
/// is reverse-engineered, proprietary knowledge — not published in any
/// standard — and isn't something this clean-room project copies from any
/// existing tool. Supply your own implementation (from your own research,
/// from documentation you're licensed to use, or from the module/tool vendor
/// directly) by implementing this interface and wiring it up in Program.cs.
///
/// Everything upstream of this (session control, framing, flash read/erase/
/// write block transfer, CLI) works without it; only the security-gated
/// portions of `read`/`write` need a real implementation here.
/// </summary>
public interface ISecurityKeyAlgorithm
{
    byte[] ComputeKey(byte[] seed);
}

/// <summary>
/// Placeholder that always fails, so misconfiguration is loud instead of
/// silently sending garbage keys to the ECU.
/// </summary>
public sealed class NotImplementedSecurityKeyAlgorithm : ISecurityKeyAlgorithm
{
    public byte[] ComputeKey(byte[] seed) =>
        throw new System.NotImplementedException(
            "No SecurityAccess key algorithm is configured. See src/Pcm/ISecurityKeyAlgorithm.cs — " +
            "this tool intentionally does not ship a seed-key implementation for this PCM.");
}

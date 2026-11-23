using System;

namespace PsychoFlash.Core.J2534
{
    /// <summary>
    /// The transport the protocol code needs from a J2534 interface - everything the client and the
    /// flashing service actually call, and nothing else.
    ///
    /// It exists so the real protocol code can be driven by something other than a vendor DLL: the
    /// self test runs a simulated PCM behind this interface, which exercises framing, response
    /// matching, unlock, erase, download, transfer and verify end to end without a car.
    /// </summary>
    public interface IPassThruDevice : IDisposable
    {
        /// <summary>Firmware string reported by the interface.</summary>
        string FirmwareVersion { get; }

        /// <summary>DLL version string.</summary>
        string DllVersion { get; }

        /// <summary>J2534 API version string.</summary>
        string ApiVersion { get; }

        /// <summary>Battery voltage in millivolts.</summary>
        uint ReadBatteryVoltage();

        /// <summary>Applies (or removes) the programming voltage on the given pin.</summary>
        void SetProgrammingVoltage(uint pinNumber, uint voltage);

        /// <summary>Drops anything queued in the interface's receive buffer.</summary>
        void ClearBuffers();

        /// <summary>Sends one ISO15765 message: four byte CAN identifier plus the payload.</summary>
        void WriteMessage(byte[] canIdAndPayload, TxFlags flags, uint timeoutMs);

        /// <summary>
        /// Receives one ISO15765 message, or throws <see cref="TimeoutException"/> when nothing
        /// arrives before the timeout.
        /// </summary>
        byte[] ReadMessage(uint timeoutMs);
    }
}

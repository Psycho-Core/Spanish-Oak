using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PsychoFlash.Core.J2534
{
    /// <summary>
    /// Manually bound SAE J2534-1 v04.04 function table. Loading the DLL by path keeps PsychoFlash
    /// working on machines with several interfaces installed, and avoids a hard link-time
    /// dependency on a specific vendor driver.
    /// </summary>
    public sealed class J2534Library : IDisposable
    {
        // --- native loader -------------------------------------------------------------
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        // --- registry reader (advapi32 P/Invoke: no extra package needed) ---------------
        private static readonly IntPtr HKeyLocalMachine = new IntPtr(unchecked((int)0x80000002));
        private const uint KeyRead = 0x20019;
        private const uint KeyWow6464 = 0x0100;
        private const uint KeyWow6432 = 0x0200;
        private const int ErrorNoMoreItems = 259;
        private const int ErrorSuccess = 0;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW", SetLastError = true)]
        private static extern int RegOpenKeyEx(IntPtr hKey, string subKey, uint options, uint samDesired, out IntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegEnumKeyExW", SetLastError = true)]
        private static extern int RegEnumKeyEx(IntPtr hKey, uint index, StringBuilder name, ref uint nameLength,
                                               IntPtr reserved, IntPtr className, IntPtr classLength, IntPtr lastWriteTime);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW", SetLastError = true)]
        private static extern int RegQueryValueEx(IntPtr hKey, string valueName, IntPtr reserved, out uint type,
                                                  StringBuilder data, ref uint dataLength);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr hKey);

        private static IList<string> ReadSubKeyNames(string path, uint viewFlag)
        {
            List<string> names = new List<string>();
            IntPtr key;
            if (RegOpenKeyEx(HKeyLocalMachine, path, 0, KeyRead | viewFlag, out key) != ErrorSuccess) return names;
            try
            {
                uint index = 0;
                while (true)
                {
                    StringBuilder buffer = new StringBuilder(256);
                    uint length = 256;
                    int result = RegEnumKeyEx(key, index, buffer, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (result == ErrorNoMoreItems) break;
                    if (result != ErrorSuccess) break;
                    names.Add(buffer.ToString());
                    index++;
                }
            }
            finally
            {
                RegCloseKey(key);
            }
            return names;
        }

        private static string ReadStringValue(IntPtr key, string valueName)
        {
            StringBuilder buffer = new StringBuilder(1024);
            uint length = (uint)(buffer.Capacity * 2);
            uint type;
            int result = RegQueryValueEx(key, valueName, IntPtr.Zero, out type, buffer, ref length);
            return result == ErrorSuccess ? buffer.ToString() : string.Empty;
        }

        // --- J2534 entry points --------------------------------------------------------
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public delegate J2534Err PassThruOpenDelegate(IntPtr name, out uint deviceId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruCloseDelegate(uint deviceId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruConnectDelegate(uint deviceId, ProtocolId protocolId, uint flags, uint baudRate, out uint channelId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruDisconnectDelegate(uint channelId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruReadMsgsDelegate(uint channelId, IntPtr msgs, ref uint numMsgs, uint timeout);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruWriteMsgsDelegate(uint channelId, IntPtr msgs, ref uint numMsgs, uint timeout);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruStartPeriodicMsgDelegate(uint channelId, IntPtr msg, out uint msgId, uint timeInterval);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruStopPeriodicMsgDelegate(uint channelId, uint msgId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruStartMsgFilterDelegate(uint channelId, FilterType filterType, IntPtr maskMsg, IntPtr patternMsg, IntPtr flowControlMsg, ref uint filterId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruStopMsgFilterDelegate(uint channelId, uint filterId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruSetProgrammingVoltageDelegate(uint deviceId, uint pinNumber, uint voltage);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruReadVersionDelegate(uint deviceId, IntPtr firmwareVersion, IntPtr dllVersion, IntPtr apiVersion);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruGetLastErrorDelegate(IntPtr description);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate J2534Err PassThruIoctlDelegate(uint channelId, IoctlId ioctlId, IntPtr input, IntPtr output);

        private readonly IntPtr _module;
        private readonly string _path;

        public PassThruOpenDelegate Open { get; private set; }
        public PassThruCloseDelegate Close { get; private set; }
        public PassThruConnectDelegate Connect { get; private set; }
        public PassThruDisconnectDelegate Disconnect { get; private set; }
        public PassThruReadMsgsDelegate ReadMsgs { get; private set; }
        public PassThruWriteMsgsDelegate WriteMsgs { get; private set; }
        public PassThruStartPeriodicMsgDelegate StartPeriodicMsg { get; private set; }
        public PassThruStopPeriodicMsgDelegate StopPeriodicMsg { get; private set; }
        public PassThruStartMsgFilterDelegate StartMsgFilter { get; private set; }
        public PassThruStopMsgFilterDelegate StopMsgFilter { get; private set; }
        public PassThruSetProgrammingVoltageDelegate SetProgrammingVoltage { get; private set; }
        public PassThruReadVersionDelegate ReadVersion { get; private set; }
        public PassThruGetLastErrorDelegate GetLastError { get; private set; }
        public PassThruIoctlDelegate Ioctl { get; private set; }

        public string Path { get { return _path; } }

        private J2534Library(string path, IntPtr module)
        {
            _path = path;
            _module = module;
            Open = Bind<PassThruOpenDelegate>("PassThruOpen");
            Close = Bind<PassThruCloseDelegate>("PassThruClose");
            Connect = Bind<PassThruConnectDelegate>("PassThruConnect");
            Disconnect = Bind<PassThruDisconnectDelegate>("PassThruDisconnect");
            ReadMsgs = Bind<PassThruReadMsgsDelegate>("PassThruReadMsgs");
            WriteMsgs = Bind<PassThruWriteMsgsDelegate>("PassThruWriteMsgs");
            StartPeriodicMsg = Bind<PassThruStartPeriodicMsgDelegate>("PassThruStartPeriodicMsg");
            StopPeriodicMsg = Bind<PassThruStopPeriodicMsgDelegate>("PassThruStopPeriodicMsg");
            StartMsgFilter = Bind<PassThruStartMsgFilterDelegate>("PassThruStartMsgFilter");
            StopMsgFilter = Bind<PassThruStopMsgFilterDelegate>("PassThruStopMsgFilter");
            SetProgrammingVoltage = Bind<PassThruSetProgrammingVoltageDelegate>("PassThruSetProgrammingVoltage");
            ReadVersion = Bind<PassThruReadVersionDelegate>("PassThruReadVersion");
            GetLastError = Bind<PassThruGetLastErrorDelegate>("PassThruGetLastError");
            Ioctl = Bind<PassThruIoctlDelegate>("PassThruIoctl");
        }

        private T Bind<T>(string export) where T : class
        {
            IntPtr fn = GetProcAddress(_module, export);
            if (fn == IntPtr.Zero)
            {
                // Older drivers export only the ANSI names; try the decorated variants.
                fn = GetProcAddress(_module, export + "A");
            }
            if (fn == IntPtr.Zero)
            {
                throw new J2534Exception("GetProcAddress(" + export + ")", J2534Err.ERR_NOT_SUPPORTED);
            }
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        /// <summary>Loads a specific J2534 driver DLL.</summary>
        public static J2534Library Load(string dllPath)
        {
            if (string.IsNullOrWhiteSpace(dllPath)) throw new ArgumentException("dllPath");
            IntPtr module = LoadLibraryW(dllPath);
            if (module == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException("Unable to load '" + dllPath + "' (Win32 error " + err + "). Install the interface vendor driver or pick the correct DLL.");
            }
            return new J2534Library(dllPath, module);
        }

        /// <summary>One J2534 interface registered under HKLM\SOFTWARE\PassThruSupport.04.04.</summary>
        public sealed class Registration
        {
            public string Name { get; set; }
            public string Vendor { get; set; }
            public string FunctionLibrary { get; set; }
            public string ConfigApplication { get; set; }

            public override string ToString()
            {
                return string.IsNullOrEmpty(Vendor) ? Name : Name + " (" + Vendor + ")";
            }
        }

        /// <summary>Enumerates every J2534 interface the registry knows about (64 and 32 bit views).</summary>
        public static IList<Registration> FindInstalledInterfaces()
        {
            List<Registration> found = new List<Registration>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string root = @"SOFTWARE\PassThruSupport.04.04";

            foreach (uint view in new uint[] { KeyWow6464, KeyWow6432 })
            {
                foreach (string subName in ReadSubKeyNames(root, view))
                {
                    IntPtr key;
                    if (RegOpenKeyEx(HKeyLocalMachine, root + "\\" + subName, 0, KeyRead | view, out key) != ErrorSuccess)
                    {
                        continue;
                    }
                    try
                    {
                        string dll = ReadStringValue(key, "FunctionLibrary");
                        if (string.IsNullOrEmpty(dll)) continue;
                        if (!seen.Add(dll)) continue;

                        Registration reg = new Registration();
                        reg.Name = ReadStringValue(key, "Name");
                        if (string.IsNullOrEmpty(reg.Name)) reg.Name = subName;
                        reg.Vendor = ReadStringValue(key, "Vendor");
                        reg.FunctionLibrary = dll;
                        reg.ConfigApplication = ReadStringValue(key, "ConfigApplication");
                        found.Add(reg);
                    }
                    finally
                    {
                        RegCloseKey(key);
                    }
                }
            }
            return found;
        }

        public void Dispose()
        {
            if (_module != IntPtr.Zero)
            {
                FreeLibrary(_module);
            }
        }
    }
}

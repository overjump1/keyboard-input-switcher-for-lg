using System;
using System.Runtime.InteropServices;

namespace LgInputSwitcher
{
    // Minimal NVAPI interop. nvapi64.dll ships with the NVIDIA driver and exports a single
    // function, nvapi_QueryInterface, which hands out function pointers by ID.
    // IDs and struct layouts are from NVIDIA/nvapi (nvapi_interface.h / nvapi.h).
    internal static class Nvapi
    {
        public const int NVAPI_OK = 0;
        public const int NVAPI_END_ENUMERATION = -7;
        public const int NVAPI_MAX_PHYSICAL_GPUS = 64;
        public const uint NVAPI_I2C_SPEED_DEPRECATED = 0xFFFF;
        public const uint NV_GPU_CONNECTED_IDS_FLAG_UNCACHED = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct NV_I2C_INFO_V3
        {
            public uint version;
            public uint displayMask;
            public byte bIsDDCPort;
            public byte i2cDevAddress;
            public IntPtr pbI2cRegAddress;
            public uint regAddrSize;
            public IntPtr pbData;
            public uint cbSize;
            public uint i2cSpeed;
            public uint i2cSpeedKhz;
            public byte portId;
            public uint bIsPortIdSet;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NV_GPU_DISPLAYIDS
        {
            public uint version;
            public int connectorType;
            public uint displayId;
            public uint flags; // bitfield: isDynamic, isMultiStreamRootNode, isActive, ... isConnected (bit 6)
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NV_EDID_V3
        {
            public uint version;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] EDID_Data;
            public uint sizeofEDID;
            public uint edidId;
            public uint offset;
        }

        public static uint MakeVersion<T>(int ver) where T : struct
        {
            return (uint)Marshal.SizeOf(typeof(T)) | ((uint)ver << 16);
        }

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EnumPhysicalGPUsFn([Out] IntPtr[] handles, out uint count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetConnectedDisplayIdsFn(IntPtr gpu, [In, Out] NV_GPU_DISPLAYIDS[] ids, ref uint count, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetEDIDFn(IntPtr gpu, uint displayOutputId, ref NV_EDID_V3 edid);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int I2CFn(IntPtr gpu, ref NV_I2C_INFO_V3 info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetErrorMessageFn(int status, [Out] byte[] desc);

        private static bool _initialized;
        private static EnumPhysicalGPUsFn _enumPhysicalGPUs;
        private static GetConnectedDisplayIdsFn _getConnectedDisplayIds;
        private static GetEDIDFn _getEDID;
        private static I2CFn _i2cWrite;
        private static I2CFn _i2cRead;
        private static GetErrorMessageFn _getErrorMessage;

        private static T Get<T>(uint id) where T : class
        {
            IntPtr p = QueryInterface(id);
            if (p == IntPtr.Zero)
                throw new NvapiException(string.Format("NVAPI function 0x{0:X8} not available", id));
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        public static void Initialize()
        {
            if (_initialized) return;
            if (Marshal.SizeOf(typeof(NV_I2C_INFO_V3)) != 64)
                throw new NvapiException("Unexpected NV_I2C_INFO_V3 size; must run as 64-bit");

            Check(Get<InitializeFn>(0x0150E828)(), "NvAPI_Initialize");
            _enumPhysicalGPUs = Get<EnumPhysicalGPUsFn>(0xE5AC921F);
            _getConnectedDisplayIds = Get<GetConnectedDisplayIdsFn>(0x0078DBA2);
            _getEDID = Get<GetEDIDFn>(0x37D32E69);
            _i2cWrite = Get<I2CFn>(0xE812EB07);
            _i2cRead = Get<I2CFn>(0x2FDE12C5);
            _getErrorMessage = Get<GetErrorMessageFn>(0x6C2D048C);
            _initialized = true;
        }

        public static IntPtr[] EnumPhysicalGPUs()
        {
            var handles = new IntPtr[NVAPI_MAX_PHYSICAL_GPUS];
            uint count;
            Check(_enumPhysicalGPUs(handles, out count), "NvAPI_EnumPhysicalGPUs");
            var result = new IntPtr[count];
            Array.Copy(handles, result, count);
            return result;
        }

        public static NV_GPU_DISPLAYIDS[] GetConnectedDisplayIds(IntPtr gpu, uint flags)
        {
            uint count = 0;
            Check(_getConnectedDisplayIds(gpu, null, ref count, flags), "NvAPI_GPU_GetConnectedDisplayIds");
            if (count == 0) return new NV_GPU_DISPLAYIDS[0];
            var ids = new NV_GPU_DISPLAYIDS[count];
            for (int i = 0; i < ids.Length; i++)
                ids[i].version = MakeVersion<NV_GPU_DISPLAYIDS>(3);
            Check(_getConnectedDisplayIds(gpu, ids, ref count, flags), "NvAPI_GPU_GetConnectedDisplayIds");
            return ids;
        }

        public static byte[] GetEDID(IntPtr gpu, uint displayId)
        {
            var edid = new NV_EDID_V3();
            edid.version = MakeVersion<NV_EDID_V3>(3);
            edid.EDID_Data = new byte[256];
            Check(_getEDID(gpu, displayId, ref edid), "NvAPI_GPU_GetEDID");
            return edid.EDID_Data;
        }

        // Raw I2C transfer on the display's DDC port. devAddress is the 8-bit (shifted) address.
        public static void I2C(bool write, IntPtr gpu, uint displayId, byte devAddress, byte[] register, byte[] data)
        {
            GCHandle hReg = default(GCHandle), hData = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var info = new NV_I2C_INFO_V3();
                info.version = MakeVersion<NV_I2C_INFO_V3>(3);
                info.displayMask = displayId;
                info.bIsDDCPort = 1;
                info.i2cDevAddress = devAddress;
                if (register != null && register.Length > 0)
                {
                    hReg = GCHandle.Alloc(register, GCHandleType.Pinned);
                    info.pbI2cRegAddress = hReg.AddrOfPinnedObject();
                    info.regAddrSize = (uint)register.Length;
                }
                info.pbData = hData.AddrOfPinnedObject();
                info.cbSize = (uint)data.Length;
                info.i2cSpeed = NVAPI_I2C_SPEED_DEPRECATED;
                info.i2cSpeedKhz = 0; // NVAPI_I2C_SPEED_DEFAULT
                Check(write ? _i2cWrite(gpu, ref info) : _i2cRead(gpu, ref info), write ? "NvAPI_I2CWrite" : "NvAPI_I2CRead");
            }
            finally
            {
                if (hReg.IsAllocated) hReg.Free();
                hData.Free();
            }
        }

        public static string ErrorMessage(int status)
        {
            try
            {
                var buf = new byte[64];
                if (_getErrorMessage != null && _getErrorMessage(status, buf) == NVAPI_OK)
                {
                    int len = Array.IndexOf(buf, (byte)0);
                    return System.Text.Encoding.ASCII.GetString(buf, 0, len < 0 ? buf.Length : len);
                }
            }
            catch { }
            return "status " + status;
        }

        private static void Check(int status, string what)
        {
            if (status != NVAPI_OK)
                throw new NvapiException(string.Format("{0} failed: {1} ({2})", what, ErrorMessage(status), status));
        }
    }

    internal class NvapiException : Exception
    {
        public NvapiException(string message) : base(message) { }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TwinEars
{
    // Minimal Windows Core Audio (WASAPI) interop.
    // Only shared-mode streams are ever opened, so nothing in here can change drivers, device
    // properties, enhancements (Dolby, spatial sound, EQ) or which device is the Windows default.

    internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;

        public PropertyKey(Guid formatId, int propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public IntPtr value;
        public IntPtr value2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(uint access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        void GetBuffer(uint frames, out IntPtr data);
        void ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("F6E4C0A0-46D9-4FB8-BE21-57A3EF2B626C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClockAdjustment
    {
        void SetSampleRate(float sampleRate);
    }

    internal static class Native
    {
        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant value);

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(uint processId);
    }

    internal sealed class AudioDevice
    {
        public readonly string Id;
        public string Name;

        public AudioDevice(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }

    internal enum SampleKind { Unsupported, Float32, Int16, Int24, Int32 }

    internal sealed class WaveFormatInfo
    {
        public int Rate;
        public int Channels;
        public int BitsPerSample;
        public int BlockAlign;
        public uint ChannelMask;
        public SampleKind Kind;

        const int WaveFormatPcm = 1;
        const int WaveFormatFloat = 3;
        const int WaveFormatExtensible = 0xFFFE;
        static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00AA00389B71");
        static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00AA00389B71");

        public static WaveFormatInfo FromPointer(IntPtr p)
        {
            var f = new WaveFormatInfo();
            int tag = (ushort)Marshal.ReadInt16(p, 0);
            f.Channels = (ushort)Marshal.ReadInt16(p, 2);
            f.Rate = Marshal.ReadInt32(p, 4);
            f.BlockAlign = (ushort)Marshal.ReadInt16(p, 12);
            f.BitsPerSample = (ushort)Marshal.ReadInt16(p, 14);
            if (tag == WaveFormatExtensible && (ushort)Marshal.ReadInt16(p, 16) >= 22)
            {
                f.ChannelMask = (uint)Marshal.ReadInt32(p, 20);
                byte[] guid = new byte[16];
                Marshal.Copy(IntPtr.Add(p, 24), guid, 0, 16);
                Guid subtype = new Guid(guid);
                tag = subtype == SubtypeFloat ? WaveFormatFloat : subtype == SubtypePcm ? WaveFormatPcm : 0;
            }
            int container = f.Channels > 0 ? f.BlockAlign / f.Channels : 0;
            if (tag == WaveFormatFloat && container == 4) f.Kind = SampleKind.Float32;
            else if (tag == WaveFormatPcm && container == 2) f.Kind = SampleKind.Int16;
            else if (tag == WaveFormatPcm && container == 3) f.Kind = SampleKind.Int24;
            else if (tag == WaveFormatPcm && container == 4) f.Kind = SampleKind.Int32;
            if (f.ChannelMask == 0) f.ChannelMask = DefaultChannelMask(f.Channels);
            return f;
        }

        // A WAVEFORMATEXTENSIBLE for 32-bit float audio. Free with Marshal.FreeCoTaskMem.
        public static IntPtr AllocFloat(int rate, int channels, uint channelMask)
        {
            IntPtr p = Marshal.AllocCoTaskMem(40);
            Marshal.WriteInt16(p, 0, unchecked((short)WaveFormatExtensible));
            Marshal.WriteInt16(p, 2, (short)channels);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * channels * 4);
            Marshal.WriteInt16(p, 12, (short)(channels * 4));
            Marshal.WriteInt16(p, 14, 32);
            Marshal.WriteInt16(p, 16, 22);
            Marshal.WriteInt16(p, 18, 32);
            Marshal.WriteInt32(p, 20, unchecked((int)channelMask));
            Marshal.Copy(SubtypeFloat.ToByteArray(), 0, IntPtr.Add(p, 24), 16);
            return p;
        }

        static uint DefaultChannelMask(int channels)
        {
            switch (channels)
            {
                case 1: return 0x4;
                case 2: return 0x3;
                case 3: return 0x7;
                case 4: return 0x33;
                case 5: return 0x37;
                case 6: return 0x3F;
                case 8: return 0x63F;
                default: return 0;
            }
        }

        public override string ToString()
        {
            string kind = Kind == SampleKind.Float32 ? "float" : Kind == SampleKind.Unsupported ? "unsupported" : BitsPerSample + "-bit";
            return Rate + " Hz, " + Channels + " ch, " + kind;
        }
    }

    internal static class Wasapi
    {
        public const int ShareModeShared = 0;
        public const uint ClsCtxAll = 23;
        public const uint DeviceStateActive = 1;

        public const uint StreamFlagsLoopback = 0x00020000;
        public const uint StreamFlagsEventCallback = 0x00040000;
        public const uint StreamFlagsNoPersist = 0x00080000;
        public const uint StreamFlagsRateAdjust = 0x00100000;
        public const uint StreamFlagsSrcDefaultQuality = 0x08000000;
        public const uint StreamFlagsAutoConvertPcm = 0x80000000;

        public const uint BufferFlagsSilent = 0x2;

        public static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        public static readonly Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static readonly Guid IID_IAudioClockAdjustment = new Guid("F6E4C0A0-46D9-4FB8-BE21-57A3EF2B626C");
        static readonly PropertyKey FriendlyNameKey = new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
        // "Allow applications to take exclusive control of this device" (Sound control panel > Advanced).
        static readonly PropertyKey ExclusiveAllowedKey = new PropertyKey(new Guid("B3F8FA53-0004-438E-9003-51A46E139BFC"), 3);

        public static IMMDeviceEnumerator CreateEnumerator()
        {
            return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        }

        public static IAudioClient Activate(IMMDevice device)
        {
            Guid iid = IID_IAudioClient;
            object client;
            device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out client);
            return (IAudioClient)client;
        }

        public static object GetService(IAudioClient client, Guid iid)
        {
            object service;
            client.GetService(ref iid, out service);
            return service;
        }

        public static void Release(object comObject)
        {
            if (comObject == null || !Marshal.IsComObject(comObject)) return;
            try { Marshal.ReleaseComObject(comObject); }
            catch (Exception) { }
        }

        public static List<AudioDevice> GetActiveOutputs()
        {
            var result = new List<AudioDevice>();
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            try
            {
                enumerator = CreateEnumerator();
                enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out collection);
                uint count;
                collection.GetCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        collection.Item(i, out device);
                        string id;
                        device.GetId(out id);
                        result.Add(new AudioDevice(id, GetFriendlyName(device) ?? "Unknown device"));
                    }
                    catch (Exception) { }
                    finally { Release(device); }
                }
            }
            finally
            {
                Release(collection);
                Release(enumerator);
            }

            // Two identical earbuds report identical names; number them so they can be told apart.
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (AudioDevice d in result)
            {
                int n;
                seen.TryGetValue(d.Name, out n);
                seen[d.Name] = n + 1;
                if (n > 0) d.Name += " #" + (n + 1);
            }
            return result;
        }

        public static string GetDefaultOutputId()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            try
            {
                enumerator = CreateEnumerator();
                enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device);
                string id;
                device.GetId(out id);
                return id;
            }
            catch (Exception)
            {
                return null;   // no output device at all
            }
            finally
            {
                Release(device);
                Release(enumerator);
            }
        }

        public static WaveFormatInfo GetMixFormat(IMMDevice device)
        {
            IAudioClient client = null;
            IntPtr format = IntPtr.Zero;
            try
            {
                client = Activate(device);
                client.GetMixFormat(out format);
                return WaveFormatInfo.FromPointer(format);
            }
            finally
            {
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Release(client);
            }
        }

        public static string DescribeMixFormat(string deviceId)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            try
            {
                enumerator = CreateEnumerator();
                enumerator.GetDevice(deviceId, out device);
                return GetMixFormat(device).ToString();
            }
            catch (Exception ex)
            {
                return "format unavailable: " + Describe(ex);
            }
            finally
            {
                Release(device);
                Release(enumerator);
            }
        }

        // True if Windows refuses ordinary (shared) audio on the device: another app holds it in exclusive
        // mode, or the device/driver itself is stuck. Then Windows has nothing to share from it.
        // Opens a shared stream without starting it, so nothing is played or changed.
        public static bool IsLockedExclusively(string deviceId)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IntPtr format = IntPtr.Zero;
            try
            {
                enumerator = CreateEnumerator();
                enumerator.GetDevice(deviceId, out device);
                client = Activate(device);
                client.GetMixFormat(out format);
                client.Initialize(ShareModeShared, StreamFlagsNoPersist, 1000000, 0, format, IntPtr.Zero);
                return false;
            }
            catch (Exception ex)
            {
                return unchecked((uint)ex.HResult) == 0x8889000A;   // AUDCLNT_E_DEVICE_IN_USE
            }
            finally
            {
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Release(client);
                Release(device);
                Release(enumerator);
            }
        }

        // Reads the device's "Allow applications to take exclusive control" checkbox.
        // 1 = allowed, 0 = not allowed, -1 = unknown. Read-only.
        public static int GetExclusiveModeAllowed(string deviceId)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IPropertyStore store = null;
            try
            {
                enumerator = CreateEnumerator();
                enumerator.GetDevice(deviceId, out device);
                device.OpenPropertyStore(0, out store);
                PropertyKey key = ExclusiveAllowedKey;
                PropVariant value;
                store.GetValue(ref key, out value);
                try
                {
                    const ushort VT_UI4 = 19;
                    if (value.vt != VT_UI4) return -1;
                    return (value.value.ToInt64() & 0xFFFFFFFF) != 0 ? 1 : 0;
                }
                finally
                {
                    Native.PropVariantClear(ref value);
                }
            }
            catch (Exception)
            {
                return -1;
            }
            finally
            {
                Release(store);
                Release(device);
                Release(enumerator);
            }
        }

        static string GetFriendlyName(IMMDevice device)
        {
            IPropertyStore store = null;
            try
            {
                device.OpenPropertyStore(0, out store);
                PropertyKey key = FriendlyNameKey;
                PropVariant value;
                store.GetValue(ref key, out value);
                try
                {
                    const ushort VT_LPWSTR = 31;
                    return value.vt == VT_LPWSTR && value.value != IntPtr.Zero ? Marshal.PtrToStringUni(value.value) : null;
                }
                finally
                {
                    Native.PropVariantClear(ref value);
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                Release(store);
            }
        }

        // Plain-English reasons for the errors people actually run into.
        public static string Describe(Exception ex)
        {
            switch (unchecked((uint)ex.HResult))
            {
                case 0x88890004: return "the device was disconnected or reconfigured";
                case 0x88890026: return "the device was reconfigured";
                case 0x8889000A: return "Windows reports this device as busy (exclusive mode, or the device/driver is stuck)";
                case 0x88890010: return "the Windows Audio service is not running";
                case 0x88890008: return "the device doesn't accept this audio format";
                case 0x88890017: return "the computer is too busy";
                case 0x80070490: return "device not found (disconnected?)";
            }
            if (ex is TimeoutException || ex is NotSupportedException) return ex.Message;
            return ex.Message + " (0x" + ex.HResult.ToString("X8") + ")";
        }
    }
}

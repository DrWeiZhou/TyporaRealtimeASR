using System.Runtime.InteropServices;
using System.Text;

namespace TyporaAsr;

/// <summary>
/// Minimal P/Invoke surface for macOS system frameworks (no Xamarin/macOS workload required).
/// Only compiled into the macOS build (TYPORA_MAC).
/// </summary>
internal static unsafe partial class MacNative
{
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    public const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    public const string Security = "/System/Library/Frameworks/Security.framework/Security";
    public const string ObjC = "/usr/lib/libobjc.A.dylib";
    public const string LibSystem = "/usr/lib/libSystem.dylib";

    public static uint FourCC(string code) =>
        ((uint)code[0] << 24) | ((uint)code[1] << 16) | ((uint)code[2] << 8) | code[3];

    /// <summary>Formats an OSStatus as its four-character code when printable, e.g. 'what' or -50.</summary>
    public static string Status(int status)
    {
        var b = new[] { (byte)(status >> 24), (byte)(status >> 16), (byte)(status >> 8), (byte)status };
        return b.All(x => x >= 32 && x < 127) ? $"'{Encoding.ASCII.GetString(b)}' ({status})" : status.ToString();
    }

    public static void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"{what} 失败，OSStatus {Status(status)}");
    }

    // ---------- CoreFoundation ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct CFRange { public nint Location; public nint Length; }

    [DllImport(CoreFoundation)] public static extern void CFRelease(IntPtr cf);
    [DllImport(CoreFoundation)] public static extern nint CFStringGetLength(IntPtr str);
    [DllImport(CoreFoundation)] public static extern void CFStringGetCharacters(IntPtr str, CFRange range, char* buffer);

    public static string? CFString(IntPtr str, bool release)
    {
        if (str == IntPtr.Zero) return null;
        try
        {
            var length = (int)CFStringGetLength(str);
            var chars = new char[length];
            fixed (char* p = chars) CFStringGetCharacters(str, new CFRange { Location = 0, Length = length }, p);
            return new string(chars);
        }
        finally { if (release) CFRelease(str); }
    }

    // ---------- CoreAudio (HAL) ----------
    public const uint SystemObject = 1;
    public static readonly uint ScopeGlobal = FourCC("glob");
    public static readonly uint ScopeInput = FourCC("inpt");
    public const uint ElementMain = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioObjectPropertyAddress { public uint Selector; public uint Scope; public uint Element; }

    [DllImport(CoreAudio)] public static extern int AudioObjectGetPropertyDataSize(uint objectId, in AudioObjectPropertyAddress address, uint qualifierSize, IntPtr qualifier, out uint dataSize);
    [DllImport(CoreAudio)] public static extern int AudioObjectGetPropertyData(uint objectId, in AudioObjectPropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint dataSize, void* data);

    public static AudioObjectPropertyAddress Address(string selector, uint? scope = null) =>
        new() { Selector = FourCC(selector), Scope = scope ?? ScopeGlobal, Element = ElementMain };

    public static bool TryGetUInt(uint objectId, string selector, out uint value, uint? scope = null)
    {
        var address = Address(selector, scope);
        uint v = 0, size = sizeof(uint);
        var status = AudioObjectGetPropertyData(objectId, address, 0, IntPtr.Zero, ref size, &v);
        value = v;
        return status == 0;
    }

    public static string? GetString(uint objectId, string selector)
    {
        var address = Address(selector);
        IntPtr value = IntPtr.Zero;
        uint size = (uint)IntPtr.Size;
        return AudioObjectGetPropertyData(objectId, address, 0, IntPtr.Zero, ref size, &value) == 0 ? CFString(value, true) : null;
    }

    /// <summary>Returns a retained CFStringRef (caller releases) for the given string-valued property.</summary>
    public static IntPtr GetCFString(uint objectId, string selector)
    {
        var address = Address(selector);
        IntPtr value = IntPtr.Zero;
        uint size = (uint)IntPtr.Size;
        Check(AudioObjectGetPropertyData(objectId, address, 0, IntPtr.Zero, ref size, &value), "读取音频设备属性");
        return value;
    }

    public static uint[] GetDeviceIds()
    {
        var address = Address("dev#");
        Check(AudioObjectGetPropertyDataSize(SystemObject, address, 0, IntPtr.Zero, out var size), "枚举音频设备");
        var ids = new uint[size / sizeof(uint)];
        if (ids.Length == 0) return ids;
        fixed (uint* p = ids) Check(AudioObjectGetPropertyData(SystemObject, address, 0, IntPtr.Zero, ref size, p), "枚举音频设备");
        return ids.Take((int)(size / sizeof(uint))).ToArray();
    }

    public static int InputChannelCount(uint deviceId)
    {
        var address = Address("slay", ScopeInput);
        if (AudioObjectGetPropertyDataSize(deviceId, address, 0, IntPtr.Zero, out var size) != 0 || size < sizeof(uint)) return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, address, 0, IntPtr.Zero, ref size, (void*)buffer) != 0) return 0;
            // AudioBufferList { UInt32 mNumberBuffers; AudioBuffer mBuffers[]; } AudioBuffer { UInt32 channels; UInt32 bytes; void* data; }
            var count = Marshal.ReadInt32(buffer);
            var offset = IntPtr.Size == 8 ? 8 : 4;
            var channels = 0;
            for (var i = 0; i < count; i++) channels += Marshal.ReadInt32(buffer, offset + i * (8 + IntPtr.Size));
            return channels;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ---------- AudioToolbox (Audio Queue Services) ----------
    public const uint FormatLinearPcm = 0x6C70636D; // 'lpcm'
    public const uint FlagSignedInteger = 1u << 2, FlagPacked = 1u << 3;
    public static readonly uint QueuePropertyCurrentDevice = FourCC("aqcd");
    public static readonly uint QueuePropertyIsRunning = FourCC("aqrn");

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [DllImport(AudioToolbox)]
    public static extern int AudioQueueNewInput(in AudioStreamBasicDescription format,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, AudioQueueBuffer*, IntPtr, uint, IntPtr, void> callback,
        IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);
    [DllImport(AudioToolbox)] public static extern int AudioQueueAllocateBuffer(IntPtr queue, uint byteSize, out AudioQueueBuffer* buffer);
    [DllImport(AudioToolbox)] public static extern int AudioQueueEnqueueBuffer(IntPtr queue, AudioQueueBuffer* buffer, uint packetDescriptions, IntPtr descriptions);
    [DllImport(AudioToolbox)] public static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(AudioToolbox)] public static extern int AudioQueueFlush(IntPtr queue);
    [DllImport(AudioToolbox)] public static extern int AudioQueueStop(IntPtr queue, byte immediate);
    [DllImport(AudioToolbox)] public static extern int AudioQueueDispose(IntPtr queue, byte immediate);
    [DllImport(AudioToolbox)] public static extern int AudioQueueSetProperty(IntPtr queue, uint propertyId, void* data, uint size);
    [DllImport(AudioToolbox)]
    public static extern int AudioQueueAddPropertyListener(IntPtr queue, uint propertyId,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void> listener, IntPtr userData);
    [DllImport(AudioToolbox)] public static extern int AudioQueueGetProperty(IntPtr queue, uint propertyId, void* data, ref uint size);

    // ---------- Security (legacy keychain API: simple C strings, available on every supported macOS) ----------
    public const int ErrSecItemNotFound = -25300, ErrSecDuplicateItem = -25299;

    [DllImport(Security)]
    public static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, IntPtr itemRef);
    [DllImport(Security)]
    public static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, IntPtr itemRef);
    [DllImport(Security)] public static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    // ---------- Objective-C runtime (microphone authorization) ----------
    [DllImport(LibSystem)] public static extern IntPtr dlopen(string path, int mode);
    [DllImport(LibSystem)] public static extern IntPtr dlsym(IntPtr handle, string symbol);
    [DllImport(ObjC)] public static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] public static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern nint MsgSendNInt(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern void MsgSendBlock(IntPtr receiver, IntPtr selector, IntPtr arg, void* block);
}

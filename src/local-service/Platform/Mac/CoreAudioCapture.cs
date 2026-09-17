using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static TyporaAsr.MacNative;

namespace TyporaAsr;

/// <summary>
/// macOS microphone capture through Audio Queue Services. The queue itself converts the device format to
/// 16 kHz mono PCM16, so no extra resampling step is needed (Flush returns nothing).
/// Device ids are Core Audio AudioDeviceIDs; -1 is the system default input.
/// System audio: macOS has no built-in loopback device; virtual input devices such as BlackHole / Loopback
/// are listed under "系统声音" (kind "system") and recorded like a microphone.
/// </summary>
public sealed unsafe class CoreAudioCapture : IAudioCapture
{
    public const int DefaultInputDeviceId = -1;
    private const int SampleRate = 16000;
    private const uint BufferBytes = SampleRate / 10 * 2; // 100 ms
    private const int BufferCount = 3;
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly object callbackGate = new();
    private IntPtr queue;
    private GCHandle self;
    private TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool recording;
    private int stopRequested;
    private Exception? dataError;
    private Exception? stopError;
    private long lastCallback;
    private Timer? watchdog;

    public event Action<short[]>? PcmAvailable;
    public event Action<Exception?>? Stopped;
    public bool IsRecording => recording;

    public void Start(int device)
    {
        lock (gate)
        {
            if (queue != IntPtr.Zero) throw new InvalidOperationException("Capture already started");
            uint? deviceId = device == DefaultInputDeviceId ? null : device >= 0 ? (uint)device : throw new ArgumentException("录音设备无效");
            if (deviceId is uint id && !CoreAudioCaptureFactory.ListInputDevices().Any(d => d.Id == id))
                throw new ArgumentException("录音设备不存在或已断开，请刷新设备列表");
            MicrophoneAccess.EnsureAuthorized();

            dataError = null; stopError = null; stopRequested = 0;
            stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            self = GCHandle.Alloc(this);
            try
            {
                var format = new AudioStreamBasicDescription
                {
                    SampleRate = SampleRate, FormatId = FormatLinearPcm, FormatFlags = FlagSignedInteger | FlagPacked,
                    BytesPerPacket = 2, FramesPerPacket = 1, BytesPerFrame = 2, ChannelsPerFrame = 1, BitsPerChannel = 16,
                };
                Check(AudioQueueNewInput(format, &OnInput, GCHandle.ToIntPtr(self), IntPtr.Zero, IntPtr.Zero, 0, out queue), "创建录音队列");
                if (deviceId is uint chosen)
                {
                    var uid = GetCFString(chosen, "uid ");
                    try { Check(AudioQueueSetProperty(queue, QueuePropertyCurrentDevice, &uid, (uint)IntPtr.Size), "选择录音设备"); }
                    finally { CFRelease(uid); }
                }
                AudioQueueAddPropertyListener(queue, QueuePropertyIsRunning, &OnRunningChanged, GCHandle.ToIntPtr(self));
                for (var i = 0; i < BufferCount; i++)
                {
                    Check(AudioQueueAllocateBuffer(queue, BufferBytes, out var buffer), "分配录音缓冲区");
                    Check(AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero), "准备录音缓冲区");
                }
                recording = true;
                Interlocked.Exchange(ref lastCallback, Environment.TickCount64);
                Check(AudioQueueStart(queue, IntPtr.Zero), "启动麦克风（请检查“系统设置 → 隐私与安全性 → 麦克风”）");
                watchdog = new Timer(_ => CheckAlive(), null, Watchdog, Watchdog);
            }
            catch
            {
                recording = false;
                ReleaseQueue();
                throw;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnInput(IntPtr user, IntPtr aq, AudioQueueBuffer* buffer, IntPtr startTime, uint packets, IntPtr descriptions)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is CoreAudioCapture capture) capture.Receive(aq, buffer);
        }
        catch { /* never let an exception cross into Core Audio */ }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnRunningChanged(IntPtr user, IntPtr aq, uint property)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not CoreAudioCapture capture) return;
            uint running = 0, size = sizeof(uint);
            if (AudioQueueGetProperty(aq, QueuePropertyIsRunning, &running, ref size) == 0 && running == 0 && capture.recording && capture.stopRequested == 0)
                capture.BeginStop(new InvalidOperationException("录音设备已停止（设备可能被断开或被系统占用）"));
        }
        catch { }
    }

    private void Receive(IntPtr aq, AudioQueueBuffer* buffer)
    {
        Interlocked.Exchange(ref lastCallback, Environment.TickCount64);
        lock (callbackGate)
        {
            var bytes = (int)buffer->AudioDataByteSize;
            if (bytes >= 2 && dataError == null)
            {
                var pcm = new short[bytes / 2];
                fixed (short* target = pcm) Buffer.MemoryCopy((void*)buffer->AudioData, target, pcm.Length * 2L, pcm.Length * 2L);
                try { PcmAvailable?.Invoke(pcm); }
                catch (Exception error) { dataError = error; BeginStop(null); }
            }
            buffer->AudioDataByteSize = 0;
            if (recording && stopRequested == 0) AudioQueueEnqueueBuffer(aq, buffer, 0, IntPtr.Zero);
        }
    }

    private void CheckAlive()
    {
        if (recording && stopRequested == 0 && Environment.TickCount64 - Interlocked.Read(ref lastCallback) > Watchdog.TotalMilliseconds)
            BeginStop(new InvalidOperationException("超过 5 秒没有收到麦克风数据，请检查设备或麦克风权限"));
    }

    public void RequestStop() => BeginStop(null);

    // Stopping runs off the caller's thread: RequestStop may be called from inside a PcmAvailable handler.
    private void BeginStop(Exception? error)
    {
        if (Interlocked.Exchange(ref stopRequested, 1) != 0) return;
        stopError = error;
        _ = Task.Run(() =>
        {
            Exception? failure = stopError;
            try
            {
                IntPtr q;
                lock (gate) q = queue;
                if (q != IntPtr.Zero)
                {
                    AudioQueueFlush(q);
                    var status = AudioQueueStop(q, 1); // synchronous: no more input callbacks after this returns
                    if (status != 0 && failure == null) failure = new InvalidOperationException("停止录音失败，OSStatus " + MacNative.Status(status));
                }
                lock (callbackGate) { } // wait for an in-flight callback
            }
            catch (Exception e) { failure ??= e; }
            finally
            {
                recording = false;
                watchdog?.Dispose(); watchdog = null;
                try { Stopped?.Invoke(failure); }
                finally { stopped.TrySetResult(); }
            }
        });
    }

    public short[] Flush() => [];

    public Exception? TakeDataError()
    {
        lock (callbackGate) { var e = dataError; dataError = null; return e; }
    }

    public Task WaitStoppedAsync(TimeSpan timeout) => stopped.Task.WaitAsync(timeout);

    private void ReleaseQueue()
    {
        watchdog?.Dispose(); watchdog = null;
        if (queue != IntPtr.Zero) { AudioQueueDispose(queue, 1); queue = IntPtr.Zero; }
        if (self.IsAllocated) self.Free();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) != 0)
        {
            // A stop is in progress on the pool: let it finish before the queue is disposed.
            try { stopped.Task.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }
        lock (gate)
        {
            if (queue != IntPtr.Zero && recording) AudioQueueStop(queue, 1);
            recording = false;
            lock (callbackGate) ReleaseQueue();
        }
    }
}

public sealed class CoreAudioCaptureFactory : IAudioCaptureFactory
{
    private static readonly string[] LoopbackNames = ["blackhole", "loopback", "soundflower", "ishowu", "background music", "zoomaudiodevice", "teams audio", "腾讯会议"];

    public IAudioCapture Create() => new CoreAudioCapture();

    internal static List<AudioDeviceInfo> ListInputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        foreach (var id in MacNative.GetDeviceIds())
        {
            if (id > int.MaxValue || MacNative.InputChannelCount(id) <= 0) continue;
            var name = MacNative.GetString(id, "lnam") ?? $"音频设备 {id}";
            MacNative.TryGetUInt(id, "tran", out var transport);
            var virtualDevice = transport == MacNative.FourCC("virt");
            var lower = name.ToLowerInvariant();
            var system = virtualDevice || LoopbackNames.Any(lower.Contains);
            list.Add(new AudioDeviceInfo((int)id, system ? "系统声音 · " + name : name, system ? "system" : "microphone"));
        }
        return list;
    }

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        var list = new List<AudioDeviceInfo> { new(CoreAudioCapture.DefaultInputDeviceId, "系统默认麦克风", "microphone") };
        try { list.AddRange(ListInputDevices().OrderBy(d => d.Kind == "system")); }
        catch { /* keep the default entry usable even if enumeration fails */ }
        return list;
    }
}

/// <summary>AVCaptureDevice microphone authorization (TCC). The service runs inside its own .app bundle so the prompt names it.</summary>
public static unsafe class MicrophoneAccess
{
    private const int NotDetermined = 0, Restricted = 1, Denied = 2, Authorized = 3;
    private static IntPtr avFoundation, mediaTypeAudio, deviceClass;
    private static readonly object gate = new();
    private static TaskCompletionSource<bool>? pending;

    private static bool Load()
    {
        if (deviceClass != IntPtr.Zero) return true;
        avFoundation = MacNative.dlopen("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", 2);
        if (avFoundation == IntPtr.Zero) return false;
        var symbol = MacNative.dlsym(avFoundation, "AVMediaTypeAudio");
        if (symbol == IntPtr.Zero) return false;
        mediaTypeAudio = Marshal.ReadIntPtr(symbol);
        deviceClass = MacNative.objc_getClass("AVCaptureDevice");
        return deviceClass != IntPtr.Zero && mediaTypeAudio != IntPtr.Zero;
    }

    public static int Status()
    {
        lock (gate)
        {
            if (!Load()) return -1;
            return (int)MacNative.MsgSendNInt(deviceClass, MacNative.sel_registerName("authorizationStatusForMediaType:"), mediaTypeAudio);
        }
    }

    public static void EnsureAuthorized()
    {
        int status;
        try { status = Status(); } catch { return; } // unknown: let Core Audio decide
        if (status == Authorized || status < 0) return;
        if (status is Denied or Restricted)
            throw new InvalidOperationException("没有麦克风权限：请在“系统设置 → 隐私与安全性 → 麦克风”中允许 TyporaASR Service，然后重新开始录音");
        if (status == NotDetermined && Environment.GetEnvironmentVariable("TYPORA_ASR_SKIP_MIC_REQUEST") != "1")
        {
            bool granted;
            try { var answer = Request(); granted = answer.Wait(TimeSpan.FromSeconds(180)) && answer.Result; }
            catch { return; }
            if (!granted) throw new InvalidOperationException("未获得麦克风权限：请在系统弹窗中允许，或到“系统设置 → 隐私与安全性 → 麦克风”中开启 TyporaASR Service");
        }
    }

    // Objective-C global block: { isa, flags, reserved, invoke, descriptor }.
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor { public nuint Reserved; public nuint Size; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral { public IntPtr Isa; public int Flags; public int Reserved; public IntPtr Invoke; public BlockDescriptor* Descriptor; }

    private static BlockLiteral* block;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnAnswer(BlockLiteral* self, byte granted)
    {
        TaskCompletionSource<bool>? tcs;
        lock (gate) tcs = pending;
        tcs?.TrySetResult(granted != 0);
    }

    private static Task<bool> Request()
    {
        lock (gate)
        {
            if (pending != null) return pending.Task;
            pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (block == null)
            {
                var libSystem = MacNative.dlopen("/usr/lib/libSystem.dylib", 2);
                var globalBlockIsa = MacNative.dlsym(libSystem, "_NSConcreteGlobalBlock");
                var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
                descriptor->Size = (nuint)sizeof(BlockLiteral);
                block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
                block->Isa = globalBlockIsa;
                block->Flags = 1 << 28; // BLOCK_IS_GLOBAL
                block->Invoke = (IntPtr)(delegate* unmanaged[Cdecl]<BlockLiteral*, byte, void>)&OnAnswer;
                block->Descriptor = descriptor;
            }
            MacNative.MsgSendBlock(deviceClass, MacNative.sel_registerName("requestAccessForMediaType:completionHandler:"), mediaTypeAudio, block);
            return pending.Task;
        }
    }
}

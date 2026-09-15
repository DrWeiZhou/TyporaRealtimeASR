using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace TyporaAsr;

/// <summary>
/// Windows WASAPI/NAudio capture that emits 16 kHz mono PCM (via <see cref="PcmConverter"/>).
/// Supports microphone (capture) and system-audio loopback (what you hear).
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture
{
    public const int DefaultCaptureDeviceId = -1;
    public const int DefaultLoopbackDeviceId = -2;
    public const int LoopbackDeviceIdBase = 10_000;

    private readonly object gate = new();
    private WasapiCapture? capture;
    private MMDevice? endpoint;
    private PcmConverter? converter;
    private TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool recording;
    private Exception? dataError;

    public event Action<short[]>? PcmAvailable;
    public event Action<Exception?>? Stopped;
    public bool IsRecording => recording;

    public static bool IsLoopbackDevice(int device) =>
        device == DefaultLoopbackDeviceId || device >= LoopbackDeviceIdBase;

    public static IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = new List<AudioDeviceInfo>
        {
            new(DefaultCaptureDeviceId, "系统默认麦克风", "microphone")
        };

        var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        for (var i = 0; i < captureDevices.Count; i++)
        {
            using var device = captureDevices[i];
            list.Add(new AudioDeviceInfo(i, device.FriendlyName, "microphone"));
        }

        list.Add(new(DefaultLoopbackDeviceId, "系统声音（默认播放设备）", "system"));

        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var i = 0; i < renderDevices.Count; i++)
        {
            using var device = renderDevices[i];
            list.Add(new AudioDeviceInfo(LoopbackDeviceIdBase + i, "系统声音 · " + device.FriendlyName, "system"));
        }

        return list;
    }

    public void Start(int device)
    {
        lock (gate)
        {
            if (capture != null) throw new InvalidOperationException("Capture already started");
            dataError = null;
            stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var loopback = IsLoopbackDevice(device);
                if (loopback)
                {
                    endpoint = device == DefaultLoopbackDeviceId
                        ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                        : enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)[device - LoopbackDeviceIdBase];
                    capture = new WasapiLoopbackCapture(endpoint);
                }
                else
                {
                    endpoint = device < 0
                        ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                        : enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)[device];
                    capture = new WasapiCapture(endpoint);
                }

                converter = new PcmConverter(capture.WaveFormat);
                capture.DataAvailable += (_, e) =>
                {
                    try
                    {
                        var pcm = converter.Push(e.Buffer, 0, e.BytesRecorded);
                        PcmAvailable?.Invoke(pcm);
                    }
                    catch (Exception error)
                    {
                        dataError = error;
                        capture?.StopRecording();
                    }
                };
                capture.RecordingStopped += (_, e) =>
                {
                    recording = false;
                    try { Stopped?.Invoke(e.Exception); }
                    finally { stopped.TrySetResult(); }
                };
                recording = true;
                capture.StartRecording();
            }
            catch
            {
                recording = false;
                capture?.Dispose();
                capture = null;
                endpoint?.Dispose();
                endpoint = null;
                converter = null;
                throw;
            }
        }
    }

    public void RequestStop()
    {
        WasapiCapture? device;
        lock (gate) { device = capture; }
        device?.StopRecording();
    }

    public short[] Flush()
    {
        lock (gate) { return converter?.Flush() ?? []; }
    }

    /// <summary>PCM conversion / callback failure while capturing.</summary>
    public Exception? TakeDataError()
    {
        lock (gate) { var e = dataError; dataError = null; return e; }
    }

    public Task WaitStoppedAsync(TimeSpan timeout) => stopped.Task.WaitAsync(timeout);

    public void Dispose()
    {
        lock (gate)
        {
            capture?.Dispose();
            capture = null;
            endpoint?.Dispose();
            endpoint = null;
            converter = null;
        }
    }
}

public sealed class WasapiAudioCaptureFactory : IAudioCaptureFactory
{
    public IAudioCapture Create() => new WasapiAudioCapture();
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() => WasapiAudioCapture.EnumerateDevices();
}

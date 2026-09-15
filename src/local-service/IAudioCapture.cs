namespace TyporaAsr;

public readonly record struct AudioDeviceInfo(int Id, string Name, string Kind = "microphone");

/// <summary>
/// Cross-platform audio capture abstraction (Phase 0).
/// Windows: <see cref="WasapiAudioCapture"/> (microphone + system loopback). Mac/Linux: not implemented yet.
/// </summary>
public interface IAudioCapture : IDisposable
{
    event Action<short[]>? PcmAvailable;
    event Action<Exception?>? Stopped;
    bool IsRecording { get; }
    void Start(int device);
    void RequestStop();
    /// <summary>Flush resampler tail after capture has stopped (call from Stopped handler).</summary>
    short[] Flush();
    Exception? TakeDataError();
    Task WaitStoppedAsync(TimeSpan timeout);
}

public interface IAudioCaptureFactory
{
    IAudioCapture Create();
    IReadOnlyList<AudioDeviceInfo> EnumerateDevices();
}

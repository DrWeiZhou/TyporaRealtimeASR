namespace TyporaAsr;

/// <summary>
/// Chooses the platform implementations at compile time (TYPORA_WINDOWS / TYPORA_MAC, see the csproj).
/// Windows: WASAPI capture + DPAPI. macOS: Core Audio (AudioQueue) capture + Keychain-backed AES-GCM.
/// </summary>
public static class PlatformServices
{
    public static string Name =>
#if TYPORA_MAC
        "macos";
#else
        "windows";
#endif

    private static readonly Lazy<ISecretProtector> protector = new(() =>
#if TYPORA_MAC
        new KeychainSecretProtector()
#else
        new DpapiSecretProtector()
#endif
    );

    /// <summary>Current-user secret protector (created lazily so tests that pass their own protector never touch the OS store).</summary>
    public static ISecretProtector SecretProtector => protector.Value;

    public static IAudioCaptureFactory CreateAudioCaptureFactory() =>
#if TYPORA_MAC
        new CoreAudioCaptureFactory();
#else
        new WasapiAudioCaptureFactory();
#endif
}

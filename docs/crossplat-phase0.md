# Phase 0 cross-platform abstractions

## TargetFramework decision

Kept `net10.0-windows` for this phase.

NAudio (WASAPI), `PcmConverter`, and DPAPI P/Invoke still require Windows. Isolating them behind `IAudioCapture` / `ISecretProtector` is enough for Phase 0 without breaking the working Windows build. Moving to bare `net10.0` (with conditional Windows packages) is deferred until a Mac audio/Keychain implementation exists.

## Residual Windows-only bits

- `Platform/Windows/WasapiAudioCapture.cs` (NAudio WASAPI)
- `PcmConverter.cs` (NAudio resampling)
- `Platform/Windows/DpapiSecretProtector.cs` (crypt32 DPAPI)
- PackageReference `NAudio`
- TFM `net10.0-windows`

## Suggested next Mac step

1. Multi-target or switch TFM to `net10.0` with Windows-conditioned NAudio.

2. Implement `IAudioCapture` with AVAudioEngine delivering 16 kHz mono PCM.

3. Implement `ISecretProtector` with Keychain.

4. Wire factory selection by RID/`OperatingSystem.IsMacOS()`.

5. Keep plugin ports/protocols unchanged.

# macOS platform

Compiled only when `TyporaAsrPlatform=mac` (building on a Mac, or `-r osx-arm64` / `-r osx-x64`). See `docs/macos.md`.

- `CoreAudioCapture.cs` — Audio Queue Services input, 16 kHz mono PCM16 straight from the queue; Core Audio device
  enumeration (virtual devices such as BlackHole are listed as "系统声音"); AVCaptureDevice microphone authorization.
- `KeychainSecretProtector.cs` — AES-256-GCM, master key stored in the login Keychain.
- `MacNative.cs` — P/Invoke declarations (CoreFoundation, CoreAudio, AudioToolbox, Security, objc runtime).
- Linux: not implemented.

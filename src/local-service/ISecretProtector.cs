namespace TyporaAsr;

/// <summary>
/// Protects secrets at rest (Phase 0).
/// Windows: DpapiSecretProtector. macOS: KeychainSecretProtector. Linux: deferred.
/// </summary>
public interface ISecretProtector
{
    string Protect(string text);
    string Unprotect(string text);
}

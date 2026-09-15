namespace TyporaAsr;

/// <summary>
/// Protects secrets at rest (Phase 0).
/// Windows: <see cref="DpapiSecretProtector"/>. Mac Keychain / Linux: deferred.
/// </summary>
public interface ISecretProtector
{
    string Protect(string text);
    string Unprotect(string text);
}

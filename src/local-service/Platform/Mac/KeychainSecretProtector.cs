using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TyporaAsr;

/// <summary>
/// macOS secret protector: AES-256-GCM with a random per-user master key kept in the login Keychain
/// (generic password, service "TyporaRealtimeASR", account "settings-key"). The encrypted settings files stay
/// under .asr like on Windows; they cannot be decrypted on another Mac or user account (use 配置导出/导入 instead).
/// </summary>
public sealed class KeychainSecretProtector : ISecretProtector
{
    private const string Prefix = "kc1:";
    private const int NonceSize = 12, TagSize = 16;
    private readonly string service, account;
    private readonly Lazy<byte[]> key;

    public KeychainSecretProtector(string service = "TyporaRealtimeASR", string account = "settings-key")
    {
        this.service = service; this.account = account;
        key = new Lazy<byte[]>(LoadOrCreateKey, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Protect(string text)
    {
        var plain = Encoding.UTF8.GetBytes(text);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key.Value, TagSize)) aes.Encrypt(nonce, plain, cipher, tag);
        var packed = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipher.CopyTo(packed, NonceSize + TagSize);
        return Prefix + Convert.ToBase64String(packed);
    }

    public string Unprotect(string text)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("该配置不是在这台 Mac 上加密保存的（可能来自 Windows），请在面板重新填写，或使用“配置导入”");
        var data = Convert.FromBase64String(text[Prefix.Length..]);
        if (data.Length < NonceSize + TagSize) throw new InvalidOperationException("加密配置已损坏");
        var plain = new byte[data.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key.Value, TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("无法用当前 Mac 用户的钥匙串密钥解密配置，请在面板重新填写或使用“配置导入”");
        }
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] LoadOrCreateKey()
    {
        var existing = Find();
        if (existing != null) return existing;
        var created = RandomNumberGenerator.GetBytes(32);
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        var secret = Encoding.ASCII.GetBytes(Convert.ToBase64String(created));
        var status = MacNative.SecKeychainAddGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, (uint)accountBytes.Length, accountBytes, (uint)secret.Length, secret, IntPtr.Zero);
        if (status == MacNative.ErrSecDuplicateItem) return Find() ?? throw new InvalidOperationException("钥匙串中的 TyporaRealtimeASR 密钥无法读取");
        if (status != 0) throw new InvalidOperationException("无法写入 macOS 钥匙串（OSStatus " + MacNative.Status(status) + "），请确认登录钥匙串已解锁");
        return created;
    }

    private byte[]? Find()
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        var status = MacNative.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, (uint)accountBytes.Length, accountBytes, out var length, out var data, IntPtr.Zero);
        if (status == MacNative.ErrSecItemNotFound) return null;
        if (status != 0) throw new InvalidOperationException("无法读取 macOS 钥匙串（OSStatus " + MacNative.Status(status) + "）；如出现系统提示，请选择“始终允许”");
        try
        {
            var text = Marshal.PtrToStringAnsi(data, (int)length);
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length != 32) throw new InvalidOperationException("钥匙串中的 TyporaRealtimeASR 密钥格式无效");
            return bytes;
        }
        finally { MacNative.SecKeychainItemFreeContent(IntPtr.Zero, data); }
    }
}

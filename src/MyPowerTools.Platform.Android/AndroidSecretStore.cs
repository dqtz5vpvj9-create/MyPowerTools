using System.Text;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using MyPowerTools.Platform.Abstractions;
using A = global::Android;

namespace MyPowerTools.Platform.Android;

/// <summary>
/// Credential store backed by the Android Keystore. The secret value is encrypted with an
/// app-scoped AES/GCM key that never leaves the Keystore; only the IV and ciphertext are written
/// to the app-private preferences file. Reads and writes are serialised per process.
/// </summary>
public sealed class AndroidSecretStore : ISecretStore
{
    private const string PreferencesName = "mpt-secrets";
    private const string KeyAlias = "mypowertools-credentials";
    private const string KeyStoreProvider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int GcmTagBits = 128;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private static A.Content.ISharedPreferences Preferences =>
        A.App.Application.Context.GetSharedPreferences(PreferencesName, A.Content.FileCreationMode.Private)
        ?? throw new InvalidOperationException("Android 首选项不可用。");

    public async Task<SecretReference> SaveAsync(string moduleId, string name, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var reference = SecretReference.Create(moduleId, name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = Encrypt(secret);
            var editor = Preferences.Edit() ?? throw new IOException("无法保存凭据。");
            if (!editor.PutString(reference.Uri, payload)!.Commit())
            {
                throw new IOException("无法保存凭据。");
            }

            return reference;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.TryGetParts(out _, out _))
        {
            throw new ArgumentException("Invalid secret reference.", nameof(reference));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = Preferences.GetString(reference.Uri, null);
            return payload is null ? null : Decrypt(payload);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.TryGetParts(out _, out _))
        {
            throw new ArgumentException("Invalid secret reference.", nameof(reference));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var editor = Preferences.Edit() ?? throw new IOException("无法删除凭据。");
            if (!editor.Remove(reference.Uri)!.Commit())
            {
                throw new IOException("无法删除凭据。");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Encrypt(string secret)
    {
        using var cipher = Cipher.GetInstance(Transformation) ?? throw new InvalidOperationException("Android 加密服务不可用。");
        using var key = LoadOrCreateKey();
        cipher.Init(CipherMode.EncryptMode, key);
        var iv = cipher.GetIV() ?? throw new InvalidOperationException("Android 加密服务未生成随机向量。");
        var ciphertext = cipher.DoFinal(Encoding.UTF8.GetBytes(secret)) ?? throw new InvalidOperationException("Android 加密失败。");
        return Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(ciphertext);
    }

    private static string Decrypt(string payload)
    {
        var separator = payload.IndexOf(':');
        if (separator <= 0 || separator == payload.Length - 1)
        {
            throw new InvalidDataException("已保存的凭据格式无效，请重新保存。");
        }

        try
        {
            using var cipher = Cipher.GetInstance(Transformation) ?? throw new InvalidOperationException("Android 加密服务不可用。");
            using var key = LoadOrCreateKey();
            using var spec = new GCMParameterSpec(GcmTagBits, Convert.FromBase64String(payload[..separator]));
            cipher.Init(CipherMode.DecryptMode, key, spec);
            var plaintext = cipher.DoFinal(Convert.FromBase64String(payload[(separator + 1)..]))
                ?? throw new InvalidOperationException("Android 解密失败。");
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is FormatException or GeneralSecurityException)
        {
            throw new InvalidDataException("已保存的凭据无法解密（Android Keystore 密钥可能已失效），请重新保存。", ex);
        }
    }

    private static IKey LoadOrCreateKey()
    {
        using var store = KeyStore.GetInstance(KeyStoreProvider) ?? throw new InvalidOperationException("Android Keystore 不可用。");
        store.Load(null);
        if (!store.ContainsAlias(KeyAlias))
        {
            using var generator = KeyGenerator.GetInstance("AES", KeyStoreProvider) ?? throw new InvalidOperationException("Android Keystore 不可用。");
            using var spec = new A.Security.Keystore.KeyGenParameterSpec.Builder(KeyAlias,
                    A.Security.Keystore.KeyStorePurpose.Encrypt | A.Security.Keystore.KeyStorePurpose.Decrypt)
                .SetBlockModes("GCM")
                .SetEncryptionPaddings("NoPadding")
                .SetKeySize(256)
                .SetRandomizedEncryptionRequired(true)
                .Build();
            generator.Init(spec);
            generator.GenerateKey();
        }

        return store.GetKey(KeyAlias, null) ?? throw new InvalidOperationException("Android Keystore 中未找到凭据密钥。");
    }
}

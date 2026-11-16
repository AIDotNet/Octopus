using System.Text;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Octopus.Mobile.Remote;

namespace Octopus.Mobile.Android;

/// <summary>
/// The phone's secrets (its device key, its sign-in) as files in the app's own folder, each sealed with AES-256-GCM
/// under a key that lives in the Android Keystore: the key never leaves the keystore (on most phones a secure chip),
/// so a copy of the files is of no use without this phone. Ordinary files (the paired hosts) sit next to them.
/// </summary>
public sealed class KeystoreMobileStore : IMobileStore
{
    private const string Provider = "AndroidKeyStore";
    private const string Alias = "octopus.secrets";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int TagBits = 128;
    private readonly Lock _gate = new();

    public KeystoreMobileStore(string directory)
    {
        DataDirectory = directory;
        Directory.CreateDirectory(Path.Combine(directory, "secrets"));
    }

    public string DataDirectory { get; }

    private string PathOf(string name) => Path.Combine(DataDirectory, "secrets", name);

    public Task<string?> GetSecretAsync(string name) => Task.Run(() =>
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return null;
        var sealedBytes = File.ReadAllBytes(path);
        // 1 byte: the IV's length, then the IV, then the ciphertext with its tag.
        if (sealedBytes.Length < 2 || sealedBytes[0] == 0 || sealedBytes.Length <= 1 + sealedBytes[0]) return null;
        var iv = sealedBytes.AsSpan(1, sealedBytes[0]).ToArray();
        var cipherText = sealedBytes.AsSpan(1 + iv.Length).ToArray();
        try
        {
            lock (_gate)
            {
                var cipher = Cipher.GetInstance(Transformation)!;
                cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(TagBits, iv));
                return Encoding.UTF8.GetString(cipher.DoFinal(cipherText)!);
            }
        }
        // The keystore key is gone (the app's data was restored to another phone, or the lock screen was removed on
        // an older Android): the secret cannot be read; the app starts over as a new device.
        catch (Exception e) when (e is GeneralSecurityException or Java.Lang.IllegalStateException)
        {
            return null;
        }
    });

    public Task SetSecretAsync(string name, string? value) => Task.Run(() =>
    {
        var path = PathOf(name);
        if (value is null)
        {
            File.Delete(path);
            return;
        }
        byte[] iv, cipherText;
        lock (_gate)
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            // The keystore picks the IV: a key made with randomized encryption refuses one given by the caller.
            cipher.Init(CipherMode.EncryptMode, Key());
            iv = cipher.GetIV()!;
            cipherText = cipher.DoFinal(Encoding.UTF8.GetBytes(value))!;
        }
        var sealedBytes = new byte[1 + iv.Length + cipherText.Length];
        sealedBytes[0] = (byte)iv.Length;
        iv.CopyTo(sealedBytes, 1);
        cipherText.CopyTo(sealedBytes, 1 + iv.Length);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, sealedBytes);
        File.Move(temporary, path, overwrite: true);
    });

    /// <summary>The keystore's AES key for the secrets, made the first time.</summary>
    private static IKey Key()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (store.GetKey(Alias, null) is { } existing) return existing;
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build());
        return generator.GenerateKey()!;
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace Octopus.Platform.MacOS;

/// <summary>
/// Generic passwords in the user's login keychain, through Security.framework (the value never appears on a command
/// line, as it would with the <c>security</c> tool). Service is the keychain item's service, name its account.
/// </summary>
public sealed class MacSecretStore : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;

    public bool IsAvailable => true;

    public Task<string?> GetAsync(string service, string name, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(name);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out var length, out var data, out var item);
        if (status == ErrSecItemNotFound) return null;
        Check(status, "read");
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }, cancellationToken);

    public Task SetAsync(string service, string name, string value, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(name);
        var v = Encoding.UTF8.GetBytes(value);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item);
        if (status == 0)
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            try
            {
                Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)v.Length, v), "change");
            }
            finally
            {
                CFRelease(item);
            }
            return;
        }
        if (status != ErrSecItemNotFound) Check(status, "read");
        Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, (uint)v.Length, v, out var added), "add");
        if (added != IntPtr.Zero) CFRelease(added);
    }, cancellationToken);

    public Task DeleteAsync(string service, string name, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(name);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item);
        if (status == ErrSecItemNotFound) return;
        Check(status, "read");
        SecKeychainItemFreeContent(IntPtr.Zero, data);
        try
        {
            Check(SecKeychainItemDelete(item), "delete");
        }
        finally
        {
            CFRelease(item);
        }
    }, cancellationToken);

    private static void Check(int status, string what)
    {
        if (status != 0) throw new IOException($"The keychain could not {what} the secret (OSStatus {status}).");
    }

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}

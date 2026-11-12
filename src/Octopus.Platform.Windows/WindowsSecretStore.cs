using System.Runtime.InteropServices;
using System.Text;

namespace Octopus.Platform.Windows;

/// <summary>Generic credentials in the Windows Credential Manager, named <c>&lt;service&gt;/&lt;name&gt;</c>, for the current user.</summary>
public sealed class WindowsSecretStore : ISecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public bool IsAvailable => true;

    private static string Target(string service, string name) => service + "/" + name;

    public Task<string?> GetAsync(string service, string name, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!CredRead(Target(service, name), CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new IOException($"The Credential Manager could not read the secret (error {error}).");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0) return "";
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }, cancellationToken);

    public Task SetAsync(string service, string name, string value, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(service, name),
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = name,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new IOException($"The Credential Manager could not save the secret (error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }, cancellationToken);

    public Task DeleteAsync(string service, string name, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!CredDelete(Target(service, name), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) throw new IOException($"The Credential Manager could not delete the secret (error {error}).");
        }
    }, cancellationToken);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}

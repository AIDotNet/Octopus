using System.Runtime.InteropServices;

namespace Octopus.Platform.MacOS;

/// <summary>
/// The Dock icon. Avalonia's <c>Window.Icon</c> never reaches the Dock on macOS, and an app started without a bundle
/// (<c>dotnet run</c>, a bare apphost) shows the generic executable icon there, so this sets
/// <c>NSApplication.applicationIconImage</c> through the Objective-C runtime. Inside an .app bundle the Dock uses the
/// bundle's own icon until this runs and the same image afterwards. Call it on the main (UI) thread.
/// </summary>
public sealed class MacAppIcon : IAppIcon
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public void Set(byte[] png)
    {
        try
        {
            var handle = GCHandle.Alloc(png, GCHandleType.Pinned);
            try
            {
                // NSData copies the bytes, so the array only has to stay put for this one call.
                var data = Send(GetClass("NSData"), Sel("dataWithBytes:length:"), handle.AddrOfPinnedObject(), (nuint)png.Length);
                var image = Send(Send(GetClass("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
                if (image == IntPtr.Zero)
                {
                    return; // not a decodable image
                }
                // NSApplication keeps the image; the reference taken by alloc is its to keep.
                var app = Send(GetClass("NSApplication"), Sel("sharedApplication"));
                Send(app, Sel("setApplicationIconImage:"), image);
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // No Objective-C runtime to talk to: the Dock keeps its default icon.
        }
    }

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument, nuint length);
}

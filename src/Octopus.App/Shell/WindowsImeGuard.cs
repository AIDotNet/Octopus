using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Octopus.App.Shell;

/// <summary>
/// Filters duplicate <c>WM_CHAR</c> messages emitted by Windows Chinese IMEs (such as QQ Pinyin, Sogou, Microsoft
/// Pinyin, or Rime) immediately after committing a composition via <c>WM_IME_COMPOSITION</c> (<c>GCS_RESULTSTR</c>)
/// when the user presses <c>Shift</c> to commit and toggle English mode.
/// </summary>
internal sealed class ImeCommitDuplicateFilter
{
    public const long DefaultWindowMs = 150;

    internal const int VkShift = 0x10;
    internal const int VkControl = 0x11;
    internal const int VkMenu = 0x12;
    internal const int VkLShift = 0xA0;
    internal const int VkRShift = 0xA1;
    internal const int VkLControl = 0xA2;
    internal const int VkRControl = 0xA3;
    internal const int VkLMenu = 0xA4;
    internal const int VkRMenu = 0xA5;
    internal const int VkProcessKey = 0xE5;

    private readonly Func<long> _clockMs;
    private readonly long _windowMs;
    private string? _pendingText;
    private int _pendingIndex;
    private long _committedAtMs;

    public ImeCommitDuplicateFilter(Func<long>? clockMs = null, long windowMs = DefaultWindowMs)
    {
        _clockMs = clockMs ?? DefaultClock;
        _windowMs = windowMs;
    }

    public void OnImeResultCommitted(string? committedText)
    {
        if (string.IsNullOrEmpty(committedText))
        {
            return;
        }

        _pendingText = committedText;
        _pendingIndex = 0;
        _committedAtMs = _clockMs();
    }

    public void OnKeyDown(int virtualKey)
    {
        if (virtualKey == VkProcessKey || IsModifierKey(virtualKey))
        {
            return;
        }

        // A genuine non-modifier key press before WM_CHAR proves subsequent WM_CHAR is real typing, not an IME duplicate.
        Reset();
    }

    public bool ShouldSuppressChar(char ch)
    {
        if (_pendingText is null)
        {
            return false;
        }

        if (_clockMs() - _committedAtMs > _windowMs)
        {
            Reset();
            return false;
        }

        if (_pendingIndex < _pendingText.Length && _pendingText[_pendingIndex] == ch)
        {
            _pendingIndex++;
            if (_pendingIndex >= _pendingText.Length)
            {
                Reset();
            }

            return true;
        }

        Reset();
        return false;
    }

    public void Reset()
    {
        _pendingText = null;
        _pendingIndex = 0;
    }

    private static long DefaultClock() => Environment.TickCount64;

    private static bool IsModifierKey(int virtualKey) =>
        virtualKey is VkShift or VkControl or VkMenu or VkLShift or VkRShift or VkLControl or VkRControl or VkLMenu or VkRMenu;
}

/// <summary>
/// Hooks the Win32 window procedure of an Avalonia <see cref="Window"/> to suppress duplicate <c>WM_CHAR</c> messages
/// emitted after a <c>WM_IME_COMPOSITION</c> commit (such as QQ Pinyin committing via <c>Shift</c>).
/// </summary>
internal static partial class WindowsImeGuard
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmChar = 0x0102;
    private const uint WmImeComposition = 0x010F;
    private const uint GcsResultStr = 0x0800;
    private const int MaxResultBytes = 65536;
    private const int StackAllocCharLimit = 256;

    public static void Attach(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var filter = new ImeCommitDuplicateFilter();

        IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WmImeComposition:
                    if ((lParam.ToInt64() & GcsResultStr) != 0)
                    {
                        filter.OnImeResultCommitted(TryReadImeResultString(hWnd));
                    }
                    break;

                case WmKeyDown:
                    filter.OnKeyDown((int)(wParam.ToInt64() & 0xFFFF));
                    break;

                case WmChar:
                    if (filter.ShouldSuppressChar((char)(wParam.ToInt64() & 0xFFFF)))
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                    break;
            }

            return IntPtr.Zero;
        }

        var callback = new Win32Properties.CustomWndProcHookCallback(OnWndProc);
        Win32Properties.AddWndProcHookCallback(window, callback);
        window.Closed += (_, _) => Win32Properties.RemoveWndProcHookCallback(window, callback);
    }

    private static unsafe string? TryReadImeResultString(IntPtr hWnd)
    {
        var hImc = ImmGetContext(hWnd);
        if (hImc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var byteCount = ImmGetCompositionStringW(hImc, GcsResultStr, null, 0);
            if (byteCount <= 0 || byteCount > MaxResultBytes)
            {
                return null;
            }

            var charCount = byteCount / sizeof(char);
            if (charCount <= 0)
            {
                return null;
            }

            if (charCount <= StackAllocCharLimit)
            {
                var stackBuffer = stackalloc char[charCount];
                var readBytes = ImmGetCompositionStringW(hImc, GcsResultStr, stackBuffer, (uint)byteCount);
                if (readBytes <= 0)
                {
                    return null;
                }

                var charsRead = Math.Min(readBytes, byteCount) / sizeof(char);
                return charsRead > 0 ? new string(stackBuffer, 0, charsRead) : null;
            }

            var buffer = new char[charCount];
            fixed (char* ptr = buffer)
            {
                var readBytes = ImmGetCompositionStringW(hImc, GcsResultStr, ptr, (uint)byteCount);
                if (readBytes <= 0)
                {
                    return null;
                }

                var charsRead = Math.Min(readBytes, byteCount) / sizeof(char);
                return charsRead > 0 ? new string(buffer, 0, charsRead) : null;
            }
        }
        finally
        {
            _ = ImmReleaseContext(hWnd, hImc);
        }
    }

    [LibraryImport("imm32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr ImmGetContext(IntPtr hWnd);

    [LibraryImport("imm32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ImmReleaseContext(IntPtr hWnd, IntPtr hImc);

    [LibraryImport("imm32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int ImmGetCompositionStringW(IntPtr hImc, uint dwIndex, void* lpBuf, uint dwBufLen);
}

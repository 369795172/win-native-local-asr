using System.Runtime.InteropServices;
using System.Text;

namespace WinLocalASR.Core.Output;

/// <summary>
/// Sets text on the OS clipboard. Retries up to 3 attempts with 50 ms between
/// attempts (Win32 clipboard open/close lock contention with other processes).
/// The delay is injectable so retry tests run instantly.
/// </summary>
public sealed class ClipboardWriter
{
    public const int DefaultMaxAttempts = 3;
    public const int DefaultRetryDelayMs = 50;

    private readonly IClipboardInterop _interop;
    private readonly int _maxAttempts;
    private readonly int _retryDelayMs;
    private readonly Action<int>? _delay;

    public ClipboardWriter(
        IClipboardInterop? interop = null,
        int maxAttempts = DefaultMaxAttempts,
        int retryDelayMs = DefaultRetryDelayMs,
        Action<int>? delay = null)
    {
        _interop = interop ?? new Win32ClipboardInterop();
        _maxAttempts = maxAttempts;
        _retryDelayMs = retryDelayMs;
        _delay = delay;
    }

    /// <returns>True if the text reached the clipboard; false if all attempts failed.</returns>
    public bool SetText(string text)
    {
        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            if (_interop.TrySetText(text))
            {
                return true;
            }

            if (attempt < _maxAttempts)
            {
                if (_delay is null)
                {
                    Thread.Sleep(_retryDelayMs);
                }
                else
                {
                    _delay(_retryDelayMs);
                }
            }
        }

        return false;
    }
}

/// <summary>Platform clipboard seam; the real implementation is Windows-only.</summary>
public interface IClipboardInterop
{
    bool TrySetText(string text);

    bool TryGetText(out string? text);
}

/// <summary>
/// Win32 clipboard via user32/kernel32 P/Invoke (no WinForms dependency in Core).
/// Single-shot per call — outer <see cref="ClipboardWriter"/> owns retry policy.
/// </summary>
public sealed partial class Win32ClipboardInterop : IClipboardInterop
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public bool TrySetText(string text)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!NativeMethods.OpenClipboard(nint.Zero))
        {
            return false;
        }

        try
        {
            if (!NativeMethods.EmptyClipboard())
            {
                return false;
            }

            byte[] payload = Encoding.Unicode.GetBytes(text);
            byte[] bytes = new byte[payload.Length + 2]; // UTF-16 null terminator
            payload.CopyTo(bytes, 0);
            nint hGlobal = NativeMethods.GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
            if (hGlobal == nint.Zero)
            {
                return false;
            }

            nint ptr = NativeMethods.GlobalLock(hGlobal);
            if (ptr == nint.Zero)
            {
                NativeMethods.GlobalFree(hGlobal);
                return false;
            }

            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
            }
            finally
            {
                NativeMethods.GlobalUnlock(hGlobal);
            }

            if (NativeMethods.SetClipboardData(CfUnicodeText, hGlobal) != nint.Zero)
            {
                return true; // ownership transferred to the system
            }

            NativeMethods.GlobalFree(hGlobal);
            return false;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public bool TryGetText(out string? text)
    {
        text = null;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!NativeMethods.OpenClipboard(nint.Zero))
        {
            return false;
        }

        try
        {
            nint handle = NativeMethods.GetClipboardData(CfUnicodeText);
            if (handle == nint.Zero)
            {
                return false;
            }

            nint ptr = NativeMethods.GlobalLock(handle);
            if (ptr == nint.Zero)
            {
                return false;
            }

            try
            {
                unsafe
                {
                    int length = 0;
                    char* current = (char*)ptr;
                    while (current[length] != '\0')
                    {
                        length++;
                    }

                    text = new string(current, 0, length);
                }

                return true;
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    internal static partial class NativeMethods
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenClipboard(nint hWndNewOwner);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseClipboard();

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EmptyClipboard();

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint SetClipboardData(uint uFormat, nint hMem);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint GetClipboardData(uint uFormat);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial nint GlobalLock(nint hMem);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GlobalUnlock(nint hMem);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial nint GlobalFree(nint hMem);
    }
}

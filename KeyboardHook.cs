using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceSiteLauncher;

internal sealed class PushToTalk(Action onStart, Action onStop)
{
    private readonly HashSet<uint> _pressed = [];
    private bool _active;

    public void Handle(uint scanCode, bool down)
    {
        if (scanCode is not (0x2F or 0x40))
            return;
        if (down)
            _pressed.Add(scanCode);
        else
            _pressed.Remove(scanCode);
        if (!_active && _pressed.Count == 2)
        {
            _active = true;
            onStart();
        }
        else if (_active && _pressed.Count == 0)
        {
            _active = false;
            onStop();
        }
    }
}

internal sealed class KeyboardHook : IDisposable
{
    private delegate nint HookCallback(int code, nuint message, nint data);
    private readonly HookCallback _callback;
    private readonly Action<uint, bool> _handle;
    private nint _hook;

    public KeyboardHook(Action<uint, bool> handle)
    {
        _handle = handle;
        _callback = OnKey;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(module.ModuleName), 0);
        if (_hook == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось установить клавиатурный hook.");
    }

    private nint OnKey(int code, nuint message, nint data)
    {
        if (code >= 0 && message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            var key = Marshal.PtrToStructure<KeyboardData>(data);
            if ((key.Flags & 0x11) == 0 && key.ScanCode is 0x2F or 0x40)
                _handle(key.ScanCode, message is 0x100 or 0x104);
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook == 0)
            return;
        UnhookWindowsHookEx(_hook);
        _hook = 0;
        GC.KeepAlive(_callback);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string name);
}

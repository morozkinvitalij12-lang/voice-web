using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VoiceSiteLauncher;

// Raw Input вместо глобального WH_KEYBOARD_LL: система не гоняет каждое нажатие
// через наш callback, и античиты игр не видят низкоуровневый хук от администратора.
internal sealed class KeyboardInputWindow : Control
{
    private const int WmInput = 0x00FF;
    private const int RidInput = 0x10000003;
    private const int RidevInputsink = 0x00000100;
    private readonly Action<uint, bool> _handle;
    private readonly byte[] _buffer = new byte[64];

    public KeyboardInputWindow(Action<uint, bool> handle) => _handle = handle;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var device = new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevInputsink, Target = Handle };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось зарегистрировать Raw Input клавиатуры.");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmInput)
        {
            var size = (uint)_buffer.Length;
            var read = GetRawInputData(m.LParam, RidInput, _buffer, ref size, (uint)Marshal.SizeOf<RawInputHeader>());
            if (read != uint.MaxValue && read > 0 && RawInputParser.TryGetKey(_buffer, out var scanCode, out var down))
                _handle(scanCode, down);
        }
        base.WndProc(ref m);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Data;
        public nuint WParam;
        public nint Device;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint rawInput, uint command, [Out] byte[] data, ref uint size, uint headerSize);
}

internal static class RawInputParser
{
    // x64: RAWINPUTHEADER = 24 байта, RAWKEYBOARD = 16 байт. Нас интересуют только V (0x2F) и F6 (0x40).
    public static bool TryGetKey(byte[] data, out uint scanCode, out bool down)
    {
        scanCode = 0;
        down = false;
        if (data.Length < 40)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)) != 1)
            return false;
        var makeCode = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(24, 2));
        if (makeCode is not (0x2F or 0x40))
            return false;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26, 2));
        scanCode = makeCode;
        down = (flags & 0x01) == 0;
        return true;
    }
}
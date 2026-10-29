using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

// Converts the mouse's private BLE button events to an ordinary Right Alt key.
// It does not activate another window, paste text or capture keyboard input.
public sealed class M110HotkeyLink : IDisposable
{
    private readonly string mode;
    private readonly Action<bool> emit;
    private readonly Action<int> wait;
    private readonly Stopwatch heldFor = new Stopwatch();
    private bool active;
    private bool keyHeld;

    public M110HotkeyLink(string mode) : this(mode, SendRightAlt, Thread.Sleep) { }

    internal M110HotkeyLink(string mode, Action<bool> emit, Action<int> wait)
    {
        if (mode != "none" && mode != "hold" && mode != "toggle") throw new ArgumentException("Unknown hotkey mode.");
        this.mode = mode; this.emit = emit; this.wait = wait;
    }

    private void Down()
    {
        emit(true);
        keyHeld = true;
        heldFor.Restart();
    }

    private void Up()
    {
        if (!keyHeld) return;
        emit(false);
        keyHeld = false;
        heldFor.Stop();
    }

    private void Tap()
    {
        Down();
        try { wait(40); }
        finally { Up(); }
    }

    public void Start()
    {
        if (active || mode == "none") return;
        if (mode == "hold") Down(); else Tap();
        active = true;
    }

    public void Stop()
    {
        if (!active) { Up(); return; }
        if (mode == "hold")
        {
            // Short taps can select an application's click-to-toggle mode.
            // Ensure even an accidental short mouse press sends a held key.
            int remaining = 900 - (int)heldFor.ElapsedMilliseconds;
            try { if (remaining > 0) wait(remaining); }
            finally { Up(); }
        }
        else Tap();
        active = false;
    }

    public void Dispose()
    {
        try { Stop(); }
        finally { Up(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort virtualKey, scanCode;
        public uint flags, time;
        public UIntPtr extraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx, dy;
        public uint data, flags, time;
        public UIntPtr extraInfo;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput keyboard;
        [FieldOffset(0)] public MouseInput mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint type; public InputUnion data; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    private static void SendRightAlt(bool down)
    {
        Input input = new Input();
        input.type = 1;
        input.data.keyboard.virtualKey = 0xA5;
        input.data.keyboard.scanCode = 0x38;
        input.data.keyboard.flags = down ? 0x1u : 0x3u; // Extended E0 key; optional KEYUP.
        if (SendInput(1, new Input[] {input}, Marshal.SizeOf(typeof(Input))) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not send Right Alt to the speech application.");
    }
}

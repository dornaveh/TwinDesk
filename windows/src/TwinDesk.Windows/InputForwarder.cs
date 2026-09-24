using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TwinDesk;

// Hooks run on the UI message thread and only enqueue packets. No network or DDC
// operation is allowed in a hook callback.
public sealed class InputForwarder : IDisposable
{
    private readonly Hook keyboardProc, mouseProc;
    private nint keyboardHook, mouseHook;
    private readonly KeyboardState keys = new();
    private readonly MiddleClickGesture middle = new(SystemInformation.DoubleClickTime,
        Math.Max(1, SystemInformation.DoubleClickSize.Width / 2), Math.Max(1, SystemInformation.DoubleClickSize.Height / 2));
    private readonly System.Windows.Forms.Timer middleTimer = new() { Interval = 15 };
    private const nuint MouseInputTag = 0x5457444D;
    private int pendingDx, pendingDy;
    private bool disposed;
    private Point anchor;
    public bool Remote { get; private set; }
    public Action<Computer?>? Shortcut;
    public Func<byte[], bool>? Forward;
    public Action? Overflow;

    public InputForwarder()
    {
        keyboardProc = Keyboard; mouseProc = Mouse;
        keyboardHook = SetWindowsHookEx(13, keyboardProc, GetModuleHandle(null), 0);
        mouseHook = SetWindowsHookEx(14, mouseProc, GetModuleHandle(null), 0);
        if (keyboardHook == 0 || mouseHook == 0) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        middleTimer.Tick += (_, _) => { ApplyMiddle(middle.Advance(Environment.TickCount64)); FlushMotion(); };
        middleTimer.Start();
    }
    public void SetRemote(bool remote)
    {
        if (Remote == remote) return;
        ReleaseMiddle();
        if (remote)
        {
            // Release modifiers already pressed on Windows before suppressing input.
            var release = keys.Held.Select(v => new NativeInput { Type = 1, Key = new KeyInput { Vk = (ushort)v, Flags = 2, Extra = KeyboardState.OwnInputTag } }).ToArray();
            if (release.Length > 0) SendInput((uint)release.Length, release, Marshal.SizeOf<NativeInput>());
            GetCursorPos(out anchor);
        }
        Remote = remote;
    }
    private void Send(object value)
    {
        if (Forward?.Invoke(JsonSerializer.SerializeToUtf8Bytes(value)) != true) { SetRemote(false); Overflow?.Invoke(); }
    }
    private nint Keyboard(int code, nint message, nint pointer)
    {
        if (code < 0) return CallNextHookEx(0, code, message, pointer);
        var k = Marshal.PtrToStructure<KeyData>(pointer);
        if (KeyboardState.IsOwnInput(k.Flags, k.Extra)) return CallNextHookEx(0, code, message, pointer);
        ApplyMiddle(middle.Flush()); FlushMotion();
        var down = message == 0x100 || message == 0x104;
        var key = keys.Process(k.Vk, down);
        if (key.Swallow)
        {
            if (key.Trigger) Shortcut?.Invoke(key.Target);
            return 1;
        }
        if (!Remote) return CallNextHookEx(0, code, message, pointer);
        var mapped = KeyboardState.Normalize(k.Vk, k.Scan, (k.Flags & 1) != 0, MapVirtualKey(k.Vk, 4));
        Send(new { kind = "key", scan = mapped.Scan, extended = mapped.Extended, down, repeat = key.Repeat });
        return 1;
    }
    private nint Mouse(int code, nint message, nint pointer)
    {
        if (code < 0) return CallNextHookEx(0, code, message, pointer);
        var m = Marshal.PtrToStructure<MouseData>(pointer);
        if (!MiddleClickGesture.IsPhysical(m.Flags)) return CallNextHookEx(0, code, message, pointer);
        var wasPending = middle.Pending;
        ApplyMiddle(middle.Advance(Environment.TickCount64));
        if (message == 0x200 && (wasPending || middle.Pending))
        {
            // While recognizing a click, hold only tiny cursor movements. If the
            // drag threshold is crossed, replay the down before all buffered motion.
            GetCursorPos(out var current);
            var dx = m.Point.X - (Remote ? anchor.X : current.X);
            var dy = m.Point.Y - (Remote ? anchor.Y : current.Y);
            pendingDx += dx; pendingDy += dy;
            ApplyMiddle(middle.Move(dx, dy, Environment.TickCount64));
            FlushMotion();
            return 1;
        }
        FlushMotion();
        if (message is 0x207 or 0x208)
        {
            ApplyMiddle(middle.Button(message == 0x207, Environment.TickCount64)); FlushMotion();
            return 1;
        }
        if (message != 0x200) { ApplyMiddle(middle.Flush()); FlushMotion(); }
        if (!Remote) return CallNextHookEx(0, code, message, pointer);
        switch ((int)message)
        {
            case 0x200:
                var dx = m.Point.X - anchor.X; var dy = m.Point.Y - anchor.Y;
                if (dx != 0 || dy != 0) Send(new { kind = "move", dx, dy });
                break;
            case 0x201: Send(new { kind = "button", button = 0, down = true }); break;
            case 0x202: Send(new { kind = "button", button = 0, down = false }); break;
            case 0x204: Send(new { kind = "button", button = 1, down = true }); break;
            case 0x205: Send(new { kind = "button", button = 1, down = false }); break;
            case 0x20B: case 0x20C: Send(new { kind = "button", button = 2 + (int)(m.Data >> 16), down = (int)message == 0x20B }); break;
            case 0x20A: case 0x20E: Send(new { kind = "scroll", delta = (short)(m.Data >> 16), horizontal = (int)message == 0x20E }); break;
        }
        return 1;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        middleTimer.Stop(); middleTimer.Dispose();
        ReleaseMiddle();
        Remote = false;
        if (keyboardHook != 0) UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
        keyboardHook = mouseHook = 0;
    }
    private void ApplyMiddle(IReadOnlyList<MiddleAction> actions)
    {
        var targetWasRemote = Remote;
        foreach (var action in actions)
        {
            if (disposed || Remote != targetWasRemote) break;
            if (action == MiddleAction.Toggle) { Shortcut?.Invoke(null); continue; }
            var down = action == MiddleAction.Down;
            if (Remote) Send(new { kind = "button", button = 2, down });
            else ReplayMouse(down ? 0x20u : 0x40u);
        }
    }
    private void FlushMotion()
    {
        if (disposed) { pendingDx = pendingDy = 0; return; }
        if (middle.Pending || (pendingDx == 0 && pendingDy == 0)) return;
        var dx = pendingDx; var dy = pendingDy; pendingDx = pendingDy = 0;
        if (Remote) Send(new { kind = "move", dx, dy }); else ReplayMouse(0x1, dx, dy);
    }
    private void ReleaseMiddle()
    {
        var actions = middle.Reset(); pendingDx = pendingDy = 0;
        if (actions.Count == 0) return;
        // Do not recurse into SetRemote if a disconnect makes this final release fail.
        if (Remote) Forward?.Invoke(JsonSerializer.SerializeToUtf8Bytes(new { kind = "button", button = 2, down = false }));
        else ReplayMouse(0x40);
    }
    private static void ReplayMouse(uint flags, int dx = 0, int dy = 0)
    {
        if (flags == 0x1)
        {
            // The hook's deltas already include Windows pointer acceleration.
            // Replay as an absolute virtual-desktop position to avoid applying it twice.
            GetCursorPos(out var current);
            var screen = SystemInformation.VirtualScreen;
            dx = (int)Math.Clamp((((long)current.X + dx - screen.Left) * 65536 + 32768) / Math.Max(1, screen.Width), 0, 65535);
            dy = (int)Math.Clamp((((long)current.Y + dy - screen.Top) * 65536 + 32768) / Math.Max(1, screen.Height), 0, 65535);
            flags |= 0x8000 | 0x4000;
        }
        var events = new[] { new NativeInput { Type = 0, Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = flags, Extra = MouseInputTag } } };
        SendInput(1, events, Marshal.SizeOf<NativeInput>());
    }
    private delegate nint Hook(int code, nint message, nint pointer);
    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public uint Vk, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct NativeInput { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyInput Key; [FieldOffset(8)] public MouseInput Mouse; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint pointer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
}

// Pure key state lets shortcut handling be tested without installing system hooks.
internal sealed class KeyboardState
{
    internal const nuint OwnInputTag = 0x5457444B;
    internal HashSet<uint> Held { get; } = [];
    private readonly HashSet<uint> swallowed = [];
    internal static bool IsOwnInput(uint flags, nuint extra) => (flags & 0x10) != 0 && extra == OwnInputTag;
    internal static (uint Scan, bool Extended) Normalize(uint vk, uint scan, bool extended, uint fallback)
    {
        if (vk == 0xA1) return (0x36, false);
        if (vk == 0xA0) return (0x2A, false);
        if (scan == 0) return (fallback & 0xFF, extended || (fallback & 0xFF00) == 0xE000);
        return (scan, extended);
    }
    internal (bool Swallow, bool Trigger, Computer? Target, bool Repeat) Process(uint vk, bool down)
    {
        var repeated = Held.Contains(vk);
        if (down) Held.Add(vk); else Held.Remove(vk);
        // Once captured, keep repeats and key-up local even if modifiers are released first.
        if (swallowed.Contains(vk)) { if (!down) swallowed.Remove(vk); return (true, false, null, repeated && down); }
        var ctrl = Held.Contains(0xA2) || Held.Contains(0xA3) || Held.Contains(0x11);
        var alt = Held.Contains(0xA4) || Held.Contains(0xA5) || Held.Contains(0x12);
        if (down && ctrl && alt && vk is >= 0x79 and <= 0x7B)
        {
            swallowed.Add(vk);
            return (true, !repeated, vk == 0x7A ? Computer.PC : vk == 0x79 ? Computer.Mac : null, repeated);
        }
        return (false, false, null, repeated && down);
    }
}

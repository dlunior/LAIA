using System.Runtime.InteropServices;

namespace Laia.Sim;

/// <summary>
/// Reads the keyboard, mouse side buttons and joystick/yoke/HOTAS buttons globally, so push-to-talk
/// works while the flight simulator has focus.
/// </summary>
internal static class Input
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("winmm.dll")] private static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public int dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
        public int dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }

    private const int JOY_RETURNBUTTONS = 0x80;
    private const int MaxJoysticks = 16;

    public static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static int? JoyButtons(int id)
    {
        var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS };
        return joyGetPosEx(id, ref info) == 0 ? info.dwButtons : null;
    }

    public static bool IsDown(PttBinding b) => b.Kind == "joy"
        ? (JoyButtons(b.Device) is int mask && (mask & (1 << b.Code)) != 0)
        : KeyDown(b.Code);

    /// <summary>Everything currently held, so the capture screen can ignore buttons already pressed.</summary>
    public static HashSet<PttBinding> Snapshot()
    {
        var set = new HashSet<PttBinding>();
        foreach (var vk in CandidateKeys()) if (KeyDown(vk)) set.Add(new PttBinding("key", vk));
        for (var j = 0; j < MaxJoysticks; j++)
        {
            if (JoyButtons(j) is not int mask) continue;
            for (var b = 0; b < 32; b++) if ((mask & (1 << b)) != 0) set.Add(new PttBinding("joy", b, j));
        }
        return set;
    }

    private static IEnumerable<int> CandidateKeys()
    {
        yield return 0x04; yield return 0x05; yield return 0x06; // middle + side mouse buttons
        for (var vk = 0x08; vk <= 0xFE; vk++)
        {
            if (vk is 0x10 or 0x11 or 0x12) continue; // generic Shift/Ctrl/Alt; use left/right versions
            if (vk is 0x1B) continue;                 // Escape cancels capture
            yield return vk;
        }
    }
}

/// <summary>Watches the bound push-to-talk input and raises pressed / released.</summary>
internal sealed class PttMonitor : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private bool _down;
    public PttBinding? Binding { get; set; }
    public bool Suspended { get; set; }
    public event Action? Pressed;
    public event Action? Released;

    public PttMonitor()
    {
        _timer.Tick += (_, _) =>
        {
            var now = !Suspended && Binding is not null && Input.IsDown(Binding);
            if (now && !_down) Pressed?.Invoke();
            else if (!now && _down) Released?.Invoke();
            _down = now;
        };
        _timer.Start();
    }

    public void Dispose() => _timer.Dispose();
}

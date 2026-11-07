using System.Runtime.InteropServices;

namespace NomadModDownloader.Core;

/// <summary>
/// Xbox-style controllers through Windows' XInput (Steam Input shows most other controllers to Windows this way too).
/// Polled by the UI; reports presses and held directions with key-repeat.
/// </summary>
public sealed class Gamepad
{
    [Flags]
    public enum Buttons : ushort
    {
        None = 0, Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008, Start = 0x0010, Back = 0x0020,
        LeftThumb = 0x0040, RightThumb = 0x0080, LB = 0x0100, RB = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
    }

    public enum Dir { None, Up, Down, Left, Right }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern uint GetState14(uint index, out XInputState state);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] private static extern uint GetState910(uint index, out XInputState state);

    private static int _api; // 0 = untried, 1 = 1.4, 2 = 9.1.0, -1 = none
    private int _index = -1;
    private DateTime _nextScan;
    private Buttons _last;
    private Dir _held;
    private DateTime _repeatAt;

    public bool Connected => _index >= 0;
    /// <summary>Right stick, -1..1 (up is positive).</summary>
    public float RightY { get; private set; }
    public float RightX { get; private set; }

    private static bool Read(uint i, out XInputState s)
    {
        s = default;
        try
        {
            if (_api is 0 or 1)
            {
                var r = GetState14(i, out s);
                _api = 1;
                return r == 0;
            }
        }
        catch (DllNotFoundException) { _api = 2; }
        catch (EntryPointNotFoundException) { _api = 2; }
        try
        {
            if (_api == 2) return GetState910(i, out s) == 0;
        }
        catch { _api = -1; }
        return false;
    }

    /// <summary>Reads the controller. Returns the buttons pressed since last time, and a direction (with repeat when held).</summary>
    public (Buttons Pressed, Dir Move) Poll()
    {
        if (_api == -1) return (Buttons.None, Dir.None);
        XInputState s = default;
        if (_index < 0 || !Read((uint)_index, out s))
        {
            _index = -1;
            if (DateTime.UtcNow < _nextScan) return (Buttons.None, Dir.None);
            _nextScan = DateTime.UtcNow.AddSeconds(2);
            for (uint i = 0; i < 4; i++)
                if (Read(i, out s)) { _index = (int)i; _last = (Buttons)s.Gamepad.wButtons; break; }
            return (Buttons.None, Dir.None);
        }

        var now = (Buttons)s.Gamepad.wButtons;
        var pressed = now & ~_last;
        _last = now;
        RightY = Axis(s.Gamepad.sThumbRY);
        RightX = Axis(s.Gamepad.sThumbRX);

        var lx = Axis(s.Gamepad.sThumbLX);
        var ly = Axis(s.Gamepad.sThumbLY);
        var dir = now.HasFlag(Buttons.Up) || ly > 0.6f ? Dir.Up
                : now.HasFlag(Buttons.Down) || ly < -0.6f ? Dir.Down
                : now.HasFlag(Buttons.Left) || lx < -0.6f ? Dir.Left
                : now.HasFlag(Buttons.Right) || lx > 0.6f ? Dir.Right : Dir.None;
        var move = Dir.None;
        if (dir == Dir.None) _held = Dir.None;
        else if (dir != _held) { _held = dir; move = dir; _repeatAt = DateTime.UtcNow.AddMilliseconds(380); }
        else if (DateTime.UtcNow >= _repeatAt) { move = dir; _repeatAt = DateTime.UtcNow.AddMilliseconds(110); }
        return (pressed & ~(Buttons.Up | Buttons.Down | Buttons.Left | Buttons.Right), move);
    }

    private static float Axis(short v)
    {
        var f = v / 32767f;
        return Math.Abs(f) < 0.25f ? 0 : Math.Clamp(f, -1f, 1f);
    }
}

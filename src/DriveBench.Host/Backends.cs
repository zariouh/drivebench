using System.Runtime.InteropServices;
using DriveBench.Core;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace DriveBench.Host;

internal interface IBackend : IDisposable
{
    bool Real { get; }
    void Submit(PadReport report);
    bool ReadbackMatches(PadReport report);
}
internal sealed class DryRun : IBackend
{
    public bool Real => false;
    public void Submit(PadReport report) { }
    public bool ReadbackMatches(PadReport report) => false;
    public void Dispose() { }
}
internal sealed class VigemBackend : IBackend
{
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _pad;
    public bool Real => true;
    public VigemBackend()
    {
        _client = new ViGEmClient();
        try { _pad = _client.CreateXbox360Controller(); _pad.AutoSubmitReport = false; _pad.Connect(); Submit(default); }
        catch { _client.Dispose(); throw; }
    }
    public void Submit(PadReport report)
    {
        _pad.ResetReport();
        _pad.SetAxisValue(Xbox360Axis.LeftThumbX, report.Steering);
        _pad.SetSliderValue(Xbox360Slider.LeftTrigger, report.Brake);
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, report.Throttle);
        _pad.SubmitReport();
    }
    public bool ReadbackMatches(PadReport report)
    {
        try
        {
            int index = _pad.UserIndex;
            if (index is < 0 or > 3 || XInputGetState((uint)index, out var state) != 0) return false;
            return state.Gamepad.LeftX == report.Steering && state.Gamepad.LeftTrigger == report.Brake &&
                state.Gamepad.RightTrigger == report.Throttle && state.Gamepad.Buttons == 0 &&
                state.Gamepad.LeftY == 0 && state.Gamepad.RightX == 0 && state.Gamepad.RightY == 0;
        }
        catch { return false; } // Enumeration/readback failure is shown, never confused with a matched report.
    }
    public void Dispose()
    {
        try { Submit(default); } finally { try { _pad.Disconnect(); } finally { _client.Dispose(); } }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Gamepad
    { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftX, LeftY, RightX, RightY; }
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint PacketNumber; public Gamepad Gamepad; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern uint XInputGetState(uint index, out State state);
}

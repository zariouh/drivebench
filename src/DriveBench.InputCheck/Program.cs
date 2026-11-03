using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using Vortice.DirectInput;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is ["--self-test"]) return ProbeStats.SelfTest();
        if (!OperatingSystem.IsWindows()) return 3;
        using var finished = new ManualResetEventSlim();
        new Thread(() => { if (!finished.Wait(TimeSpan.FromSeconds(45))) Environment.Exit(70); }) { IsBackground = true }.Start();
        try
        {
            using var window = new ProbeWindow();
            using var input = DInput.DirectInput8Create();
            var instances = input.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
            var devices = new List<DeviceProbe>();
            foreach (var instance in instances.Take(8))
            {
                try
                {
                    var device = input.CreateDevice(instance.InstanceGuid);
                    device.SetDataFormat<RawJoystickState>();
                    device.SetCooperativeLevel(window.Handle, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                    var acquired = device.Acquire();
                    devices.Add(new DeviceProbe(instance.InstanceName, device, acquired.Success));
                }
                catch { }
            }
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(25))
            {
                window.Pump(); foreach (var d in devices) d.Sample(clock.Elapsed.TotalSeconds); Thread.Sleep(50);
            }
            foreach (var d in devices) Console.WriteLine(d.Summary());
            return 0;
        }
        finally { finished.Set(); }
    }
}

internal sealed class DeviceProbe(string name, IDirectInputDevice8 device, bool acquired) : IDisposable
{
    private readonly ProbeStats _stats = new(["X", "Y", "Z", "Rx", "Ry", "Rz"]);
    private bool _acquired = acquired;
    public void Sample(double s)
    {
        try
        {
            if (!_acquired && device.Acquire().Failure) return;
            _acquired = true; if (device.Poll().Failure) { _acquired = false; return; }
            var state = device.GetCurrentJoystickState();
            _stats.Observe([state.X, state.Y, state.Z, state.RotationX, state.RotationY, state.RotationZ]);
        } catch { _acquired = false; }
    }
    public string Summary() => name + ": " + _stats.Summary();
    public void Dispose() { try { device.Unacquire(); } catch { } device.Dispose(); }
}

internal sealed class ProbeWindow : IDisposable
{
    public nint Handle { get; }
    public ProbeWindow() { Handle = Native.CreateWindowEx(0, "STATIC", "DriveBench", 0, 0, 0, 1, 1, 0, 0, Native.GetModuleHandle(null), 0); }
    public void Pump() { while (Native.PeekMessage(out var m, 0, 0, 0, 1)) { Native.TranslateMessage(ref m); Native.DispatchMessage(ref m); } }
    public void Dispose() => Native.DestroyWindow(Handle);
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct XState { public uint Packet; public short LX; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint ex, string c, string t, uint s, int x, int y, int w, int h, nint p, nint m, nint i, nint pa);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint w);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessage(out Message m, nint w, uint min, uint max, uint r);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref Message m);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static extern nint DispatchMessage(ref Message m);
}
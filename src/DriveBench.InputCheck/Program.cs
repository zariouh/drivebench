using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.DirectInput;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("DriveBench InputCheck 0.1.2 — read-only DirectInput / XInput probe");
        Console.WriteLine($"OS: {Environment.OSVersion}; process: {RuntimeInformation.ProcessArchitecture}; runtime: {RuntimeInformation.FrameworkDescription}");
        if (args is ["--self-test"]) return ProbeStats.SelfTest();
        if (args.Length != 0) return 2;
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Windows is required for live input capture."); return 3; }
        using var finished = new ManualResetEventSlim();
        var watchdog = new Thread(() =>
        {
            if (finished.Wait(TimeSpan.FromSeconds(45))) return;
            Console.Error.WriteLine("TIMEOUT: the input probe did not finish in 45 seconds.");
            Environment.Exit(70);
        }) { IsBackground = true, Name = "InputCheck timeout" };
        watchdog.Start();
        try { return Capture(); }
        catch (Exception ex) { Console.WriteLine("FATAL: " + ex); return 1; }
        finally { finished.Set(); watchdog.Join(TimeSpan.FromSeconds(1)); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Capture()
    {
        bool lfsRunning = Process.GetProcessesByName("LFS").Any();
        Console.WriteLine("LFS process present at capture start: " + lfsRunning);
        Console.WriteLine("The probe does not create controllers, submit input, change axis ranges, or acquire keyboard/mouse input.");
        using var window = new ProbeWindow();
        using var input = DInput.DirectInput8Create();
        var instances = input.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
        Console.WriteLine("Attached DirectInput game-controller count: " + instances.Count);
        var devices = new List<DeviceProbe>();
        try
        {
            foreach (var instance in instances.Take(8))
            {
                Console.WriteLine($"DEVICE: {instance.InstanceName}; product: {instance.ProductName}; instance GUID: {instance.InstanceGuid}; product GUID: {instance.ProductGuid}");
                IDirectInputDevice8? device = null;
                try
                {
                    device = input.CreateDevice(instance.InstanceGuid);
                    Result format = device.SetDataFormat<RawJoystickState>();
                    Console.WriteLine($"  SetDataFormat: 0x{unchecked((uint)format.Code):X8}");
                    format.CheckError();
                    Result cooperative = device.SetCooperativeLevel(window.Handle, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                    Console.WriteLine($"  Background/nonexclusive cooperative level: 0x{unchecked((uint)cooperative.Code):X8}");
                    cooperative.CheckError();
                    Result acquired = device.Acquire();
                    Console.WriteLine($"  Acquire: 0x{unchecked((uint)acquired.Code):X8}");
                    devices.Add(new DeviceProbe(instance.InstanceName, device, acquired.Success));
                    device = null;
                }
                catch (Exception ex) { Console.WriteLine("  SETUP ERROR: " + ex); }
                finally { device?.Dispose(); }
            }
            var xinput = Enumerable.Range(0, 4).Select(slot => new XInputProbe(slot)).ToArray();
            Console.WriteLine("RECORDING for 25 seconds. In DriveBench set steering +100%, pedals zero, then click 5-second game test once. Leave the host running.");
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(25))
            {
                window.Pump();
                foreach (var device in devices) device.Sample(clock.Elapsed.TotalSeconds);
                foreach (var slot in xinput) slot.Sample(clock.Elapsed.TotalSeconds);
                Thread.Sleep(50);
            }
            Console.WriteLine("\n=== SUMMARY — send this section and any earlier ERROR lines ===");
            Console.WriteLine($"DirectInput setup succeeded for {devices.Count} of {instances.Count} enumerated game controllers (capture limit 8).");
            foreach (var device in devices) device.Summarize();
            foreach (var slot in xinput) slot.Summarize();
            Console.WriteLine("Movement in both APIs points to a game-specific configuration/acquisition or per-process issue; this probe alone cannot identify which.");
            Console.WriteLine("XInput movement with no DirectInput movement requires investigating the legacy input path. Match device identities before drawing conclusions if more than one controller is connected.");
            Console.WriteLine("No movement in either API makes this recording inconclusive: verify that the intended five-second test occurred during capture.");
            Console.WriteLine("END OF REPORT");
            return 0;
        }
        finally { foreach (var device in devices) device.Dispose(); }
    }
}

internal sealed class DeviceProbe(string name, IDirectInputDevice8 device, bool acquired) : IDisposable
{
    private readonly ProbeStats _stats = new(["X", "Y", "Z", "Rx", "Ry", "Rz"]);
    private bool _acquired = acquired;
    private double _nextAcquire;
    private int _eventLines;

    public void Sample(double seconds)
    {
        try
        {
            if (!_acquired)
            {
                if (seconds < _nextAcquire) return;
                _nextAcquire = seconds + .5;
                Result result = device.Acquire();
                if (result.Failure) { Error("Acquire", result.Code, seconds); return; }
                _acquired = true;
            }
            Result poll = device.Poll();
            if (poll.Failure)
            {
                Error("Poll", poll.Code, seconds);
                _acquired = false;
                return;
            }
            // GetCurrentJoystickState throws on a failed native GetDeviceState.
            // Record that failure instead of interpreting it as a zero report.
            var state = device.GetCurrentJoystickState();
            int[] values = [state.X, state.Y, state.Z, state.RotationX, state.RotationY, state.RotationZ];
            bool changed = _stats.Observe(values);
            if ((_stats.Samples == 1 || changed) && _eventLines++ < 24)
                Console.WriteLine($"{seconds:F2}s DINPUT {name}: X={state.X} Y={state.Y} Z={state.Z} Rx={state.RotationX} Ry={state.RotationY} Rz={state.RotationZ}");
        }
        catch (SharpGenException ex) { Error("GetDeviceState", ex.ResultCode.Code, seconds); _acquired = false; }
        catch (Exception ex)
        {
            Error("ReadException", ex.HResult, seconds);
            if (_eventLines++ < 24) Console.WriteLine(ex.ToString());
            _acquired = false;
        }
    }

    private void Error(string operation, int result, double seconds)
    {
        string key = $"{operation}:0x{unchecked((uint)result):X8}";
        if (_stats.RecordError(key)) Console.WriteLine($"{seconds:F2}s DINPUT ERROR {name}: {key}");
    }
    public void Summarize() => Console.WriteLine("DINPUT " + name + "\n" + _stats.Summary());
    public void Dispose() { try { device.Unacquire(); } catch { } device.Dispose(); }
}

internal sealed class XInputProbe(int slot)
{
    private readonly ProbeStats _stats = new(["LX", "LY", "RX", "RY", "Brake", "Throttle"]);
    private int _eventLines;
    private uint _lastPacket;
    private int _packetChanges;
    public void Sample(double seconds)
    {
        uint result = Native.XInputGetState((uint)slot, out var state);
        if (result != 0)
        {
            _stats.RecordError("XInputGetState:" + result);
            return;
        }
        bool changed = _stats.Observe([state.Pad.LeftX, state.Pad.LeftY, state.Pad.RightX, state.Pad.RightY, state.Pad.LeftTrigger, state.Pad.RightTrigger]);
        if (_stats.Samples > 1 && state.Packet != _lastPacket) _packetChanges++;
        _lastPacket = state.Packet;
        if ((_stats.Samples == 1 || changed) && _eventLines++ < 24)
            Console.WriteLine($"{seconds:F2}s XINPUT slot {slot}: packet={state.Packet} LX={state.Pad.LeftX} brake={state.Pad.LeftTrigger} throttle={state.Pad.RightTrigger}");
    }
    public void Summarize()
    {
        if (_stats.Samples == 0)
        {
            Console.WriteLine($"XINPUT slot {slot}: no successful reads (usually an unused slot). " + _stats.ErrorSummary());
            return;
        }
        Console.WriteLine($"XINPUT slot {slot}; packet changes={_packetChanges}\n" + _stats.Summary());
    }
}

internal sealed class ProbeWindow : IDisposable
{
    public nint Handle { get; }
    public ProbeWindow()
    {
        // A real hidden top-level window owned by this process, not the desktop
        // or a console-host window belonging to a different process.
        Handle = Native.CreateWindowEx(0, "STATIC", "DriveBench read-only input probe", 0,
            0, 0, 1, 1, 0, 0, Native.GetModuleHandle(null), 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Pump()
    {
        for (int count = 0; count < 100 && Native.PeekMessage(out var message, 0, 0, 0, 1); count++)
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
    }
    public void Dispose() => Native.DestroyWindow(Handle);
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Gamepad
    { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftX, LeftY, RightX, RightY; }
    [StructLayout(LayoutKind.Sequential)] internal struct XState { public uint Packet; public Gamepad Pad; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] internal static extern uint XInputGetState(uint slot, out XState state);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static extern nint DispatchMessage(ref Message message);
}

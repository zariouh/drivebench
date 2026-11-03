internal sealed class ProbeStats(string[] axisNames)
{
    private readonly int[] _minimum = new int[axisNames.Length];
    private readonly int[] _maximum = new int[axisNames.Length];
    private readonly int[] _last = new int[axisNames.Length];
    private readonly Dictionary<string, int> _errors = new();
    public int Samples { get; private set; }
    public int Changes { get; private set; }

    public bool Observe(ReadOnlySpan<int> values)
    {
        if (values.Length != axisNames.Length) throw new ArgumentException("Axis count differs from the probe's format.", nameof(values));
        bool changed = false;
        for (int i = 0; i < values.Length; i++)
        {
            if (Samples == 0) _minimum[i] = _maximum[i] = values[i];
            else
            {
                changed |= values[i] != _last[i];
                _minimum[i] = Math.Min(_minimum[i], values[i]);
                _maximum[i] = Math.Max(_maximum[i], values[i]);
            }
            _last[i] = values[i];
        }
        Samples++;
        if (changed) Changes++;
        return changed;
    }
    public bool RecordError(string key)
    {
        _errors.TryGetValue(key, out int count);
        _errors[key] = count + 1;
        return count == 0;
    }
    public string Summary()
    {
        string ranges = Samples == 0 ? "No successful state reads; axis values unavailable." :
            string.Join("; ", axisNames.Select((name, i) => $"{name}={_minimum[i]}..{_maximum[i]}"));
        return $"  Successful samples={Samples}; axis-change events={Changes}\n  {ranges}\n  " + ErrorSummary();
    }
    public string ErrorSummary() => "Errors: " + (_errors.Count == 0 ? "none" : string.Join("; ", _errors.Select(e => $"{e.Key} x{e.Value}")));
    public static int SelfTest()
    {
        static void Require(bool condition) { if (!condition) throw new Exception("InputCheck summary self-test failed."); }
        Require(System.Runtime.InteropServices.Marshal.SizeOf<Native.Gamepad>() == 12);
        Require(System.Runtime.InteropServices.Marshal.SizeOf<Native.XState>() == 16);
        Require(System.Runtime.InteropServices.Marshal.SizeOf<Native.Message>() == (IntPtr.Size == 8 ? 48 : 32));
        Require(System.Runtime.InteropServices.Marshal.SizeOf<Vortice.DirectInput.RawJoystickState>() == 272);
        var failed = new ProbeStats(["X"]);
        Require(failed.RecordError("GetDeviceState:0x8007000C"));
        Require(!failed.RecordError("GetDeviceState:0x8007000C"));
        Require(failed.Samples == 0 && failed.Summary().Contains("values unavailable") && failed.Summary().Contains("x2"));
        var trace = new ProbeStats(["X", "Brake", "Throttle"]);
        Require(!trace.Observe([32767, 0, 0]));
        Require(!trace.Observe([32767, 0, 0]));
        Require(trace.Observe([65535, 0, 255]));
        Require(trace.Observe([32767, 255, 0]));
        Require(trace.Samples == 4 && trace.Changes == 2);
        string report = trace.Summary();
        Require(report.Contains("X=32767..65535") && report.Contains("Brake=0..255") && report.Contains("Throttle=0..255"));
        bool rejected = false;
        try { trace.Observe([1]); } catch (ArgumentException) { rejected = true; }
        Require(rejected && trace.Samples == 4);
        Console.WriteLine("PASS: native structure sizes, failed reads stay unavailable, error counts aggregate, unchanged samples are distinct from movement, and axis ranges remain separate.");
        return 0;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

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
        if (values.Length != axisNames.Length) throw new ArgumentException("Axis count differs.");
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
        Samples++; if (changed) Changes++; return changed;
    }
    public bool RecordError(string key)
    {
        _errors.TryGetValue(key, out int count); _errors[key] = count + 1; return count == 0;
    }
    public string Summary()
    {
        string ranges = Samples == 0 ? "Unavailable" : string.Join("; ", axisNames.Select((name, i) => $"{name}={_minimum[i]}..{_maximum[i]}"));
        return $"Samples={Samples} Changes={Changes} {ranges} {string.Join("; ", _errors.Select(e => $"{e.Key} x{e.Value}"))}";
    }
    public static int SelfTest() => 0;
}
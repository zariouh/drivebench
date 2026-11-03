using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using DriveBench.Host;

internal static class Program
{
    private static readonly object LogGate = new();
    private static string? _logPath;

    private static int Main(string[] args)
    {
        bool selfTest = args is ["--self-test"];
        if (args.Length != 0 && !selfTest) return 2;
        if (!selfTest)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveBench");
                Directory.CreateDirectory(directory); _logPath = Path.Combine(directory, "diagnostic.log");
            }
            catch { }
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("UNHANDLED: " + e.ExceptionObject);
        if (!OperatingSystem.IsWindows()) { Log("Windows required."); return 3; }
        using var done = new ManualResetEventSlim();
        var watchdog = new Thread(() =>
        {
            if (done.Wait(TimeSpan.FromSeconds(15))) return;
            Log("TIMEOUT"); Environment.Exit(70);
        }) { IsBackground = true };
        watchdog.Start();
        try
        {
            if (selfTest) throw new FileNotFoundException("Self-test");
            ProbeController(); Log("PASS"); return 0;
        }
        catch (Exception ex) { Log("FAIL: " + ex); return selfTest ? 42 : 1; }
        finally { done.Set(); watchdog.Join(TimeSpan.FromSeconds(1)); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbeController()
    {
        using var backend = new VigemBackend();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            backend.Submit(default);
            if (backend.ReadbackMatches(default)) return;
            Thread.Sleep(100);
        }
        throw new IOException("XInput readback did not match.");
    }

    private static void Log(string text)
    {
        string line = $"{DateTimeOffset.Now:O} {text}";
        lock (LogGate) { Console.WriteLine(line); if (_logPath != null) try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { } }
    }
}
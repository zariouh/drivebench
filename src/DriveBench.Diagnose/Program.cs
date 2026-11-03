using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DriveBench.Host;

internal static class Program
{
    private static readonly object LogGate = new();
    private static string? _logPath;

    private static int Main(string[] args)
    {
        // Keep the entry point independent of ViGEm types. Loading the backend
        // happens in a non-inlined method inside the exception boundary below.
        bool selfTest = args is ["--self-test"];
        if (args.Length != 0 && !selfTest) return 2;
        if (!selfTest)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveBench");
                Directory.CreateDirectory(directory);
                _logPath = Path.Combine(directory, "diagnostic.log");
            }
            catch (Exception ex) { Console.Error.WriteLine("Could not create diagnostic log: " + ex.Message); }
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("UNHANDLED: " + e.ExceptionObject);
        Log("=== DriveBench diagnostic 0.1.1 ===");
        Log($"OS: {Environment.OSVersion}; process: {RuntimeInformation.ProcessArchitecture}; runtime: {RuntimeInformation.FrameworkDescription}");
        Log("This check installs nothing and sends only neutral controller input.");
        if (_logPath != null) Log("Log: " + _logPath);
        try
        {
            if (selfTest) RunSelfTestFailure();
            if (!OperatingSystem.IsWindows()) { Log("Windows is required for the controller check."); return 3; }
            using var done = new ManualResetEventSlim();
            // Independent timeout: this probe must not wait forever in native code.
            var watchdog = new Thread(() =>
            {
                if (done.Wait(TimeSpan.FromSeconds(15))) return;
                Log("TIMEOUT: controller probe did not finish within 15 seconds. Ending the neutral-only probe.");
                Environment.Exit(70);
            }) { IsBackground = true, Name = "DriveBench diagnostic timeout" };
            watchdog.Start();
            try
            {
                Log("STAGE: loading ViGEm client and connecting to the bus.");
                ProbeController();
                Log("PASS: virtual device was created, given neutral input, and disconnected.");
                return 0;
            }
            finally { done.Set(); watchdog.Join(TimeSpan.FromSeconds(1)); }
        }
        catch (Exception ex)
        {
            Log("FAIL: " + ex);
            return selfTest ? 42 : 1;
        }
        finally { Log("Diagnostic finished."); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbeController()
    {
        using var backend = new VigemBackend();
        Log("STAGE: virtual Xbox device connected; checking neutral XInput readback.");
        bool matched = false;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            backend.Submit(default);
            if (backend.ReadbackMatches(default)) { matched = true; break; }
            Thread.Sleep(100);
        }
        Log(matched ? "XInput neutral readback matched." : "XInput readback did not match within one second; game compatibility is not established.");
        Log("STAGE: disconnecting the neutral device.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunSelfTestFailure() => throw new FileNotFoundException("Intentional diagnostic exception-capture self-test.");

    private static void Log(string text)
    {
        string line = $"{DateTimeOffset.Now:O} {text}";
        lock (LogGate)
        {
            Console.WriteLine(line);
            if (_logPath == null) return;
            try { File.AppendAllText(_logPath, line + Environment.NewLine); }
            catch (Exception ex) { Console.Error.WriteLine("Log write failed: " + ex.Message); }
        }
    }
}

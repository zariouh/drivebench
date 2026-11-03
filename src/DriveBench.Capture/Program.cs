using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using DriveBench.Capture;

if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Run this companion on the Windows PC."); return 2; }
using var identity = WindowsIdentity.GetCurrent();
if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
{ Console.Error.WriteLine("Windows administrator approval is required to record NIC packet timestamps."); return 2; }

string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
if (string.IsNullOrWhiteSpace(desktop)) desktop = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
string folder = Path.Combine(desktop, "DriveBench-capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);
Directory.CreateDirectory(folder);
var runner = new WindowsCommands(Path.Combine(folder, "capture-command-log.txt"));
var capture = new CaptureSession(runner, folder, CaptureSession.NewFilterName(false));
string checkFolder=Path.Combine(folder,"recorder-check");Directory.CreateDirectory(checkFolder);
var recorderCheck=new CaptureSession(runner,checkFolder,CaptureSession.NewFilterName(true));
using var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };
int exitCode = 0;
try
{
    Console.WriteLine("DriveBench network capture 0.1.3");
    Console.WriteLine("Keep Receiver 0.3.5 and Android 0.3.4. Connect the phone normally first.");
    Console.WriteLine("Records existing UDP port 49173 at network interfaces; sends no packets.");
    Console.WriteLine("No firewall, driver, controller lease or Wi-Fi setting changes.");
    Console.WriteLine("Press ENTER to verify the recorder. Keep the phone connected and wait before driving.");
    Console.ReadLine();
    if(await CaptureSession.RecoverKnownLegacyFilter(runner))
        Console.WriteLine("Removed the verified leftover DriveBench recorder filter.");
    Console.WriteLine("RECORDER CHECK: observing existing UDP for five seconds. No packets are generated.");
    await recorderCheck.Start();
    await Task.Delay(TimeSpan.FromSeconds(5),quit.Token);
    await recorderCheck.Finish();
    Console.WriteLine($"Recorder verified: {recorderCheck.PacketRecords} timestamped packet records exported.");
    await capture.Start();
    Console.WriteLine("RECORDING. Resume on the phone if needed, then drive. After the first pause, return here and press ENTER.");
    Console.WriteLine("Leave this window open; capture stops automatically after five minutes.");
    await Task.WhenAny(Console.In.ReadLineAsync(), Task.Delay(TimeSpan.FromMinutes(5), quit.Token));
    await capture.Finish();
}
catch (Exception ex) { exitCode = 1; Console.Error.WriteLine(ex.Message); File.WriteAllText(Path.Combine(folder, "capture-error.txt"), ex.ToString()); }
finally
{
    foreach(var owned in new[]{capture,recorderCheck})
    {
        if(!owned.OwnsCapture) continue;
        try { await owned.Finish(); }
        catch (Exception ex) { exitCode = 1; File.AppendAllText(Path.Combine(folder, "capture-error.txt"), "\nCleanup: " + ex.Message); }
    }
    bool running=capture.OwnsCapture || recorderCheck.OwnsCapture;
    File.WriteAllText(Path.Combine(folder, "capture-info.txt"), $"DriveBench Capture0.1.3\nFinished local time: {DateTimeOffset.Now:O}\nWindows: {Environment.OSVersion}\nFilter: UDP49173\nComponent: NICs; application processing and over-air timestamps are not measured\nPer-packet prefix:256bytes; circular file ceiling512MB per stage (disk limit, not RAM limit); recorder check5s, main requested duration<=5min\nTimestamped packet records: recorder-check={recorderCheck.PacketRecords}, main={capture.PacketRecords}\nRaw ETL retained for capture-loss/drop metadata; PCAPNG may duplicate a packet across virtual/physical NICs.\nCapture still owned/running: {running}\nCleanup: check={recorderCheck.CleanupNote ?? "none"}; main={capture.CleanupNote ?? "none"}\nResult: {exitCode}\n");
    if (!running)
    {
        string zip = folder + ".zip";
        try { ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Fastest, false); Console.WriteLine("Attach this ZIP in chat:\n" + zip); }
        catch (Exception ex) { exitCode = 1; Console.Error.WriteLine("ZIP could not be created: " + ex.Message + "\nFiles remain in " + folder); }
    }
    else Console.Error.WriteLine("Packet Monitor did not confirm stopping. Run 'pktmon stop' in this same administrator session before closing. Output: " + folder);
    Console.WriteLine("Press ENTER to close."); Console.ReadLine();
}
return exitCode;

sealed class WindowsCommands(string log) : ICommandRunner
{
    public async Task<CommandResult> Run(string executable, params string[] arguments)
    {
        if (executable is not ("pktmon.exe" or "logman.exe")) throw new ArgumentException("Unexpected system tool");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, executable))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + executable);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await outputTask + await errorTask;
        // Trace-session inventory is inspected in memory, not saved to the shareable report.
        string recorded = executable == "logman.exe" ? "Trace inventory checked; output omitted.\n" : output;
        await File.AppendAllTextAsync(log, $"{DateTimeOffset.Now:O} {executable} {string.Join(' ', arguments)}\nexit={process.ExitCode}\n{recorded}\n");
        return new(process.ExitCode, output);
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using DriveBench.Core;
using DriveBench.Host;

if (args.Length != 2 || !args[0].StartsWith("DriveBench-", StringComparison.Ordinal) ||
    args[0].Length > 80 || args[1] is not ("dry-run" or "vigem")) return 2;
string pipeName = args[0];
string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveBench", "host.log");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
void Log(string text) { try { File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}"); } catch { } }
try
{
    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await pipe.WaitForConnectionAsync(lifetime.Token);
    lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
    using IBackend backend = args[1] == "vigem" ? new VigemBackend() : new DryRun();
    var lease = new InputLease(Stopwatch.Frequency);
    object gate = new();
    var receiver = Task.Run(async () =>
    {
        byte[] bytes = new byte[InputFrame.Size];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await pipe.ReadExactlyAsync(bytes, lifetime.Token);
                var frame = InputFrame.Decode(bytes);
                lock (gate) lease.Accept(frame, Stopwatch.GetTimestamp());
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
        { lock (gate) lease.Disconnect(); lifetime.Cancel(); }
    });
    // Separate feedback writer: a blocked pipe write cannot block the device watchdog.
    HostStatus latestStatus = default;
    var feedback = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                HostStatus status; lock (gate) status = latestStatus;
                if (status.CompletedTicks == 0) continue;
                await pipe.WriteAsync(status.Encode(), lifetime.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { lifetime.Cancel(); }
    });
    Log($"Started {args[1]} on {Environment.OSVersion}; {RuntimeInformation.OSArchitecture}");
    try
    {
        using var outputTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
        while (await outputTimer.WaitForNextTickAsync(lifetime.Token))
        {
            // Do not wait for network/UI work. Re-check absolute deadline at report time.
            PadReport report; ulong sequence; uint epoch; LeaseState state;
            lock (gate)
            {
                report = lease.Sample(Stopwatch.GetTimestamp()); state = lease.State; epoch = lease.Epoch;
                sequence = state == LeaseState.Armed ? lease.CurrentSequence : 0;
            }
            backend.Submit(report);
            bool matched = backend.ReadbackMatches(report);
            long completed = Stopwatch.GetTimestamp();
            lock (gate)
            {
                lease.Submitted(sequence);
                latestStatus = new(completed, lease.LastAppliedSequence, epoch, report, state, backend.Real, matched);
            }
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        backend.Submit(default);
        lifetime.Cancel();
        try { await Task.WhenAll(receiver, feedback).WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
    }
    Log("Neutral submitted; closing device"); return 0;
}
catch (Exception ex) { Log(ex.ToString()); Console.Error.WriteLine(ex.Message); return 1; }

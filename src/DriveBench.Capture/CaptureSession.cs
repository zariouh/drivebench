using System.Text.RegularExpressions;

namespace DriveBench.Capture;

public sealed record CommandResult(int ExitCode, string Output);
public interface ICommandRunner
{
    Task<CommandResult> Run(string executable, params string[] arguments);
}

/// <summary>Records existing UDP at NICs. No socket creation, input generation or application changes.</summary>
public sealed class CaptureSession(ICommandRunner runner, string folder, string filterName, Func<string,long>? inspectPackets=null)
{
    private bool _filterAdded;
    public bool OwnsCapture { get; private set; }
    public string EtlPath => Path.Combine(folder, "DriveBench.etl");
    public string PcapPath => Path.Combine(folder, "DriveBench.pcapng");
    public string? CleanupNote { get; private set; }
    public long PacketRecords { get; private set; } = -1;
    private async Task<CommandResult> Pktmon(params string[] args) => await runner.Run("pktmon.exe", args);
    private static void RequireSuccess(CommandResult result, string stage)
    {
        if (result.ExitCode != 0) throw new IOException($"{stage} failed ({result.ExitCode}). See capture-command-log.txt.");
    }
    // Stay below the native table's truncation width; never infer ownership from a prefix.
    public static string NewFilterName(bool check) => "DriveBench_" + (check ? "C" : "M") + Guid.NewGuid().ToString("N")[..7];
    public static bool KnownLegacyFilter(string output) => Regex.IsMatch(output,
        @"\APacket Filters:\s*#\s+Name\s+Protocol\s+Port\s+-\s+-+\s+-+\s+-+\s+1\s+DriveBench_10596_check_e\.\.\.\s+UDP\s+49173\s*\z");
    public static async Task<bool> RecoverKnownLegacyFilter(ICommandRunner runner)
    {
        // This exact sole row was photographed after our old check failed cleanup.
        // Unknown names, extra rows/columns, and active traces remain untouched.
        var listing = await runner.Run("pktmon.exe", "filter", "list");
        RequireSuccess(listing, "Inspect previous recorder filter");
        if (!KnownLegacyFilter(listing.Output)) return false;
        var loggers = await runner.Run("logman.exe", "query", "-ets");
        RequireSuccess(loggers, "Check previous recorder is stopped");
        if (Regex.IsMatch(loggers.Output, @"\bPktMon\b", RegexOptions.IgnoreCase))
            throw new IOException("A Packet Monitor capture is active. Its filters were left unchanged.");
        listing = await runner.Run("pktmon.exe", "filter", "list");
        RequireSuccess(listing, "Recheck previous recorder filter");
        if (!KnownLegacyFilter(listing.Output)) throw new IOException("Previous recorder filters changed; left unchanged.");
        RequireSuccess(await runner.Run("pktmon.exe", "filter", "remove"), "Remove verified leftover recorder filter");
        listing = await runner.Run("pktmon.exe", "filter", "list");
        RequireSuccess(listing, "Verify leftover filter removal");
        if (!EmptyFilters(listing.Output)) throw new IOException("Filters are not empty after cleanup; capture was not started.");
        return true;
    }
    // Fail closed on unknown/localized output rather than deleting unrelated global filters.
    public static bool EmptyFilters(string output)
    {
        var lines = output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        bool header = lines.Length > 0 && lines[0].Equals("Packet Filters:", StringComparison.OrdinalIgnoreCase);
        if (header) lines = lines.Skip(1).ToArray();
        if (lines.Length != 1) return false;
        // Windows22621 reports this exact header + None. A bare None is ambiguous.
        return (header && lines[0].Equals("None", StringComparison.OrdinalIgnoreCase)) ||
            Regex.IsMatch(lines[0], @"\ANo filters(?:\s+(?:are\s+)?(?:defined|configured|set|found))?[.!]?\z", RegexOptions.IgnoreCase);
    }
    public static bool SoleFilter(string output, string name)
    {
        var rows = Regex.Matches(output, @"(?m)^\s*\d+\s+(\S+)");
        return rows.Count == 1 && rows[0].Groups[1].Value == name;
    }
    public async Task Start()
    {
        if (OwnsCapture || _filterAdded) throw new InvalidOperationException("Capture session already used");
        if (!Regex.IsMatch(filterName, @"\ADriveBench_[a-zA-Z0-9_]{1,8}\z")) throw new ArgumentException("Private filter name must be at most 19 characters to avoid native truncation");
        var loggers = await runner.Run("logman.exe", "query", "-ets");
        RequireSuccess(loggers, "Check active trace sessions");
        if (Regex.IsMatch(loggers.Output, @"\bPktMon\b", RegexOptions.IgnoreCase))
            throw new IOException("Another Packet Monitor capture is active. It was left unchanged.");
        var filters = await Pktmon("filter", "list");
        RequireSuccess(filters, "Read capture filters");
        if (!EmptyFilters(filters.Output))
            throw new IOException("Packet Monitor has existing filters or an unrecognized listing. They were left unchanged. Send capture-command-log.txt.");
        try
        {
            RequireSuccess(await Pktmon("filter", "add", filterName, "-t", "UDP", "-p", "49173"), "Add DriveBench-only UDP filter");
            _filterAdded = true;
            var owned = await Pktmon("filter", "list");
            RequireSuccess(owned, "Confirm capture scope");
            if (!SoleFilter(owned.Output, filterName)) throw new IOException("Capture filters changed; capture was not started.");
            RequireSuccess(await Pktmon("start", "--capture", "--comp", "nics", "--pkt-size", "256",
                "--file-size", "512", "--log-mode", "circular", "--file-name", EtlPath), "Start NIC capture");
            OwnsCapture = true;
        }
        catch { await Cleanup(); throw; }
    }
    public async Task Finish()
    {
        if (!OwnsCapture) { await Cleanup(); return; }
        // Preserve raw ETL and loss information as well as the convenient PCAPNG export.
        await Pktmon("status", "--buffer-info");
        await Pktmon("counters");
        var stopped = await Pktmon("stop");
        RequireSuccess(stopped, "Stop this capture");
        OwnsCapture = false;
        try
        {
            RequireSuccess(await Pktmon("etl2pcap", EtlPath, "--out", PcapPath), "Export packet timestamps");
            await Pktmon("etl2txt", EtlPath, "--stats-only", "--out", Path.Combine(folder, "capture-statistics.txt"));
            PacketRecords=(inspectPackets ?? PcapngReader.Count)(PcapPath);
            if(PacketRecords==0) throw new IOException("Export contains ZERO timestamped packet records. The recorder did not produce usable packet timing; see the generated ZIP. This is not evidence of zero network traffic.");
        }
        finally { await Cleanup(); }
    }
    private async Task Cleanup()
    {
        if (!_filterAdded || OwnsCapture) return;
        var filters = await Pktmon("filter", "list");
        if (filters.ExitCode == 0 && SoleFilter(filters.Output, filterName))
        {
            var result = await Pktmon("filter", "remove");
            if (result.ExitCode == 0) { _filterAdded = false; return; }
        }
        CleanupNote = "Temporary filter was left in place because exclusive ownership could not be verified. No unrelated filters were removed.";
    }
}

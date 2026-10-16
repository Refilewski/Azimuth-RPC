using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AzimuthRPC;

internal sealed record DebugStatus(GameSnapshot Game, string Process, string Mapping, string Discord, DiscordPresence Presence, int? ProcessId = null, DateTime ProcessStartUtc = default);

internal sealed class DebugRuntime : IAsyncDisposable
{
    private AppConfig _options;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private Process? _process;
    private ExternalProcessMemoryReader? _memory;
    private GameSnapshotReader? _reader;
    private ITelemetrySource? _hooks;
    private PeIdentity _pe = PeIdentity.Fh2XboxOne10064;
    private ulong _base;
    private DateTime _started;
    private DateTime _nextAttach, _nextMemory, _nextHook, _nextDiscord;
    private GameSnapshot _memorySnapshot = GameSnapshot.Unavailable("Waiting for Azimuth");
    private DiscordIpcClient? _discord;
    private string? _sentFingerprint;
    private string _discordStatus = "Disconnected";
    private DebugStatus _status;
    private int _disposed;
    internal event Action<DebugStatus>? Changed;
    internal event Action<string>? Log;
    internal DebugRuntime(AppConfig options)
    {
        _options = options;
        _status = new(_memorySnapshot, "Waiting for Azimuth", "Not attached", "Disconnected", DiscordPresenceBuilder.Build(_memorySnapshot, options, DateTime.UtcNow));
    }
    internal DebugStatus Current => Volatile.Read(ref _status);
    internal void UpdateConfig(AppConfig options) => Volatile.Write(ref _options, options);
    internal void Start() => _loop ??= Task.Run(RunAsync);
    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            do
            {
                try { await TickAsync(_stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception e) { _memorySnapshot = GameSnapshot.Unavailable(e.Message); Log?.Invoke(e.Message); Publish(_memorySnapshot); }
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task TickAsync(CancellationToken token)
    {
        var options = Volatile.Read(ref _options); var now = DateTime.UtcNow;
        if (options.Offline) { Publish(GameSnapshot.Unavailable("Offline debug mode; memory, hooks and Discord disabled")); return; }
        if (_process is not null && Exited(_process)) await DetachAsync();
        if (_process is null && now >= _nextAttach)
        {
            _nextAttach = now.AddSeconds(2);
            var found = FindProcess(options.ProcessName);
            if (found is not null)
            {
                _process = found;
                try { _started = found.StartTime.ToUniversalTime(); _memory = new(found.Id); _reader = new(_memory, Fh2MainlandLocationResolver.Instance); }
                catch { await DetachAsync(); throw; }
                _nextMemory = DateTime.MinValue;
            }
        }
        if (_process is not null && _memory is not null && now >= _nextMemory)
        {
            _nextMemory = now.AddSeconds(1);
            if (_base != 0 && !MappedImageLocator.Matches(_memory, _base, _pe)) ResetMapping();
            if (_base == 0) _base = MappedImageLocator.Find(_memory, _memory, _pe);
            var previousRoots = _memorySnapshot.Roots;
            _memorySnapshot = _base == 0 ? GameSnapshot.Unavailable("Waiting for supported FH2 1.0.0.64 image") : _reader!.Capture(_base, true);
            if (!_memorySnapshot.Attached || previousRoots.AudioEngine != 0 && previousRoots != _memorySnapshot.Roots) ResetHooks();
        }
        if (_memorySnapshot.Attached && _process is not null && _hooks is null && now >= _nextHook)
        {
            _nextHook = now.AddSeconds(5);
            var candidate = new Fh2TelemetryHook(_process, _base); var installed = candidate.TryInstall();
            if (installed.Success) _hooks = candidate;
            else { candidate.Dispose(); Log?.Invoke(installed.Status); }
        }
        var telemetry = _hooks?.Read() ?? Fh2TelemetrySample.Unavailable("Telemetry hooks unavailable");
        var snapshot = GameStateInterpreter.Apply(_memorySnapshot, telemetry, _hooks?.Installed == true);
        var presence = DiscordPresenceBuilder.Build(snapshot, options, _started == default ? now : _started);
        if (_discord is not null && (!_discord.IsConnected || _discordApplicationId != options.ApplicationId)) await DisconnectAsync();
        if (_process is not null && !string.IsNullOrWhiteSpace(options.ApplicationId) && _discord is null && now >= _nextDiscord)
        {
            _nextDiscord = now.AddSeconds(5); var client = new DiscordIpcClient(options.ApplicationId);
            try
            {
                if (await client.ConnectAsync(token)) { _discord = client; _discordApplicationId = options.ApplicationId; _sentFingerprint = null; _discordStatus = "Connected"; }
                else { await client.DisposeAsync(); _discordStatus = "Discord not detected"; }
            }
            catch { await client.DisposeAsync(); throw; }
        }
        if (_discord?.IsConnected == true && _sentFingerprint != presence.Fingerprint)
        {
            await _discord.SetActivityAsync(presence.Options, presence.SessionStartUtc, presence.Details, presence.State, token);
            _sentFingerprint = presence.Fingerprint;
        }
        if (string.IsNullOrWhiteSpace(options.ApplicationId)) _discordStatus = "Set Discord Application ID";
        Publish(snapshot, presence);
    }
    private string? _discordApplicationId;
    private void Publish(GameSnapshot snapshot, DiscordPresence? presence = null)
    {
        var next = new DebugStatus(snapshot, _process is null ? "Waiting for Azimuth" : $"azimuth · PID {_process.Id}", snapshot.Attached ? $"0x{snapshot.GameBase:X} ({(_pe.PreferredImageBase == snapshot.GameBase ? "preferred" : "relocated")})" : snapshot.Error ?? "Not attached", _discordStatus, presence ?? DiscordPresenceBuilder.Build(snapshot, Volatile.Read(ref _options), _started), _process?.Id, _started);
        Volatile.Write(ref _status, next); Changed?.Invoke(next);
    }
    private void ResetHooks() { _hooks?.Dispose(); _hooks = null; _nextHook = DateTime.MinValue; }
    private void ResetMapping() { ResetHooks(); _base = 0; _reader = _memory is null ? null : new(_memory, Fh2MainlandLocationResolver.Instance); _memorySnapshot = GameSnapshot.Unavailable("Mapping invalidated"); }
    private async Task DetachAsync()
    {
        ResetHooks(); _memory?.Dispose(); _memory = null; _reader = null; _pe = PeIdentity.Fh2XboxOne10064; _base = 0;
        _process?.Dispose(); _process = null; _memorySnapshot = GameSnapshot.Unavailable("Waiting for Azimuth");
        if (_discord?.IsConnected == true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _discord.ClearActivityAsync(timeout.Token); } catch (Exception e) { Log?.Invoke(e.Message); }
        }
        await DisconnectAsync();
    }
    private async Task DisconnectAsync() { if (_discord is not null) await _discord.DisposeAsync(); _discord = null; _discordApplicationId = null; _sentFingerprint = null; _discordStatus = "Disconnected"; _nextDiscord = DateTime.MinValue; }
    private static bool Exited(Process p) { try { return p.HasExited; } catch { return true; } }
    private static Process? FindProcess(string name)
    {
        Process[] all = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
        Process? selected = null; DateTime newest = DateTime.MinValue;
        foreach (var p in all) { try { var t = p.StartTime; if (t > newest) { selected?.Dispose(); selected = p; newest = t; } else p.Dispose(); } catch { p.Dispose(); } }
        return selected;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync(); if (_loop is not null) await _loop;
        await DetachAsync(); _stop.Dispose();
    }
}

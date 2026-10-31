using System.Runtime.InteropServices;

namespace BenqBridge.Ddc;

public sealed record OptionDto(uint Value, string Label);

public sealed record FeatureDto(
    string Hex,
    int Code,
    string Name,
    string Category,
    string Kind,
    string? Unit,
    bool Writable,
    uint Max,
    uint? Value,
    List<OptionDto>? Options);

public sealed record MonitorDto(
    string Id,
    string Device,
    string Name,
    string Model,
    bool Primary,
    /// <summary>A physical DDC/CI channel exists (allows reads/writes).</summary>
    bool DdcChannel,
    /// <summary>DDC channel exists <em>and</em> the monitor reported usable features.</summary>
    bool Supported,
    int Width,
    int Height,
    string? MccsVersion,
    int PhysicalMonitors,
    string? Capabilities,
    List<string> Categories,
    List<FeatureDto> Features);

public sealed class MonitorRegistry : IAsyncDisposable
{
    private readonly ILogger<MonitorRegistry> _log;
    private readonly Dictionary<string, DdcDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MonitorRegistry(ILogger<MonitorRegistry> log) => _log = log;

    public int PollIntervalMs { get; set; } = 1500;
    public volatile bool ActiveClients;

    /// <summary>Raised (on the poll thread) after any monitor state may have changed.</summary>
    public event Action? StateChanged;

    public IReadOnlyList<DdcDevice> Devices
    {
        get { lock (_devices) return _devices.Values.ToList(); }
    }

    public DdcDevice? Find(string id)
    {
        lock (_devices)
            return _devices.Values.FirstOrDefault(d =>
                string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.DeviceName, id, StringComparison.OrdinalIgnoreCase));
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        Rescan();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var lastRecoveryAttempt = DateTime.UtcNow;

        while (!token.IsCancellationRequested)
        {
            try
            {
                Rescan();

                // A monitor that answered enumeration but not the capability request (asleep,
                // OSD open, cable settling) should be retried now and then rather than staying
                // permanently blank in the UI.
                var needsRecovery = Devices.Any(d => d.IsSupported && d.Features.Count == 0);
                if (needsRecovery && (DateTime.UtcNow - lastRecoveryAttempt).TotalSeconds >= 10)
                {
                    lastRecoveryAttempt = DateTime.UtcNow;
                    var recovered = false;

                    foreach (var device in Devices.Where(d => d.IsSupported && d.Features.Count == 0))
                    {
                        token.ThrowIfCancellationRequested();
                        if (device.Refresh())
                        {
                            _log.LogInformation("Recovered capability string for {Id} ({Count} features)",
                                device.Id, device.Features.Count);
                            device.ReadAll();
                            recovered = true;
                        }
                    }

                    if (recovered) StateChanged?.Invoke();
                }

                if (ActiveClients)
                {
                    var changed = false;
                    foreach (var device in Devices)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!device.IsSupported || device.Features.Count == 0) continue;
                        if (device.Poll()) changed = true;
                    }

                    if (changed) StateChanged?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Monitor poll loop failed");
            }

            try { await Task.Delay(PollIntervalMs, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Re-enumerate displays, creating/disposing device wrappers as needed.</summary>
    public void Rescan()
    {
        var topologyChanged = false;
        _mutation.Wait();
        try
        {
            var targets = Enumerate();
            var names = targets.Select(t => t.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var stale in _devices.Keys.Where(k => !names.Contains(k)).ToList())
            {
                _log.LogInformation("Monitor {Id} disconnected", _devices[stale].Id);
                _devices[stale].Dispose();
                _devices.Remove(stale);
                topologyChanged = true;
            }

            foreach (var target in targets)
            {
                if (_devices.ContainsKey(target.DeviceName)) continue;

                topologyChanged = true;
                var device = new DdcDevice(target, _log);
                if (!device.IsSupported)
                {
                    _log.LogInformation("Skipping {Device}: no DDC/CI physical monitor handle", target.DeviceName);
                    device.Dispose();
                    _devices[target.DeviceName] = device; // still listed, but unsupported
                    continue;
                }

                if (!device.Refresh())
                    _log.LogWarning("Could not read capability string from {Device} ({Desc})", target.DeviceName, target.Description);
                else
                    device.ReadAll();

                _devices[target.DeviceName] = device;
            }
        }
        finally
        {
            _mutation.Release();
        }

        // A display appearing or disappearing is exactly the kind of change a connected
        // UI must hear about; the value-diff poll would not notice an empty -> full list.
        if (topologyChanged) StateChanged?.Invoke();
    }

    public void RefreshAll()
    {
        foreach (var device in Devices)
        {
            if (!device.IsSupported) continue;
            device.Refresh();
            device.ReadAll();
        }

        StateChanged?.Invoke();
    }

    public List<MonitorDto> Snapshot()
    {
        var result = new List<MonitorDto>();

        foreach (var device in Devices.OrderBy(d => d.DeviceName))
        {
            var features = device.Features
                .Select(f => new FeatureDto(
                    f.Hex,
                    f.Code,
                    f.Name,
                    f.Category,
                    f.Kind,
                    f.Unit,
                    f.Writable,
                    f.Max,
                    f.Value,
                    (f.Options ?? VcpCatalog.AutoOptions(f.Code, f.Kind, f.Max))
                        ?.Select(o => new OptionDto(o.Value, o.Label)).ToList()))
                .ToList();

            result.Add(new MonitorDto(
                device.Id,
                device.DeviceName,
                device.Description,
                device.Model,
                device.Primary,
                device.IsSupported,
                device.IsSupported && device.Features.Count > 0,
                device.Width,
                device.Height,
                device.Capabilities?.MccsVersion,
                device.PhysicalMonitorCount,
                device.CapabilitiesRaw,
                VcpCatalog.CategoryOrder.ToList(),
                features));
        }

        return result;
    }

    private static List<DisplayTarget> Enumerate()
    {
        var list = new List<DisplayTarget>();

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hm, _, _, _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(hm, ref mi))
                return true;

            var physical = Array.Empty<Native.PHYSICAL_MONITOR>();
            if (Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out var count) && count > 0)
            {
                var buffer = new Native.PHYSICAL_MONITOR[count];
                if (Native.GetPhysicalMonitorsFromHMONITOR(hm, count, buffer))
                    physical = buffer;
                else
                    Native.DestroyPhysicalMonitors(count, buffer);
            }

            var description = physical.Length > 0 ? physical[0].szPhysicalMonitorDescription : "Display";
            list.Add(new DisplayTarget(
                hm,
                mi.szDevice ?? string.Empty,
                description,
                (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                mi.rcMonitor.Right - mi.rcMonitor.Left,
                mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                physical));

            return true;
        }, IntPtr.Zero);

        return list;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_loop is not null)
            {
                try { await _loop; } catch { /* ignored */ }
            }

            _cts.Dispose();
        }

        foreach (var device in Devices) device.Dispose();
        _mutation.Dispose();
    }
}

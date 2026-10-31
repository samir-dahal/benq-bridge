using System.Text;

namespace BenqBridge.Ddc;

/// <summary>
/// One DDC/CI-capable display. All traffic is serialised through a semaphore because
/// DDC is slow (~30-150 ms per transaction) and must not be issued concurrently.
/// </summary>
public sealed class DdcDevice : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Native.PHYSICAL_MONITOR[] _physical;
    private readonly object _featuresLock = new();
    private List<VcpFeature> _features = new();
    private volatile bool _disposed;

    /// <summary>Codes polled for live state. Chosen to cover everything a user can change.</summary>
    private static readonly byte[] PollCodes =
    {
        0x10, 0x12, 0x14, 0x19, 0x52, 0x60, 0x62, 0x72, 0x86, 0x87, 0x8D, 0xDC, 0xE2
    };

    public DdcDevice(DisplayTarget target, ILogger logger)
    {
        DeviceName = target.DeviceName;
        Id = target.DeviceName.Replace(@"\\.\", string.Empty);
        Description = target.Description;
        Primary = target.Primary;
        Width = target.Width;
        Height = target.Height;
        _physical = target.PhysicalMonitors;
        Handle = _physical.Length > 0 ? _physical[0].hPhysicalMonitor : IntPtr.Zero;
        Logger = logger;
    }

    public ILogger Logger { get; }

    public string Id { get; }
    public string DeviceName { get; }
    public string Description { get; }
    public bool Primary { get; }
    public int Width { get; }
    public int Height { get; }
    public IntPtr Handle { get; }

    public Capabilities? Capabilities { get; private set; }
    public string? CapabilitiesRaw { get; private set; }
    public string Model => Capabilities?.Model ?? Description;
    public bool IsSupported => _physical.Length > 0;
    public int PhysicalMonitorCount => _physical.Length;

    public IReadOnlyList<VcpFeature> Features
    {
        get { lock (_featuresLock) return _features; }
    }

    /// <summary>Reads the capability string and builds the feature list. Values are filled in by Poll().</summary>
    public bool Refresh()
    {
        if (!IsSupported) return false;

        var raw = ReadCapabilities();
        if (raw is null) return false;

        CapabilitiesRaw = raw;
        var caps = Capabilities.Parse(raw);
        Capabilities = caps;

        // Keep any values we already know so the UI does not flicker.
        Dictionary<byte, uint> previous;
        lock (_featuresLock) previous = _features.Where(f => f.Value.HasValue).ToDictionary(f => f.Code, f => f.Value!.Value);

        var list = new List<VcpFeature>();
        foreach (var vcp in caps.Vcps)
        {
            var feature = VcpCatalog.Build(vcp.Code, vcp.SubValues);
            if (previous.TryGetValue(vcp.Code, out var known)) feature.Value = known;
            list.Add(feature);
        }

        list.Sort((a, b) =>
        {
            var c = VcpCatalog.CategoryRank(a.Category).CompareTo(VcpCatalog.CategoryRank(b.Category));
            return c != 0 ? c : a.Code.CompareTo(b.Code);
        });

        lock (_featuresLock) _features = list;

        Logger.LogInformation("Monitor {Id} ({Model}) reports {Count} VCP codes", Id, Model, list.Count);
        return true;
    }

    public string? ReadCapabilities()
    {
        var sb = new StringBuilder(1024);
        _gate.Wait();
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (Native.CapabilitiesRequestAndCapabilitiesReply(Handle, sb, 1024))
                {
                    var text = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }

                Thread.Sleep(100);
                sb.Clear();
            }
        }
        finally
        {
            _gate.Release();
        }

        return null;
    }

    public (uint Type, uint Current, uint Maximum)? Read(byte code)
    {
        if (!IsSupported) return null;

        _gate.Wait();
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (Native.GetVCPFeatureAndVCPFeatureReply(Handle, code, out var type, out var cur, out var max))
                    return (type, cur, max);

                Thread.Sleep(50);
            }
        }
        finally
        {
            _gate.Release();
        }

        return null;
    }

    public bool Write(byte code, uint value)
    {
        if (!IsSupported) return false;

        _gate.Wait();
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (Native.SetVCPFeature(Handle, code, value)) return true;
                Thread.Sleep(50);
            }
        }
        finally
        {
            _gate.Release();
        }

        return false;
    }

    /// <summary>Refresh the values of the polled features. Returns true when anything moved.</summary>
    public bool Poll()
    {
        if (!IsSupported) return false;

        var changed = false;
        var targets = Features.Where(f => PollCodes.Contains(f.Code)).ToList();

        foreach (var feature in targets)
        {
            if (_disposed) break;

            var reading = Read(feature.Code);
            if (reading is null) continue;

            feature.Max = Math.Max(feature.Max, reading.Value.Maximum);
            if (feature.Value != reading.Value.Current)
            {
                feature.Value = reading.Value.Current;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Read every advertised code once (used on first connect / manual refresh).</summary>
    public void ReadAll()
    {
        foreach (var feature in Features)
        {
            if (_disposed || feature.Kind == "action") continue;
            var reading = Read(feature.Code);
            if (reading is null) continue;
            feature.Value = reading.Value.Current;
            if (reading.Value.Maximum > 0) feature.Max = reading.Value.Maximum;
        }
    }

    public VcpFeature? Find(byte code)
    {
        lock (_featuresLock) return _features.FirstOrDefault(f => f.Code == code);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_physical.Length > 0)
        {
            try { Native.DestroyPhysicalMonitors((uint)_physical.Length, _physical); }
            catch (Exception ex) { Logger.LogWarning(ex, "Failed to release physical monitors for {Id}", Id); }
        }

        _gate.Dispose();
    }
}

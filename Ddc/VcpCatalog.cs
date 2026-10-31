namespace BenqBridge.Ddc;

public sealed record VcpOption(uint Value, string Label);

public sealed class VcpFeature
{
    public byte Code { get; init; }
    public string Hex => Code.ToString("X2");
    public string Name { get; init; } = "";
    public string Category { get; init; } = "Advanced";
    public string Kind { get; init; } = "continuous"; // continuous | discrete | toggle | action | info
    public string? Unit { get; init; }
    public bool Writable { get; init; }
    public uint Max { get; set; }
    public uint? Value { get; set; }
    public List<VcpOption>? Options { get; init; }

    public VcpFeature Clone() => new()
    {
        Code = Code,
        Name = Name,
        Category = Category,
        Kind = Kind,
        Unit = Unit,
        Writable = Writable,
        Max = Max,
        Value = Value,
        Options = Options
    };
}

/// <summary>
/// Maps raw MCCS VCP codes onto human names/semantics. Unknown codes still surface
/// in the UI (as raw controls) so a monitor is never limited by this table.
/// </summary>
public static class VcpCatalog
{
    public static readonly string[] CategoryOrder =
        { "Picture", "Color", "Eye Care", "Audio", "Input", "System", "Advanced" };

    private sealed record Meta(string Name, string Category, string Kind, string? Unit = null,
        Dictionary<uint, string>? Labels = null, bool Writable = true);

    private static readonly Dictionary<byte, Meta> Known = new()
    {
        [0x02] = new("New Control Value", "System", "info", Writable: false),
        [0x04] = new("Restore Factory Defaults", "System", "action"),
        [0x05] = new("Restore Luminance/Contrast Defaults", "System", "action"),
        [0x08] = new("Restore Color Defaults", "System", "action"),

        [0x10] = new("Brightness", "Picture", "continuous", "%"),
        [0x12] = new("Contrast", "Picture", "continuous", "%"),
        [0x72] = new("Gamma", "Picture", "discrete",
            Labels: new() { [80] = "Step 1 (1.6)", [100] = "Step 2 (2.0)", [120] = "Step 3 (2.4)", [140] = "Step 4 (2.8)", [160] = "Step 5 (3.2)" }),
        [0x86] = new("Display Scaling", "Picture", "discrete"),
        [0x87] = new("Sharpness", "Picture", "continuous"),
        [0xDC] = new("Picture Mode", "Picture", "discrete",
            Labels: new() { [0] = "Standard", [1] = "Care Mode", [40] = "Game", [50] = "Cinema" }),

        [0x14] = new("Color Preset", "Color", "discrete",
            Labels: new() { [4] = "5000 K", [5] = "6500 K", [8] = "9300 K", [11] = "User" }),
        [0x16] = new("Red Gain", "Color", "continuous"),
        [0x18] = new("Green Gain", "Color", "continuous"),
        [0x1A] = new("Blue Gain", "Color", "continuous"),
        [0x0B] = new("Color Temperature (raw)", "Color", "info", Writable: false),

        [0x19] = new("Low Blue Light", "Eye Care", "discrete"),
        [0x52] = new("Active Control", "Eye Care", "continuous"),
        [0xE2] = new("BI (Auto Brightness)", "Eye Care", "toggle",
            Labels: new() { [0] = "Off", [255] = "On" }),

        [0x62] = new("Volume", "Audio", "continuous"),
        [0x8D] = new("Mute", "Audio", "toggle", Labels: new() { [1] = "Muted", [2] = "Unmuted" }),

        [0x60] = new("Input Source", "Input", "discrete",
            Labels: new() { [15] = "DisplayPort", [17] = "HDMI-1", [18] = "HDMI-2" }),

        [0xC1] = new("Version Word 1", "System", "info", Writable: false),
        [0xC2] = new("Version Word 2", "System", "info", Writable: false),
        [0xC8] = new("Version Word 3", "System", "info", Writable: false),
        [0xC9] = new("Firmware Version", "System", "info", Writable: false),
        [0xD2] = new("Internal 0xD2", "System", "info", Writable: false),
        [0xDF] = new("Internal 0xDF", "System", "info", Writable: false),
        [0xE1] = new("Internal 0xE1", "Advanced", "info", Writable: false),
        [0xEA] = new("BenQ 0xEA", "Advanced", "discrete"),
        [0xEB] = new("BenQ 0xEB", "Advanced", "discrete"),
        [0xEE] = new("BenQ 0xEE", "Advanced", "discrete"),
        [0xF0] = new("BenQ 0xF0", "Advanced", "discrete"),
        [0xF1] = new("BenQ 0xF1", "Advanced", "discrete"),
        [0xF8] = new("BenQ 0xF8", "Advanced", "discrete"),
        [0xFD] = new("BenQ 0xFD", "Advanced", "discrete"),
    };

    public static VcpFeature Build(byte code, IReadOnlyList<uint>? subs, uint max = 0)
    {
        if (Known.TryGetValue(code, out var meta))
        {
            var kind = meta.Kind;

            // A toggle advertised with sub-values is still a toggle.
            if (kind == "toggle" && subs is { Count: > 0 } && meta.Labels is not null)
                kind = "toggle";

            var options = BuildOptions(code, kind, subs, meta.Labels, max);

            return new VcpFeature
            {
                Code = code,
                Name = meta.Name,
                Category = meta.Category,
                Kind = kind,
                Unit = meta.Unit,
                Writable = meta.Writable && kind != "info",
                Max = max,
                Options = options
            };
        }

        // Unknown / unimplemented code: expose it generically rather than hide it.
        if (subs is { Count: > 0 })
        {
            return new VcpFeature
            {
                Code = code,
                Name = $"VCP 0x{code:X2}",
                Category = "Advanced",
                Kind = "discrete",
                Writable = true,
                Max = subs.Max(),
                Options = subs.Select(v => new VcpOption(v, v.ToString())).ToList()
            };
        }

        var genericKind = max > 0 ? "continuous" : "info";
        return new VcpFeature
        {
            Code = code,
            Name = $"VCP 0x{code:X2}",
            Category = "Advanced",
            Kind = genericKind,
            Writable = genericKind != "info",
            Max = max
        };
    }

    private static List<VcpOption>? BuildOptions(byte code, string kind, IReadOnlyList<uint>? subs,
        Dictionary<uint, string>? labels, uint max)
    {
        if (kind is "continuous" or "action" or "info") return null;

        var values = subs is { Count: > 0 }
            ? subs.ToList()
            : max is > 0 and <= 16
                ? Enumerable.Range(0, (int)max + 1).Select(i => (uint)i).ToList()
                : null;

        if (values is null) return null;

        return values
            .Distinct()
            .OrderBy(v => v)
            .Select(v => new VcpOption(v, Label(code, v, labels)))
            .ToList();
    }

    /// <summary>
    /// Options for a discrete control that did not advertise sub-values. The range is only
    /// known once the monitor has been read, so this is called again after every poll.
    /// </summary>
    public static List<VcpOption>? AutoOptions(byte code, string kind, uint max)
    {
        // One-shot commands have no value set to choose from; they are just "fire".
        if (kind is not ("discrete" or "toggle")) return null;
        if (max is 0 or > 16) return null;

        Known.TryGetValue(code, out var meta);
        var labels = meta?.Labels;

        return Enumerable.Range(0, (int)max + 1)
            .Select(i => (uint)i)
            .Select(v => new VcpOption(v, Label(code, v, labels)))
            .ToList();
    }

    private static string Label(byte code, uint value, Dictionary<uint, string>? labels)
    {
        if (labels is not null && labels.TryGetValue(value, out var label))
            return label;

        return code switch
        {
            0xDC => $"0x{value:X2}",
            0x86 => value switch { 1 => "1:1", 2 => "Full", 3 => "Aspect", _ => $"Mode {value}" },
            0x19 => value == 0 ? "Off" : $"Level {value}",
            _ => value.ToString()
        };
    }

    public static int CategoryRank(string category)
    {
        var idx = Array.IndexOf(CategoryOrder, category);
        return idx < 0 ? CategoryOrder.Length : idx;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace BenqBridge.Ddc;

public sealed record VcpCapability(byte Code, IReadOnlyList<uint>? SubValues);

/// <summary>
/// Parser for the MCCS capability string reported by the monitor, e.g.
/// (prot(monitor)type(lcd)model(GW2490)cmds(01 02 03)vcp(10 12 60(0F 11 12))mccs_ver(2.2))
/// </summary>
public sealed class Capabilities
{
    public string Raw { get; private init; } = "";
    public string? Model { get; private set; }
    public string? Type { get; private set; }
    public string? MccsVersion { get; private set; }
    public List<VcpCapability> Vcps { get; } = new();

    public bool Supports(byte code) => Vcps.Any(v => v.Code == code);

    public IReadOnlyList<uint>? SubValuesFor(byte code) =>
        Vcps.FirstOrDefault(v => v.Code == code)?.SubValues;

    public static Capabilities Parse(string raw)
    {
        var caps = new Capabilities { Raw = raw };
        caps.Model = Simple(raw, "model");
        caps.Type = Simple(raw, "type");
        caps.MccsVersion = Simple(raw, "mccs_ver");

        var block = Block(raw, "vcp");
        if (block is not null)
        {
            foreach (Match m in Regex.Matches(block, @"([0-9A-Fa-f]{2})(?:\(([^)]*)\))?"))
            {
                if (!byte.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    continue;

                List<uint>? subs = null;
                if (m.Groups[2].Success)
                {
                    subs = new List<uint>();
                    foreach (var token in m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                            subs.Add(v);
                    }
                }

                caps.Vcps.Add(new VcpCapability(code, subs));
            }
        }

        return caps;
    }

    private static string? Simple(string raw, string key)
    {
        var m = Regex.Match(raw, Regex.Escape(key) + @"\(([^()]*)\)");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>Extract the contents of name(...) honouring nested parentheses.</summary>
    private static string? Block(string raw, string key)
    {
        var marker = key + "(";
        var start = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        var open = start + key.Length;
        var depth = 0;
        for (var i = open; i < raw.Length; i++)
        {
            if (raw[i] == '(') depth++;
            else if (raw[i] == ')')
            {
                depth--;
                if (depth == 0) return raw.Substring(open + 1, i - open - 1);
            }
        }

        return null;
    }
}

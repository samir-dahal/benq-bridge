using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BenqBridge;
using BenqBridge.Ddc;

// The exe has no console of its own; when started from a terminal, borrow that terminal's.
NativeConsole.TryAttach();

var port = int.TryParse(GetOption(args, "--port") ?? Environment.GetEnvironmentVariable("BENQ_BRIDGE_PORT"), out var p)
    ? p
    : 5080;

var pairUrl = $"http://localhost:{port}/pair.html";
var noBrowser = args.Contains("--no-browser")
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BENQ_BRIDGE_NO_BROWSER"));

// One-shot commands that do their job and exit.
if (args.Contains("--install")) { Environment.ExitCode = AutoStart.Enable() ? 0 : 1; return; }
if (args.Contains("--uninstall")) { Environment.ExitCode = AutoStart.Disable() ? 0 : 1; return; }

using var instance = SingleInstance.Acquire(port);

if (args.Contains("--stop")) { SingleInstance.RequestStop(port); return; }

// Already running: launching again just brings up the pairing page instead of a second server.
if (!instance.IsFirst)
{
    if (!noBrowser) TryOpenBrowser(pairUrl);
    return;
}

// A published build keeps wwwroot next to the exe. Anchor there so a shortcut or a
// start-with-Windows entry still finds it; `dotnet run` keeps the project folder.
var contentRoot = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
    ? AppContext.BaseDirectory
    : Directory.GetCurrentDirectory();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
// The per-request ASP.NET logs drown out the one-line-per-monitor output.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton<MonitorRegistry>();
builder.Services.AddSingleton<WsHub>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
};

var (token, tokenIsNew) = ResolveToken(args);
var registry = app.Services.GetRequiredService<MonitorRegistry>();
var hub = app.Services.GetRequiredService<WsHub>();

app.Use(async (context, next) =>
{
    var path = context.Request.Path;

    // Pairing hands out the token and auto-start changes the PC's settings, so both are for the
    // PC itself only and need no token.
    if (path.StartsWithSegments("/api/pair") || path.StartsWithSegments("/api/autostart"))
    {
        if (!Pairing.IsLocalRequest(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next();
        return;
    }

    if (path.StartsWithSegments("/api") || path.StartsWithSegments("/ws"))
    {
        var supplied = context.Request.Headers["X-Api-Token"].FirstOrDefault()
                       ?? context.Request.Query["token"].FirstOrDefault();

        if (!TokenEquals(supplied, token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized" }, json);
            return;
        }
    }

    await next();
});

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------------------------------------------------------- REST API

app.MapGet("/api/health", () => new
{
    status = "ok",
    version = BuildInfo.Version,
    monitors = registry.Devices.Count(d => d.IsSupported),
    pollIntervalMs = registry.PollIntervalMs
});

app.MapGet("/api/pair", () => new { token, addresses = Pairing.Build(port, token) });

app.MapGet("/api/autostart", () => new { available = AutoStart.IsAvailable, enabled = AutoStart.IsEnabled() });

app.MapPut("/api/autostart", (AutoStartRequest? body) =>
{
    if (body is null) return Results.BadRequest(new { error = "body must be { \"enabled\": true|false }" });
    if (!AutoStart.IsAvailable) return Results.BadRequest(new { error = "start with Windows needs the BenqBridge.exe build" });

    var ok = body.Enabled ? AutoStart.Enable() : AutoStart.Disable();
    return ok
        ? Results.Ok(new { available = true, enabled = AutoStart.IsEnabled() })
        : Results.Problem("could not change the Windows startup entry", statusCode: 500);
});

app.MapGet("/api/monitors", () => registry.Snapshot());

app.MapGet("/api/monitors/{id}", (string id) =>
{
    var dto = registry.Snapshot().FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    return dto is null ? Results.NotFound(new { error = "monitor not found" }) : Results.Ok(dto);
});

app.MapPost("/api/monitors/{id}/refresh", (string id) =>
{
    var device = registry.Find(id);
    if (device is null) return Results.NotFound(new { error = "monitor not found" });
    if (!device.IsSupported) return Results.BadRequest(new { error = "monitor does not support DDC/CI" });

    device.Refresh();
    device.ReadAll();
    Broadcast();

    return Results.Ok(registry.Snapshot().FirstOrDefault(m => m.Id == device.Id));
});

app.MapGet("/api/monitors/{id}/vcp/{code}", (string id, string code) =>
{
    var device = registry.Find(id);
    if (device is null) return Results.NotFound(new { error = "monitor not found" });
    if (!TryParseCode(code, out var vcp)) return Results.BadRequest(new { error = "invalid VCP code" });

    var feature = device.Find(vcp);
    if (feature is null) return Results.NotFound(new { error = "VCP code not advertised by this monitor" });

    var reading = device.Read(vcp);
    if (reading is null) return Results.Problem("monitor did not answer the DDC/CI request", statusCode: 504);

    feature.Value = reading.Value.Current;
    if (reading.Value.Maximum > 0) feature.Max = reading.Value.Maximum;
    Broadcast();

    return Results.Ok(new
    {
        code = $"0x{vcp:X2}",
        name = feature.Name,
        type = reading.Value.Type,
        value = reading.Value.Current,
        max = reading.Value.Maximum
    });
});

app.MapPut("/api/monitors/{id}/vcp/{code}", (string id, string code, VcpWriteRequest? body) =>
{
    var device = registry.Find(id);
    if (device is null) return Results.NotFound(new { error = "monitor not found" });
    if (!device.IsSupported) return Results.BadRequest(new { error = "monitor does not support DDC/CI" });
    if (!TryParseCode(code, out var vcp)) return Results.BadRequest(new { error = "invalid VCP code" });
    if (body is null) return Results.BadRequest(new { error = "body must be { \"value\": <number> }" });

    var feature = device.Find(vcp);
    if (feature is null) return Results.NotFound(new { error = "VCP code not advertised by this monitor" });
    if (!feature.Writable) return Results.BadRequest(new { error = $"{feature.Name} is read-only" });

    var value = body.Value;
    if (feature.Options is { Count: > 0 } && feature.Options.All(o => o.Value != value))
    {
        return Results.BadRequest(new
        {
            error = "value not in the advertised set",
            allowed = feature.Options.Select(o => o.Value)
        });
    }

    if (feature.Kind == "continuous" && feature.Max > 0 && value > feature.Max)
        return Results.BadRequest(new { error = $"value must be between 0 and {feature.Max}" });

    if (!device.Write(vcp, value))
        return Results.Problem("monitor rejected the DDC/CI write", statusCode: 502);

    // One-shot commands (factory reset, restore-colour-defaults) clear themselves on the
    // panel, so a readback would always show 0 and look like a failed write. The write
    // acknowledgement is the whole result.
    if (feature.Kind == "action")
    {
        Broadcast();
        return Results.Ok(new
        {
            code = $"0x{vcp:X2}",
            name = feature.Name,
            requested = value,
            value = (uint?)null,
            max = feature.Max,
            confirmed = true,
            action = true
        });
    }

    // Let the panel settle, then confirm the value actually stuck.
    Thread.Sleep(80);
    var reading = device.Read(vcp);
    if (reading is not null)
    {
        feature.Value = reading.Value.Current;
        if (reading.Value.Maximum > 0) feature.Max = reading.Value.Maximum;
    }

    Broadcast();

    return Results.Ok(new
    {
        code = $"0x{vcp:X2}",
        name = feature.Name,
        requested = value,
        value = feature.Value,
        max = feature.Max,
        confirmed = feature.Value == value
    });
});

app.MapGet("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await hub.RunAsync(socket, json);
});

// ---------------------------------------------------------------- startup

registry.StateChanged += Broadcast;
app.Lifetime.ApplicationStarted.Register(() =>
{
    registry.Start();

    Console.WriteLine();
    Console.WriteLine($"  BenQ Bridge {BuildInfo.Version}");
    Console.WriteLine("  ----------------------------------------------");
    foreach (var url in LanUrls(port)) Console.WriteLine($"  {url}");
    Console.WriteLine($"  token: {token}");
    Console.WriteLine("  ----------------------------------------------");
    Console.WriteLine($"  Pair a phone: {pairUrl}");
    Console.WriteLine();

    // First run shows the QR code without being asked. After that it stays out of the way,
    // so a start-with-Windows entry does not open a browser on every boot.
    var wantPairPage = tokenIsNew || args.Contains("--pair");
    if (wantPairPage && !noBrowser) TryOpenBrowser(pairUrl);
});

app.Lifetime.ApplicationStopped.Register(() => registry.DisposeAsync().AsTask().GetAwaiter().GetResult());

// A second launch with --stop asks this copy to quit.
instance.ListenForStop(port, () => app.Lifetime.StopApplication());

await app.StartAsync();

var tray = TrayApp.Start(
    BuildInfo.Version,
    pairUrl,
    $"http://localhost:{port}/#t={Uri.EscapeDataString(token)}",
    quit: () => app.Lifetime.StopApplication(),
    announce: tokenIsNew && !noBrowser);

await app.WaitForShutdownAsync();
tray.Stop();
await app.StopAsync();

// ---------------------------------------------------------------- helpers

void Broadcast()
{
    var payload = JsonSerializer.Serialize(new { type = "state", monitors = registry.Snapshot() }, json);
    _ = hub.BroadcastAsync(payload);
}

static bool TryParseCode(string raw, out byte code)
{
    var text = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
    return byte.TryParse(text, System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture, out code);
}

static string? GetOption(string[] argv, string name)
{
    for (var i = 0; i < argv.Length; i++)
    {
        if (argv[i] == name && i + 1 < argv.Length) return argv[i + 1];
        if (argv[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            return argv[i][(name.Length + 1)..];
    }

    return null;
}

static void TryOpenBrowser(string url)
{
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch
    {
        // Headless or locked-down machine: the console already printed the address.
    }
}

/// <summary>Returns the token and whether it was generated on this run (a first-time setup).</summary>
static (string Token, bool IsNew) ResolveToken(string[] argv)
{
    var explicitToken = GetOption(argv, "--token") ?? Environment.GetEnvironmentVariable("BENQ_BRIDGE_TOKEN");
    if (!string.IsNullOrWhiteSpace(explicitToken)) return (explicitToken.Trim(), false);

    try
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var path = Path.Combine(AppPaths.DataDir, "token.txt");

        var saved = ReadToken(path);
        if (saved is not null) return (saved, false);

        // An early test build kept the token next to the exe. Carry it over so paired phones stay paired.
        var legacy = ReadToken(Path.Combine(AppContext.BaseDirectory, "token.txt"));
        if (legacy is not null)
        {
            File.WriteAllText(path, legacy);
            return (legacy, false);
        }

        var generated = NewToken();
        File.WriteAllText(path, generated);
        return (generated, true);
    }
    catch
    {
        // The data folder is not writable: fall back to a token that lasts for this run only.
        return (NewToken(), true);
    }
}

static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

static string? ReadToken(string path)
{
    try
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Trim();
        return text.Length > 0 ? text : null;
    }
    catch
    {
        return null;
    }
}

static bool TokenEquals(string? supplied, string expected)
{
    if (string.IsNullOrEmpty(supplied)) return false;
    var a = Encoding.UTF8.GetBytes(supplied);
    var b = Encoding.UTF8.GetBytes(expected);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

static IEnumerable<string> LanUrls(int port)
{
    yield return $"http://localhost:{port}/";
    foreach (var address in Pairing.FindLanAddresses()) yield return $"http://{address.Ip}:{port}/";
}

internal sealed record VcpWriteRequest(uint Value);

internal sealed record AutoStartRequest(bool Enabled);

internal static class BuildInfo
{
    public static string Version => "0.1.0";
}

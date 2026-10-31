using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BenqBridge;

/// <summary>
/// The app's only visible surface: a notification-area icon. It replaces the console window,
/// so there is something to right-click to pair a phone or quit.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Control _marshal = new();
    private readonly string _pairUrl;
    private readonly string _controlsUrl;
    private Thread? _thread;

    private TrayApp(string version, string pairUrl, string controlsUrl, Action quit, bool announce)
    {
        _pairUrl = pairUrl;
        _controlsUrl = controlsUrl;

        var autoStart = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        autoStart.Click += (_, _) =>
        {
            if (!(autoStart.Checked ? AutoStart.Disable() : AutoStart.Enable()))
            {
                _icon!.ShowBalloonTip(4000, "BenQ Bridge",
                    "Could not change the Start with Windows setting.", ToolTipIcon.Warning);
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"BenQ Bridge {version}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pair a phone", null, (_, _) => Open(_pairUrl));
        menu.Items.Add("Open controls", null, (_, _) => Open(_controlsUrl));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());

        menu.Opening += (_, _) =>
        {
            autoStart.Enabled = AutoStart.IsAvailable;
            autoStart.Checked = AutoStart.IsEnabled();
        };

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "BenQ Bridge",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => Open(_pairUrl);

        // Creating the handle now lets Stop() marshal onto this thread later.
        _ = _marshal.Handle;

        if (announce)
        {
            _icon.ShowBalloonTip(6000, "BenQ Bridge is running",
                "Scan the QR code in your browser to pair a phone. Right-click this icon for options.",
                ToolTipIcon.Info);
        }
    }

    /// <summary>Starts the tray icon on its own STA thread and returns once it is showing.</summary>
    public static TrayApp Start(string version, string pairUrl, string controlsUrl, Action quit, bool announce)
    {
        TrayApp? app = null;
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            app = new TrayApp(version, pairUrl, controlsUrl, quit, announce);
            ready.Set();
            Application.Run(app);
        })
        {
            IsBackground = true,
            Name = "BenqBridge tray"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        app!._thread = thread;
        return app;
    }

    /// <summary>Removes the icon (Windows would otherwise leave a ghost until hovered) and ends the loop.</summary>
    public void Stop()
    {
        if (_marshal.IsHandleCreated)
        {
            _marshal.BeginInvoke(new Action(() =>
            {
                _icon.Visible = false;
                _icon.Dispose();
                ExitThread();
            }));
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Nothing sensible to show without a window; the address is also in the console banner.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>A small monitor glyph in the app's violet, drawn at runtime so no .ico file ships.</summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var violet = new SolidBrush(Color.FromArgb(155, 131, 255));
            using var dark = new SolidBrush(Color.FromArgb(20, 16, 42));
            using var lamp = new SolidBrush(Color.FromArgb(242, 180, 90));

            FillRounded(g, violet, new Rectangle(1, 3, 30, 20), 5);
            FillRounded(g, dark, new Rectangle(4, 6, 24, 14), 3);
            g.FillEllipse(lamp, 11, 9, 10, 8);
            g.FillRectangle(violet, 13, 23, 6, 4);
            FillRounded(g, violet, new Rectangle(8, 27, 16, 3), 1);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}

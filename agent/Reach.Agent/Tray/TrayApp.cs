using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Reach.Agent.Commands;
using Reach.Agent.Net;
using Reach.Agent.Pairing;
using Reach.Agent.Storage;

namespace Reach.Agent.Tray;

/// <summary>The tray icon and its menu (spec §5). Everything UI runs on the WinForms thread.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly AgentContext _agent;
    private readonly AgentPaths _paths;
    private readonly int _port;
    private readonly NotifyIcon _icon;
    private readonly Control _ui = new();
    private readonly StartupRegistration _startup = new();
    private readonly ToolStripLabel _header;
    private readonly ToolStripMenuItem _disconnect;
    private PairWindow? _pairWindow;
    private ConnectedWindow? _connectedWindow;
    /// <summary>The connected phone, while one is.</summary>
    private (string Name, DateTimeOffset Since)? _connected;

    public TrayApp(AgentContext agent, CommandService commands, AgentPaths paths, int port)
    {
        _agent = agent;
        _paths = paths;
        _port = port;
        _ui.CreateControl(); // gives background threads something to BeginInvoke onto

        ToolStripManager.Renderer = new TrayTheme.MenuRenderer(); // submenus too
        var menu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.ManagerRenderMode, ShowImageMargin = false, Padding = new Padding(4) };
        _header = new ToolStripLabel
        {
            Image = TrayIcons.Draw(Color.FromArgb(0x6D, 0x93, 0xFF), Color.FromArgb(0x3A, 0x5B, 0xF0), 64),
            ImageScaling = ToolStripItemImageScaling.None,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Font = new Font("Segoe UI Semibold", 10.5f),
            Padding = new Padding(4, 8, 12, 8),
        };
        _header.Image = new Bitmap(_header.Image, 28, 28);
        menu.Items.Add(_header);
        menu.Items.Add(new ToolStripSeparator());
        _disconnect = new ToolStripMenuItem("Disconnect", null, (_, _) => _agent.Sessions.DisconnectActive()) { Visible = false };
        menu.Items.Add(_disconnect);
        menu.Items.Add("Pair new phone…", null, (_, _) => ShowPairWindow());
        var devices = new ToolStripMenuItem("Paired devices");
        devices.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
        devices.DropDownOpening += (_, _) => FillDevices(devices);
        menu.Items.Add(devices);
        menu.Items.Add("Edit commands", null, (_, _) => EditCommands());
        var startWithWindows = new ToolStripMenuItem("Start with Windows") { Checked = _startup.IsEnabled };
        startWithWindows.Click += (_, _) => ToggleStartup(startWithWindows);
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Reach", null, (_, _) => ExitThread());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripLabel(TrayTheme.Promise) { ForeColor = TrayTheme.Muted, Font = new Font("Segoe UI", 8.5f), ToolTipText = TrayTheme.PromiseDetail });

        _icon = new NotifyIcon { Icon = TrayIcons.Idle, Text = "Reach — no phone connected", ContextMenuStrip = menu, Visible = true };
        ShowStatus();
        _icon.MouseClick += (_, e) =>
        {
            // While a phone is connected there is nothing to pair: show who is connected instead.
            if (e.Button != MouseButtons.Left) return;
            if (_connected is null) ShowPairWindow();
            else ShowConnectedWindow();
        };

        agent.Sessions.Started += session =>
        {
            var name = agent.Devices.All.FirstOrDefault(d => d.Id == session.Device.Id)?.Name ?? DeviceStore.DefaultName;
            OnUi(() => OnConnected(name, session.StartedAt));
        };
        agent.Sessions.Ended += _ => OnUi(OnDisconnected);
        agent.Sessions.DeviceConnected += name => OnUi(() =>
        {
            // The name from the phone's hello, which follows the connection.
            if (_connected is { } c) OnConnected(name, c.Since);
            Notify($"{name} connected", ToolTipIcon.Info);
        });
        commands.ConfigError += error => OnUi(() => Notify($"commands.json has a problem, keeping the previous list: {error}", ToolTipIcon.Warning));
        if (commands.LastError is { } startupError)
            Notify($"commands.json has a problem, so no commands are available: {startupError}", ToolTipIcon.Warning);
    }

    /// <summary>Shows a Windows notification (a toast on Windows 10/11).</summary>
    public void Notify(string text, ToolTipIcon icon) => _icon.ShowBalloonTip(5000, "Reach", text, icon);

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _pairWindow?.Close();
        _connectedWindow?.Close();
        base.ExitThreadCore();
    }

    private void OnUi(Action action)
    {
        if (!_ui.IsDisposed) _ui.BeginInvoke(action);
    }

    private void OnConnected(string name, DateTimeOffset since)
    {
        _connected = (name, since);
        _pairWindow?.Close(); // a phone is in: no code to show
        if (_connectedWindow is { IsDisposed: false }) _connectedWindow.SetDeviceName(name);
        ShowStatus();
    }

    private void OnDisconnected()
    {
        _connected = null;
        _connectedWindow?.Close();
        ShowStatus();
    }

    /// <summary>The icon, its tooltip, the menu's header and its Disconnect item.</summary>
    private void ShowStatus()
    {
        _icon.Icon = _connected is null ? TrayIcons.Idle : TrayIcons.Active;
        if (_connected is { } c)
        {
            var tooltip = $"Reach — {c.Name} connected";
            _icon.Text = tooltip.Length > 63 ? tooltip[..62] + "…" : tooltip; // the tray's limit
            _header.Text = $"Reach\n{c.Name} · since {c.Since.ToLocalTime():HH:mm}";
            _disconnect.Text = $"Disconnect {c.Name}";
            _disconnect.Visible = true;
        }
        else
        {
            _icon.Text = "Reach — no phone connected";
            _header.Text = "Reach\nNo phone connected";
            _disconnect.Visible = false;
        }
    }

    private void ShowConnectedWindow()
    {
        if (_connectedWindow is { IsDisposed: false })
        {
            _connectedWindow.Activate();
            return;
        }
        var (name, since) = _connected!.Value;
        _connectedWindow = new ConnectedWindow(name, since, _agent.Time, _agent.Sessions.DisconnectActive);
        _connectedWindow.Show();
    }

    private void ShowPairWindow()
    {
        _connectedWindow?.Close();
        if (_pairWindow is { IsDisposed: false })
        {
            _pairWindow.Activate();
            return;
        }
        var hosts = PairingUri.LocalPrivateAddresses();
        var code = _agent.Pairing.Start();
        var uri = PairingUri.Build(hosts, _port, _agent.Identity.PublicKey, code.Code, _agent.PcName);
        _pairWindow = new PairWindow(_agent.Pairing, uri, code, _agent.Time, hasNetwork: hosts.Count > 0);
        _pairWindow.Show();
    }

    private void FillDevices(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        var devices = _agent.Devices.All;
        if (devices.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
            return;
        }
        foreach (var device in devices)
        {
            var item = new ToolStripMenuItem($"{device.Name}  (last seen {device.LastSeenAt.ToLocalTime():d MMM, HH:mm})");
            item.DropDownItems.Add("Revoke…", null, (_, _) => Revoke(device));
            parent.DropDownItems.Add(item);
        }
    }

    private void Revoke(Device device)
    {
        var answer = MessageBox.Show(
            $"Revoke \"{device.Name}\"? It will be disconnected and must be paired again to reconnect.",
            "Reach", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (answer != DialogResult.OK) return;
        _agent.Devices.Remove(device.Id);
        _agent.Sessions.CloseDevice(device.Id);
    }

    private void EditCommands()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_paths.Commands) { UseShellExecute = true, Verb = "edit" });
        }
        catch (Win32Exception)
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_paths.Commands}\""));
        }
    }

    private void ToggleStartup(ToolStripMenuItem item)
    {
        if (_startup.IsEnabled) _startup.Disable();
        else _startup.Enable(Environment.ProcessPath!);
        item.Checked = _startup.IsEnabled;
    }
}

using System.Text.Json;

namespace GalacticMouse;

internal sealed class MainForm : Form
{
    private readonly TextBox _url = new() { Dock = DockStyle.Fill };
    private readonly TextBox _code = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _side = new() { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _connect = new() { Text = "Connect", Width = 110 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, Text = "Disconnected" };
    private readonly System.Windows.Forms.Timer _edgeTimer = new() { Interval = 10 };
    private readonly Settings _settings = Settings.Load();
    private NativeInput? _input;
    private RelayClient? _relay;
    private bool _remoteMode, _controlled;
    private int _remoteWidth, _remoteHeight, _remoteX, _remoteY;
    private bool _movePending;
    private DateTime _nextMoveSend;
    private DateTime _edgeGuard;
    private readonly HashSet<(uint scan, bool extended)> _heldKeys = [];
    private readonly HashSet<int> _heldButtons = [];

    public MainForm()
    {
        Text = "GalacticMouse";
        Width = 500; Height = 295;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        _url.Text = _settings.RelayUrl;
        _code.Text = _settings.PairingCode;
        _side.Items.AddRange(["Right", "Left"]);
        _side.SelectedIndex = _settings.NeighborRight ? 0 : 1;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(15), ColumnCount = 2, RowCount = 5,
            AutoSize = false
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 4 ? 55 : 40));
        layout.Controls.Add(new Label { Text = "Relay URL", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_url, 1, 0);
        layout.Controls.Add(new Label { Text = "Pairing code", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_code, 1, 1);
        layout.Controls.Add(new Label { Text = "Other PC is", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_side, 1, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var generate = new Button { Text = "Generate pairing code", AutoSize = true };
        generate.Click += (_, _) => { _code.Text = Settings.NewCode(); Clipboard.SetText(_code.Text); _status.Text = "New code copied to clipboard. Paste it on the other PC."; };
        buttons.Controls.Add(generate);
        buttons.Controls.Add(_connect);
        _connect.Click += (_, _) => { if (_relay is null) Connect(); else Disconnect(); };
        layout.Controls.Add(buttons, 1, 3);
        layout.Controls.Add(_status, 1, 4);
        Controls.Add(layout);
        _edgeTimer.Tick += (_, _) => CheckEdge();
        FormClosing += (_, _) => Disconnect();
    }

    private void Connect()
    {
        try
        {
            var uri = new Uri(_url.Text.Trim());
            if (uri.Scheme != "wss" && !(uri.Scheme == "ws" && uri.IsLoopback))
                throw new FormatException("Use a secure wss:// URL (ws:// only for localhost development).");
            if (!uri.AbsolutePath.EndsWith("/ws", StringComparison.Ordinal)) throw new FormatException("Relay URL must end in /ws.");
            var secret = Settings.DecodeCode(_code.Text);
            _settings.RelayUrl = uri.ToString();
            _settings.PairingCode = _code.Text.Trim();
            _settings.NeighborRight = _side.SelectedIndex == 0;
            _settings.Save();
            _input = new NativeInput(Handle);
            _input.MouseEvent = OnMouseEvent;
            _input.KeyEvent = OnKeyEvent;
            _input.RawMouseMove = OnRawMouseMove;
            _relay = new RelayClient(uri, secret);
            _relay.PresenceChanged += present => OnUi(() => PresenceChanged(present));
            _relay.StatusChanged += status => OnUi(() => _status.Text = status);
            _relay.MessageReceived += message => OnUi(() => HandlePeerMessage(message));
            _relay.Start();
            _edgeTimer.Start();
            _url.Enabled = _code.Enabled = _side.Enabled = false;
            _connect.Text = "Disconnect";
        }
        catch (Exception ex)
        {
            _input?.Dispose(); _input = null;
            _relay?.Dispose(); _relay = null;
            MessageBox.Show(this, ex.Message, "GalacticMouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Disconnect()
    {
        _edgeTimer.Stop();
        StopRemote();
        ReleaseHeldInput();
        _controlled = false;
        _input?.Dispose(); _input = null;
        _relay?.Dispose(); _relay = null;
        _url.Enabled = _code.Enabled = _side.Enabled = true;
        _connect.Text = "Connect";
        _status.Text = "Disconnected";
    }

    private void OnUi(Action action)
    {
        if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed && _relay is not null) action(); });
    }

    private void PresenceChanged(bool present)
    {
        if (!present)
        {
            StopRemote();
            ReleaseHeldInput();
            _controlled = false;
            _remoteWidth = _remoteHeight = 0;
        }
        else
        {
            var bounds = Screen.PrimaryScreen!.Bounds;
            _relay?.Send(new { type = "hello", width = bounds.Width, height = bounds.Height });
        }
    }

    private void HandlePeerMessage(JsonElement message)
    {
        try
        {
            var type = message.GetProperty("type").GetString();
            if (type == "hello")
            {
                _remoteWidth = Math.Clamp(message.GetProperty("width").GetInt32(), 100, 10000);
                _remoteHeight = Math.Clamp(message.GetProperty("height").GetInt32(), 100, 10000);
            }
            else if (type == "enter")
            {
                if (_remoteMode) return;
                _controlled = true;
                SetRemoteCursor(message);
            }
            else if (type == "move" && _controlled) SetRemoteCursor(message);
            else if (type == "key" && _controlled)
            {
                var scan = (uint)message.GetProperty("scan").GetInt32();
                var extended = message.GetProperty("extended").GetBoolean();
                var up = message.GetProperty("up").GetBoolean();
                if (scan is 0 or > 255) return;
                NativeInput.InjectKey(scan, extended, up);
                if (up) _heldKeys.Remove((scan, extended)); else _heldKeys.Add((scan, extended));
            }
            else if (type == "mouse" && _controlled)
            {
                var action = message.GetProperty("action").GetInt32();
                var data = message.GetProperty("data").GetInt32();
                if (action is not (0x0201 or 0x0202 or 0x0204 or 0x0205 or 0x0207 or 0x0208 or 0x020A or 0x020E or 0x020B or 0x020C)) return;
                NativeInput.InjectMouse(action, data);
                if (action is 0x0201 or 0x0204 or 0x0207 or 0x020B) _heldButtons.Add(action);
                if (action is 0x0202 or 0x0205 or 0x0208 or 0x020C) _heldButtons.Remove(action - 1);
            }
            else if (type == "leave")
            {
                _controlled = false;
                ReleaseHeldInput();
            }
        }
        catch (Exception) { /* malformed but authenticated frame; ignore */ }
    }

    private void SetRemoteCursor(JsonElement message)
    {
        var bounds = Screen.PrimaryScreen!.Bounds;
        var x = Math.Clamp(message.GetProperty("x").GetInt32(), 0, bounds.Width - 1);
        var y = Math.Clamp(message.GetProperty("y").GetInt32(), 0, bounds.Height - 1);
        NativeInput.Cursor = new Point(bounds.Left + x, bounds.Top + y);
    }

    private void CheckEdge()
    {
        if (_remoteMode)
        {
            if (_movePending && DateTime.UtcNow >= _nextMoveSend) FlushMove();
            return;
        }
        if (_remoteMode || _controlled || _relay?.PeerPresent != true || _remoteWidth < 100 || DateTime.UtcNow < _edgeGuard) return;
        var bounds = Screen.PrimaryScreen!.Bounds;
        var point = NativeInput.Cursor;
        if (!bounds.Contains(point)) return;
        var atEdge = _settings.NeighborRight ? point.X >= bounds.Right - 1 : point.X <= bounds.Left;
        if (!atEdge) return;
        _remoteMode = true;
        _remoteX = _settings.NeighborRight ? 2 : _remoteWidth - 3;
        _remoteY = Math.Clamp((int)((long)(point.Y - bounds.Top) * _remoteHeight / bounds.Height), 0, _remoteHeight - 1);
        _relay.Send(new { type = "enter", x = _remoteX, y = _remoteY });
        _status.Text = "Controlling other PC — move back or press Ctrl+Alt+Shift+F12";
    }

    private void OnRawMouseMove(int dx, int dy)
    {
        if (!_remoteMode || (dx == 0 && dy == 0)) return;
        var next = _remoteX + dx;
        if ((_settings.NeighborRight && next <= 0) || (!_settings.NeighborRight && next >= _remoteWidth - 1))
        {
            StopRemote();
            return;
        }
        _remoteX = Math.Clamp(next, 1, _remoteWidth - 2);
        _remoteY = Math.Clamp(_remoteY + dy, 0, _remoteHeight - 1);
        _movePending = true;
    }

    private void FlushMove()
    {
        if (!_movePending || !_remoteMode) return;
        _movePending = false;
        _nextMoveSend = DateTime.UtcNow.AddMilliseconds(12);
        _relay?.Send(new { type = "move", x = _remoteX, y = _remoteY });
    }

    private bool OnMouseEvent(int message, NativeInput.MouseHookData data)
    {
        if (!_remoteMode) return false;
        if (message is 0x0201 or 0x0202 or 0x0204 or 0x0205 or 0x0207 or 0x0208 or 0x020A or 0x020E or 0x020B or 0x020C)
        {
            FlushMove();
            var wheel = unchecked((short)(data.MouseData >> 16));
            var extra = message is 0x020B or 0x020C ? (int)((data.MouseData >> 16) << 16) : wheel;
            _relay?.Send(new { type = "mouse", action = message, data = extra });
        }
        return true;
    }

    private bool OnKeyEvent(int message, NativeInput.KeyHookData data)
    {
        if (!_remoteMode) return false;
        var up = message is 0x0101 or 0x0105;
        if (data.Vk == 0x7B && !up && NativeInput.IsPressed(0x11) && NativeInput.IsPressed(0x12) && NativeInput.IsPressed(0x10))
        {
            StopRemote();
            return true;
        }
        if (message is 0x0100 or 0x0101 or 0x0104 or 0x0105)
            _relay?.Send(new { type = "key", scan = data.Scan, extended = (data.Flags & 1) != 0, up });
        return true;
    }

    private void StopRemote()
    {
        if (!_remoteMode) return;
        _remoteMode = false;
        _movePending = false;
        _relay?.Send(new { type = "leave" });
        var bounds = Screen.PrimaryScreen!.Bounds;
        var x = _settings.NeighborRight ? bounds.Right - 25 : bounds.Left + 25;
        NativeInput.Cursor = new Point(x, Math.Clamp(NativeInput.Cursor.Y, bounds.Top, bounds.Bottom - 1));
        _edgeGuard = DateTime.UtcNow.AddMilliseconds(350);
        _status.Text = "Local control";
    }

    private void ReleaseHeldInput()
    {
        foreach (var (scan, extended) in _heldKeys) NativeInput.InjectKey(scan, extended, true);
        _heldKeys.Clear();
        foreach (var down in _heldButtons) NativeInput.InjectMouse(down + 1);
        _heldButtons.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        _input?.HandleMessage(ref m);
        base.WndProc(ref m);
    }
}

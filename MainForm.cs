using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Windows.Forms;
using NmosUmd.App;
using NmosUmd.Net;
using NmosUmd.Nmos;
using NmosUmd.Tsl;

namespace NmosUmd;

public sealed class MainForm : Form
{
    /// <summary>Item behind the receiver drop-down in the mapping grid.</summary>
    private sealed class ReceiverOption
    {
        public string Id { get; init; } = string.Empty;
        public string Display { get; init; } = string.Empty;
        public override string ToString() => Display;
    }

    private const int MaxLogLines = 500;

    /// <summary>Width of the caption column in the Label group, so the three rows line up.</summary>
    private static readonly int LabelColumnWidth = TextRenderer.MeasureText("Unrouted", SystemFonts.MessageBoxFont ?? DefaultFont).Width + 12;

    // ---- registry ----
    private readonly RadioButton _rbDiscover = new() { Text = "Discover (mDNS)", Checked = true, AutoSize = true };
    private readonly ComboBox _cmbDiscovered = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly Button _btnRescan = new() { Text = "Rescan" };
    private readonly RadioButton _rbManual = new() { Text = "Manual", AutoSize = true };
    private readonly TextBox _txtRegistry = new() { Width = 200, Enabled = false };
    private readonly CheckBox _chkHttps = new() { Text = "HTTPS", AutoSize = true, Enabled = false };
    private readonly CheckBox _chkInsecure = new() { Text = "Ignore certificate errors", AutoSize = true, Enabled = false };
    private readonly ComboBox _cmbApiVersion = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly NumericUpDown _numPoll = new() { Minimum = 200, Maximum = 60000, Increment = 100, Value = 1000, Width = 80 };
    private readonly Button _btnConnect = new() { Text = "Connect" };
    private readonly Label _lblRegistry = new() { AutoSize = true, Text = "Not connected." };

    // ---- TSL output ----
    private readonly TextBox _txtTslHost = new() { Text = "127.0.0.1", Width = 150 };
    private readonly NumericUpDown _numTslPort = new() { Minimum = 1, Maximum = 65535, Value = 8900, Width = 80 };
    private readonly RadioButton _rbUdp = new() { Text = "UDP", Checked = true, AutoSize = true };
    private readonly RadioButton _rbTcp = new() { Text = "TCP", AutoSize = true };
    private readonly CheckBox _chkFraming = new() { Text = "TCP stream framing (DLE/STX)", AutoSize = true };
    private readonly ComboBox _cmbVersion = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly NumericUpDown _numScreen = new() { Minimum = 0, Maximum = 65534, Value = 0, Width = 80 };
    private readonly CheckBox _chkScreenBroadcast = new() { Text = "All screens (0xFFFF)", AutoSize = true };
    private readonly CheckBox _chkUnicode = new() { Text = "UTF-16LE text (V5.0)", AutoSize = true };
    private readonly NumericUpDown _numSendInterval = new() { Minimum = 100, Maximum = 60000, Increment = 100, Value = 1000, Width = 80 };
    private readonly ComboBox _cmbRoutedLamp = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly ComboBox _cmbUnroutedLamp = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly CheckBox _chkTextTally = new() { Text = "Drive text tally", AutoSize = true };
    private readonly ComboBox _cmbBrightness = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly Button _btnOutput = new() { Text = "Start output" };

    // ---- label ----
    private readonly TextBox _txtRouted = new() { Width = 260, Text = LabelFormatter.DefaultRoutedTemplate };
    private readonly TextBox _txtUnrouted = new() { Width = 260, Text = LabelFormatter.DefaultUnroutedTemplate };
    private readonly ComboBox _cmbFit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly CheckBox _chkUppercase = new() { Text = "Upper case", AutoSize = true };
    private readonly Label _lblFitNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    // ---- mapping ----
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        EditMode = DataGridViewEditMode.EditOnEnter,
        BackgroundColor = SystemColors.Window
    };

    private readonly DataGridViewCheckBoxColumn _colEnabled = new() { HeaderText = "On", Width = 38 };
    private readonly DataGridViewTextBoxColumn _colAddress = new() { HeaderText = "Addr", Width = 56 };
    private readonly DataGridViewComboBoxColumn _colReceiver = new()
    {
        HeaderText = "NMOS receiver",
        Width = 300,
        DisplayMember = nameof(ReceiverOption.Display),
        ValueMember = nameof(ReceiverOption.Id),
        FlatStyle = FlatStyle.Flat,
        DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing,
        DisplayStyleForCurrentCellOnly = true
    };
    private readonly DataGridViewTextBoxColumn _colSource = new() { HeaderText = "Routed source", Width = 250, ReadOnly = true };
    private readonly DataGridViewTextBoxColumn _colText = new() { HeaderText = "UMD text", Width = 170, ReadOnly = true };
    private readonly DataGridViewTextBoxColumn _colTemplate = new() { HeaderText = "Template override", Width = 160 };

    private readonly Button _btnAddReceivers = new() { Text = "Add receivers..." };
    private readonly Button _btnAddRow = new() { Text = "Add blank row" };
    private readonly Button _btnRemove = new() { Text = "Remove" };
    private readonly Button _btnSaveConfig = new() { Text = "Save" };
    private readonly Button _btnExport = new() { Text = "Export..." };
    private readonly Button _btnImport = new() { Text = "Import..." };

    // ---- log and status ----
    private readonly TextBox _txtLog = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        BackColor = Color.White,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f)
    };

    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusRegistry = new() { Text = "Registry: idle", AutoSize = true };
    private readonly ToolStripStatusLabel _statusOutput = new() { Text = "Output: stopped", AutoSize = true, Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusPackets = new() { Text = "0 packets", AutoSize = true };

    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
    private readonly System.Windows.Forms.Timer _sendTimer = new();

    private readonly AppConfig _config;
    private readonly RegistryMonitor _registry = new();
    private readonly MdnsBrowser _mdns;

    private FlowLayoutPanel _settings = null!;
    private RegistrySnapshot _snapshot = RegistrySnapshot.Empty;
    private IPacketSender? _sender;
    private string _senderKey = string.Empty;
    private string _receiverOptionsKey = string.Empty;
    private long _packetsSent;
    private int _sendCursor;

    // The window connects and starts sending on its own. These track whether that is still
    // wanted: an explicit Disconnect or Stop output means the operator has taken over, and
    // nothing should restart behind their back.
    private bool _autoConnectPending = true;
    private bool _autoOutputPending = true;
    private DateTime _lastAutoOutputAttempt = DateTime.MinValue;
    private bool _outputRunning;
    private bool _loading = true;

    public MainForm()
    {
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont ?? DefaultFont;
        Text = "NMOS UMD";
        StartPosition = FormStartPosition.CenterScreen;

        _config = LoadConfigSafely();
        _mdns = new MdnsBrowser(log: message => BeginInvoke(() => Log(message)));

        BuildLayout();
        WireEvents();
        ApplyConfig();
        _loading = false;

        RebuildReceiverOptions(force: true);
        RefreshRows(send: false);
        UpdateEnabledState();

        // Open wide enough for the three setting groups to sit side by side where the screen
        // allows it, and never wider than the screen.
        var working = Screen.FromPoint(Cursor.Position).WorkingArea;
        var width = Math.Min(Math.Max(_settings.PreferredSize.Width + 24, 1000), working.Width - 80);
        var height = Math.Min(880, working.Height - 100);
        ClientSize = new Size(width, height);
        MinimumSize = new Size(700, 560);
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        foreach (var button in new[] { _btnRescan, _btnConnect, _btnOutput, _btnAddReceivers, _btnAddRow,
                                       _btnRemove, _btnSaveConfig, _btnExport, _btnImport })
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(90, 27);
            button.Margin = new Padding(3);
        }

        _cmbApiVersion.Items.Add("Auto");
        foreach (var version in NmosQueryClient.SupportedVersions) _cmbApiVersion.Items.Add(version);
        _cmbApiVersion.SelectedIndex = 0;

        _cmbVersion.Items.AddRange(new object[] { "V3.1 (16 chars)", "V4.0 (16 chars)", "V5.0" });
        _cmbVersion.SelectedIndex = 2;

        foreach (var combo in new[] { _cmbRoutedLamp, _cmbUnroutedLamp })
            combo.Items.AddRange(new object[] { TallyColour.Off, TallyColour.Red, TallyColour.Green, TallyColour.Amber });
        _cmbRoutedLamp.SelectedIndex = 0;
        _cmbUnroutedLamp.SelectedIndex = 0;

        _cmbBrightness.Items.AddRange(new object[] { "0 - off", "1 - 1/7", "2 - 1/2", "3 - full" });
        _cmbBrightness.SelectedIndex = 3;

        _cmbFit.Items.AddRange(new object[] { "Truncate", "Keep the end", "Squeeze (drop vowels)" });
        _cmbFit.SelectedIndex = 0;

        // Wrapping rather than a fixed three columns: on a narrow window the groups stack
        // instead of running off the right-hand edge.
        _settings = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(6, 6, 6, 0)
        };

        _settings.Controls.Add(BuildRegistryGroup());
        _settings.Controls.Add(BuildOutputGroup());
        _settings.Controls.Add(BuildLabelGroup());

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 420,
            Panel2MinSize = 80
        };

        split.Panel1.Controls.Add(BuildMappingPanel());
        split.Panel2.Controls.Add(_txtLog);
        split.Panel2.Padding = new Padding(6, 0, 6, 0);

        _status.Items.AddRange(new ToolStripItem[] { _statusRegistry, _statusOutput, _statusPackets });

        Controls.Add(split);
        Controls.Add(_settings);
        Controls.Add(_status);

        _tips.SetToolTip(_txtRouted, "Text shown when the receiver is routed.\r\n" +
                                     "Tokens: " + string.Join(" ", LabelFormatter.Tokens) + "\r\n" +
                                     "Use | for fallbacks, e.g. {sender.label|sender.id|\"NO NAME\"}");
        _tips.SetToolTip(_txtUnrouted, "Text shown when the receiver has no active route.");
        _tips.SetToolTip(_grid, "The template override column applies to that row alone, routed or not.");
        _tips.SetToolTip(_numPoll, "How often the registry is re-read.");
        _tips.SetToolTip(_numSendInterval,
            "How often each display is re-sent its current label, as a keepalive.\r\n" +
            "A label that changes is sent straight away regardless of this interval.");
        _tips.SetToolTip(_chkInsecure, "Accept self-signed certificates from the registry.");
    }

    private Control BuildRegistryGroup()
    {
        var layout = NewGroupLayout();

        var discoverRow = NewFlow();
        discoverRow.Controls.Add(_rbDiscover);
        discoverRow.Controls.Add(_cmbDiscovered);
        discoverRow.Controls.Add(_btnRescan);

        var manualRow = NewFlow();
        manualRow.Controls.Add(_rbManual);
        manualRow.Controls.Add(_txtRegistry);
        manualRow.Controls.Add(_chkHttps);
        manualRow.Controls.Add(_chkInsecure);

        var optionsRow = NewFlow();
        optionsRow.Controls.Add(NewLabel("API version"));
        optionsRow.Controls.Add(_cmbApiVersion);
        optionsRow.Controls.Add(NewLabel("Poll (ms)"));
        optionsRow.Controls.Add(_numPoll);
        optionsRow.Controls.Add(_btnConnect);

        layout.Controls.Add(discoverRow);
        layout.Controls.Add(manualRow);
        layout.Controls.Add(optionsRow);
        layout.Controls.Add(_lblRegistry);

        return NewGroupBox("NMOS registry", layout);
    }

    private Control BuildOutputGroup()
    {
        var layout = NewGroupLayout();

        var destination = NewFlow();
        destination.Controls.Add(NewLabel("Destination"));
        destination.Controls.Add(_txtTslHost);
        destination.Controls.Add(NewLabel("Port"));
        destination.Controls.Add(_numTslPort);
        destination.Controls.Add(_rbUdp);
        destination.Controls.Add(_rbTcp);

        var protocol = NewFlow();
        protocol.Controls.Add(NewLabel("Protocol"));
        protocol.Controls.Add(_cmbVersion);
        protocol.Controls.Add(NewLabel("Screen"));
        protocol.Controls.Add(_numScreen);
        protocol.Controls.Add(_chkScreenBroadcast);

        var options = NewFlow();
        options.Controls.Add(_chkUnicode);
        options.Controls.Add(_chkFraming);

        var tally = NewFlow();
        tally.Controls.Add(NewLabel("Lamps: routed"));
        tally.Controls.Add(_cmbRoutedLamp);
        tally.Controls.Add(NewLabel("unrouted"));
        tally.Controls.Add(_cmbUnroutedLamp);
        tally.Controls.Add(_chkTextTally);

        var timing = NewFlow();
        timing.Controls.Add(NewLabel("Repeat every (ms)"));
        timing.Controls.Add(_numSendInterval);
        timing.Controls.Add(NewLabel("Brightness"));
        timing.Controls.Add(_cmbBrightness);
        timing.Controls.Add(_btnOutput);

        layout.Controls.Add(destination);
        layout.Controls.Add(protocol);
        layout.Controls.Add(options);
        layout.Controls.Add(tally);
        layout.Controls.Add(timing);

        return NewGroupBox("TSL output", layout);
    }

    private Control BuildLabelGroup()
    {
        var layout = NewGroupLayout();

        var routed = NewFlow();
        routed.Controls.Add(NewLabel("Routed", LabelColumnWidth));
        routed.Controls.Add(_txtRouted);

        var unrouted = NewFlow();
        unrouted.Controls.Add(NewLabel("Unrouted", LabelColumnWidth));
        unrouted.Controls.Add(_txtUnrouted);

        var fit = NewFlow();
        fit.Controls.Add(NewLabel("Too long", LabelColumnWidth));
        fit.Controls.Add(_cmbFit);
        fit.Controls.Add(_chkUppercase);

        layout.Controls.Add(routed);
        layout.Controls.Add(unrouted);
        layout.Controls.Add(fit);
        layout.Controls.Add(_lblFitNote);

        return NewGroupBox("Label", layout);
    }

    private Control BuildMappingPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 6, 6, 0) };

        _grid.Columns.AddRange(_colEnabled, _colAddress, _colReceiver, _colSource, _colText, _colTemplate);

        var toolbar = NewFlow();
        toolbar.Dock = DockStyle.Top;
        toolbar.Controls.Add(_btnAddReceivers);
        toolbar.Controls.Add(_btnAddRow);
        toolbar.Controls.Add(_btnRemove);
        toolbar.Controls.Add(_btnSaveConfig);
        toolbar.Controls.Add(_btnExport);
        toolbar.Controls.Add(_btnImport);

        panel.Controls.Add(_grid);
        panel.Controls.Add(toolbar);
        return panel;
    }

    private GroupBox NewGroupBox(string text, Control content)
    {
        var box = new GroupBox
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, Font.Height + 8, 10, 10),
            Margin = new Padding(3, 3, 6, 3)
        };

        box.Controls.Add(content);

        // A group box draws its caption inside its own client area, and an undocked child is
        // placed by its Location alone - the parent's Padding does not move it. Left at the
        // default (0,0) the content paints straight over the caption, so place it by hand.
        content.Location = new Point(box.Padding.Left, box.Padding.Top);
        return box;
    }

    /// <summary>
    /// The stack of rows inside a group box. Deliberately not docked: a docked child inside an
    /// AutoSize parent has to know the parent's width to lay itself out, while the parent is
    /// waiting on the child to work out how wide it should be, and the group collapses to
    /// nothing. Undocked and AutoSize, the child measures itself and the group follows.
    /// </summary>
    private static FlowLayoutPanel NewGroupLayout() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Margin = new Padding(0)
    };

    private static FlowLayoutPanel NewFlow() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        Margin = new Padding(0, 2, 0, 2)
    };

    private static Label NewLabel(string text, int width = 0) => new()
    {
        Text = text,
        AutoSize = width == 0,
        Width = width,
        Margin = new Padding(3, 6, 3, 3)
    };

    // ---------------------------------------------------------------- events

    private void WireEvents()
    {
        _rbDiscover.CheckedChanged += (_, _) => UpdateEnabledState();
        _btnRescan.Click += (_, _) => Rescan();
        _btnConnect.Click += (_, _) => ToggleRegistry();
        _btnOutput.Click += (_, _) => ToggleOutput();

        _cmbVersion.SelectedIndexChanged += (_, _) => OnVersionChanged();
        _rbTcp.CheckedChanged += (_, _) => { ResetSender(); UpdateEnabledState(); };
        _txtTslHost.TextChanged += (_, _) => ResetSender();
        _numTslPort.ValueChanged += (_, _) => ResetSender();
        _numSendInterval.ValueChanged += (_, _) => UpdateSendPacing();

        foreach (var control in new Control[] { _txtRouted, _txtUnrouted })
            control.TextChanged += (_, _) => RefreshRows(send: false);

        _cmbFit.SelectedIndexChanged += (_, _) => RefreshRows(send: false);
        _chkUppercase.CheckedChanged += (_, _) => RefreshRows(send: false);
        _cmbRoutedLamp.SelectedIndexChanged += (_, _) => RefreshRows(send: false);
        _cmbUnroutedLamp.SelectedIndexChanged += (_, _) => RefreshRows(send: false);
        _chkScreenBroadcast.CheckedChanged += (_, _) => _numScreen.Enabled = !_chkScreenBroadcast.Checked;

        _btnAddReceivers.Click += (_, _) => AddReceivers();
        _btnAddRow.Click += (_, _) => AddRow(new Assignment { Address = NextAddress() });
        _btnRemove.Click += (_, _) => RemoveSelectedRows();
        _btnSaveConfig.Click += (_, _) => SaveConfig(null, announce: true);
        _btnExport.Click += (_, _) => ExportConfig();
        _btnImport.Click += (_, _) => ImportConfig();

        _grid.CellValueChanged += OnCellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValidating += OnCellValidating;
        // A stale receiver id in a cell must not throw a modal dialog once a second.
        _grid.DataError += (_, e) => e.ThrowException = false;

        _sendTimer.Tick += (_, _) => SendNextDisplay();

        _registry.StateChanged += (state, message) => BeginInvoke(() => OnRegistryState(state, message));
        _registry.SnapshotUpdated += snapshot => BeginInvoke(() => OnSnapshot(snapshot));
        _registry.Log += message => BeginInvoke(() => Log(message));

        _mdns.Updated += () => BeginInvoke(RefreshDiscoveryList);

        Load += (_, _) => OnFormLoad();
        FormClosing += (_, _) => OnFormClosing();
    }

    private void OnFormLoad()
    {
        _autoConnectPending = _config.AutoStart;
        _autoOutputPending = _config.AutoStart;

        Log(_config.AutoStart
            ? "NMOS UMD started. Browsing for registries; will connect and start output automatically."
            : "NMOS UMD started. Browsing for registries...");

        _mdns.Start();

        // A typed address needs no discovery, so it can be tried at once.
        if (!_rbDiscover.Checked) TryAutoConnect();
    }

    /// <summary>
    /// Connects without prompting, as soon as there is something to connect to. Called when
    /// discovery turns up a registry, and at start-up for a manually typed address.
    /// </summary>
    private void TryAutoConnect()
    {
        if (!_autoConnectPending || _registry.IsRunning) return;
        if (StartRegistry(silent: true)) _autoConnectPending = false;
    }

    /// <summary>
    /// Starts the output once the registry has answered and there is something mapped to send.
    /// Retried on a cooldown rather than once, so a destination that is not listening yet -
    /// a multiviewer still booting - is picked up when it appears.
    /// </summary>
    private void TryAutoStartOutput()
    {
        if (!_autoOutputPending || _outputRunning || _loading) return;
        if (SendableRows().Count == 0) return;
        if (DateTime.UtcNow - _lastAutoOutputAttempt < TimeSpan.FromSeconds(5)) return;

        _lastAutoOutputAttempt = DateTime.UtcNow;
        StartOutput(silent: true);
    }

    private void OnFormClosing()
    {
        StopOutput(userRequested: true);
        _registry.Dispose();
        _mdns.Dispose();
        SaveConfig(null, announce: false);
    }

    private void OnVersionChanged()
    {
        var version = SelectedVersion();
        var max = UmdEngine.MaxAddress(version);

        _lblFitNote.Text = version == TslVersion.V50
            ? "V5.0 has no text length limit."
            : $"V3.1 and V4.0 carry {TslPacketBuilder.V31TextLength} ASCII characters.";

        var overflow = Assignments().Where(a => a.Address > max).ToList();
        if (overflow.Count > 0)
        {
            Log($"Warning: {overflow.Count} address(es) exceed {max}, the maximum for this protocol version " +
                $"({string.Join(", ", overflow.Take(8).Select(a => a.Address))}). Those rows will not be sent.");
        }

        RefreshRows(send: false);
        UpdateEnabledState();
    }

    private void OnCellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (e.ColumnIndex != _colAddress.Index) return;

        var text = Convert.ToString(e.FormattedValue, CultureInfo.InvariantCulture) ?? string.Empty;
        var max = UmdEngine.MaxAddress(SelectedVersion());

        if (!int.TryParse(text, out var address) || address < 0 || address > max)
        {
            Log($"Address must be a whole number between 0 and {max} for the selected protocol version.");
            e.Cancel = true;
        }
    }

    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0) return;

        var row = _grid.Rows[e.RowIndex];
        if (row.Tag is not Assignment assignment) return;

        if (e.ColumnIndex == _colEnabled.Index)
            assignment.Enabled = row.Cells[_colEnabled.Index].Value is true;
        else if (e.ColumnIndex == _colAddress.Index)
            assignment.Address = ParseInt(row.Cells[_colAddress.Index].Value, assignment.Address);
        else if (e.ColumnIndex == _colReceiver.Index)
            SetReceiver(assignment, Convert.ToString(row.Cells[_colReceiver.Index].Value) ?? string.Empty);
        else if (e.ColumnIndex == _colTemplate.Index)
            assignment.TemplateOverride = Convert.ToString(row.Cells[_colTemplate.Index].Value) ?? string.Empty;

        assignment.HasSent = false; // force a refresh of that display
        RefreshRows(send: false);
    }

    private void SetReceiver(Assignment assignment, string receiverId)
    {
        assignment.ReceiverId = receiverId;
        var receiver = _snapshot.Receiver(receiverId);
        if (receiver is not null) assignment.ReceiverLabel = receiver.DisplayName;
    }

    // ---------------------------------------------------------------- registry

    private void Rescan()
    {
        Log("Rescanning for NMOS registries...");
        _mdns.Clear();
        _mdns.Refresh();
    }

    private void RefreshDiscoveryList()
    {
        var services = _mdns.Services()
            .Where(s => s.ServiceType == MdnsBrowser.QueryServiceType)
            .ToList();

        var selectedInstance = (_cmbDiscovered.SelectedItem as MdnsService)?.Instance;

        _cmbDiscovered.BeginUpdate();
        _cmbDiscovered.Items.Clear();
        foreach (var service in services) _cmbDiscovered.Items.Add(service);
        _cmbDiscovered.EndUpdate();

        if (services.Count == 0)
        {
            _cmbDiscovered.Text = string.Empty;
            return;
        }

        var index = selectedInstance is null
            ? 0
            : Math.Max(0, services.FindIndex(s => s.Instance == selectedInstance));
        _cmbDiscovered.SelectedIndex = index;

        // Registration-only registries are worth a word: the address is right but the Query API
        // this tool needs is advertised separately, and its port is usually different.
        var others = _mdns.Services().Count(s => s.ServiceType != MdnsBrowser.QueryServiceType);
        _statusRegistry.Text = $"Registry: {services.Count} Query API found" + (others > 0 ? $", {others} other NMOS service(s)" : string.Empty);

        TryAutoConnect();
    }

    private void ToggleRegistry()
    {
        if (_registry.IsRunning)
        {
            // An explicit disconnect stays disconnected.
            _autoConnectPending = false;
            _registry.Stop();
            _btnConnect.Text = "Connect";
            UpdateEnabledState();
            return;
        }

        StartRegistry(silent: false);
    }

    /// <summary>
    /// Starts the registry monitor. In silent mode nothing is asked of the operator - it simply
    /// reports whether there was enough to go on.
    /// </summary>
    private bool StartRegistry(bool silent)
    {
        string address;
        var https = false;

        if (_rbDiscover.Checked)
        {
            if (_cmbDiscovered.SelectedItem is not MdnsService service)
            {
                if (!silent)
                    MessageBox.Show(this, "No registry discovered yet. Wait for the scan, or switch to Manual and type the address.",
                        "NMOS UMD", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            if (!service.IsResolved)
            {
                if (!silent)
                    MessageBox.Show(this, "That registry has been announced but not resolved to an address yet. Try Rescan.",
                        "NMOS UMD", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            address = service.HostPort;
            https = service.ApiProto.Equals("https", StringComparison.OrdinalIgnoreCase);
            Log($"Using discovered registry {service.DisplayName} at {address} (api_ver {service.VersionSummary}).");
        }
        else
        {
            address = _txtRegistry.Text.Trim();
            if (address.Length == 0)
            {
                if (!silent)
                    MessageBox.Show(this, "Type the registry address, e.g. 10.0.0.5:8235.",
                        "NMOS UMD", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            https = _chkHttps.Checked;
        }

        var version = _cmbApiVersion.SelectedIndex <= 0 ? null : Convert.ToString(_cmbApiVersion.SelectedItem);

        _registry.Start(address, https, _chkInsecure.Checked, version, (int)_numPoll.Value);
        _btnConnect.Text = "Disconnect";
        UpdateEnabledState();
        return true;
    }

    private void OnRegistryState(RegistryState state, string message)
    {
        _lblRegistry.Text = message;
        _lblRegistry.ForeColor = state switch
        {
            RegistryState.Connected => Color.FromArgb(0, 110, 0),
            RegistryState.Error => Color.FromArgb(170, 0, 0),
            _ => SystemColors.ControlText
        };

        _statusRegistry.Text = "Registry: " + state switch
        {
            RegistryState.Connected => "connected",
            RegistryState.Connecting => "connecting",
            RegistryState.Error => "error",
            _ => "idle"
        };

        Log(message);

        if (state is RegistryState.Stopped or RegistryState.Error)
        {
            _snapshot = state == RegistryState.Stopped ? RegistrySnapshot.Empty : _snapshot;
            RefreshRows(send: false);
        }
    }

    private void OnSnapshot(RegistrySnapshot snapshot)
    {
        var previous = _snapshot;
        _snapshot = snapshot;

        RebuildReceiverOptions(force: false);
        RefreshRows(send: false);
        LogRouteChanges(previous, snapshot);
        TryAutoStartOutput();
    }

    /// <summary>Notes route changes in the log, which is the record of what the displays were told.</summary>
    private void LogRouteChanges(RegistrySnapshot previous, RegistrySnapshot current)
    {
        if (previous.Receivers.Count == 0) return;

        foreach (var assignment in Assignments())
        {
            if (string.IsNullOrEmpty(assignment.ReceiverId)) continue;

            var before = previous.Receiver(assignment.ReceiverId);
            var after = current.Receiver(assignment.ReceiverId);
            if (before is null || after is null) continue;
            if (before.SenderId == after.SenderId && before.SubscriptionActive == after.SubscriptionActive) continue;

            var beforeName = NameOfSender(previous, before);
            var afterName = NameOfSender(current, after);
            Log($"Address {assignment.Address} ({after.DisplayName}): {beforeName} -> {afterName}");
        }
    }

    private static string NameOfSender(RegistrySnapshot snapshot, NmosReceiver receiver)
    {
        if (!receiver.SubscriptionActive || string.IsNullOrEmpty(receiver.SenderId)) return "not routed";
        return snapshot.Senders.TryGetValue(receiver.SenderId!, out var sender)
            ? sender.DisplayName
            : receiver.SenderId!;
    }

    // ---------------------------------------------------------------- mapping table

    private IEnumerable<Assignment> Assignments() =>
        _grid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<Assignment>();

    private void AddRow(Assignment assignment)
    {
        var index = _grid.Rows.Add();
        var row = _grid.Rows[index];
        row.Tag = assignment;
        WriteRow(row, assignment);
    }

    private void WriteRow(DataGridViewRow row, Assignment assignment)
    {
        var wasLoading = _loading;
        _loading = true;
        row.Cells[_colEnabled.Index].Value = assignment.Enabled;
        row.Cells[_colAddress.Index].Value = assignment.Address;
        row.Cells[_colReceiver.Index].Value = assignment.ReceiverId;
        row.Cells[_colTemplate.Index].Value = assignment.TemplateOverride;
        _loading = wasLoading;
    }

    private int NextAddress()
    {
        var used = Assignments().Select(a => a.Address).ToHashSet();
        var max = UmdEngine.MaxAddress(SelectedVersion());
        for (var address = 1; address <= max; address++)
            if (!used.Contains(address)) return address;
        return 1;
    }

    private void AddReceivers()
    {
        if (_snapshot.Receivers.Count == 0)
        {
            MessageBox.Show(this, "Connect to a registry first - there are no receivers to choose from.",
                "Add receivers", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var picker = new ReceiverPickerForm(_snapshot, NextAddress(), UmdEngine.MaxAddress(SelectedVersion()));
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        if (picker.ReplaceExisting) _grid.Rows.Clear();

        var address = picker.FirstAddress;
        var max = UmdEngine.MaxAddress(SelectedVersion());

        foreach (var receiver in picker.SelectedReceivers)
        {
            if (address > max)
            {
                Log($"Stopped at address {max}: the selected protocol version addresses no higher.");
                break;
            }

            AddRow(new Assignment
            {
                Address = address++,
                ReceiverId = receiver.Id,
                ReceiverLabel = receiver.DisplayName
            });
        }

        RebuildReceiverOptions(force: true);
        RefreshRows(send: false);
        Log($"Mapped {picker.SelectedReceivers.Count} receiver(s) from address {picker.FirstAddress}.");
    }

    private void RemoveSelectedRows()
    {
        var rows = _grid.SelectedRows.Cast<DataGridViewRow>().ToList();
        if (rows.Count == 0 && _grid.CurrentRow is not null) rows.Add(_grid.CurrentRow);

        foreach (var row in rows) _grid.Rows.Remove(row);
        RefreshRows(send: false);
    }

    /// <summary>
    /// Refreshes the drop-down contents. Rebuilt only when the receiver set actually changes,
    /// so a once-a-second poll does not close a drop-down the user is reading.
    /// </summary>
    private void RebuildReceiverOptions(bool force)
    {
        var options = new List<ReceiverOption> { new() { Id = string.Empty, Display = "(none)" } };

        foreach (var receiver in _snapshot.ReceiversByName)
        {
            var device = _snapshot.DeviceNameOf(receiver);
            options.Add(new ReceiverOption
            {
                Id = receiver.Id,
                Display = device.Length > 0 ? $"{receiver.DisplayName}  -  {device}" : receiver.DisplayName
            });
        }

        // Mapped receivers the registry does not currently hold still need an entry, or the grid
        // cannot display the value it is holding.
        foreach (var assignment in Assignments())
        {
            if (string.IsNullOrEmpty(assignment.ReceiverId)) continue;
            if (options.Any(o => o.Id == assignment.ReceiverId)) continue;

            var name = string.IsNullOrWhiteSpace(assignment.ReceiverLabel) ? assignment.ReceiverId : assignment.ReceiverLabel;
            options.Add(new ReceiverOption { Id = assignment.ReceiverId, Display = $"{name}  -  (offline)" });
        }

        var key = string.Join("", options.Select(o => o.Id + "=" + o.Display));
        if (!force && key == _receiverOptionsKey) return;
        _receiverOptionsKey = key;

        var wasLoading = _loading;
        _loading = true;

        var values = _grid.Rows.Cast<DataGridViewRow>()
            .Select(r => Convert.ToString(r.Cells[_colReceiver.Index].Value) ?? string.Empty)
            .ToList();

        _colReceiver.DataSource = null;
        _colReceiver.Items.Clear();
        _colReceiver.DataSource = options;
        _colReceiver.DisplayMember = nameof(ReceiverOption.Display);
        _colReceiver.ValueMember = nameof(ReceiverOption.Id);

        for (var i = 0; i < _grid.Rows.Count; i++)
            _grid.Rows[i].Cells[_colReceiver.Index].Value = values[i];

        _loading = wasLoading;
    }

    // ---------------------------------------------------------------- output

    private void ToggleOutput()
    {
        if (_outputRunning)
        {
            StopOutput(userRequested: true);
        }
        else
        {
            _autoOutputPending = true;
            StartOutput(silent: false);
        }
    }

    private void StartOutput(bool silent)
    {
        ReadConfigFromControls();

        if (Assignments().All(a => !a.Enabled || string.IsNullOrEmpty(a.ReceiverId)))
        {
            if (!silent)
                MessageBox.Show(this, "Map at least one receiver to a UMD address first.",
                    "NMOS UMD", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            EnsureSender();
        }
        catch (Exception ex)
        {
            // Silent means this was the automatic start; say so once and let the cooldown retry.
            Log($"Cannot open the TSL destination: {ex.Message}{(silent ? " Retrying." : string.Empty)}");
            if (!silent)
                MessageBox.Show(this, ex.Message, "TSL output", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        foreach (var assignment in Assignments()) assignment.HasSent = false;

        _outputRunning = true;
        _sendCursor = 0;
        UpdateSendPacing();
        _sendTimer.Start();
        _btnOutput.Text = "Stop output";
        _statusOutput.Text = $"Output: running to {_sender!.Description}";
        var displays = SendableRows().Count;
        Log($"Output started to {_sender.Description} using {VersionName(_config.Version)}, " +
            $"{displays} display(s) repeating every {(int)_numSendInterval.Value} ms " +
            $"({_sendTimer.Interval} ms apart); changes sent immediately.");
        UpdateEnabledState();

        RefreshRows(send: true);
    }

    private void StopOutput(bool userRequested)
    {
        // A failed send stops the output but leaves the automatic restart armed; an operator
        // pressing Stop means stopped.
        if (userRequested) _autoOutputPending = false;

        if (!_outputRunning) return;

        _sendTimer.Stop();
        _outputRunning = false;
        _btnOutput.Text = "Start output";
        _statusOutput.Text = "Output: stopped";
        Log("Output stopped.");
        UpdateEnabledState();
    }

    private void EnsureSender()
    {
        var host = _txtTslHost.Text.Trim();
        var port = (int)_numTslPort.Value;
        var tcp = _rbTcp.Checked;
        var key = $"{(tcp ? "tcp" : "udp")}|{host}|{port}";

        if (_sender is not null && _senderKey == key) return;

        ResetSender();

        var endpoint = new IPEndPoint(ResolveHost(host), port);
        if (tcp)
        {
            var sender = new TcpPacketSender(endpoint);
            sender.Connect();
            _sender = sender;
        }
        else
        {
            _sender = new UdpPacketSender(endpoint);
        }

        _senderKey = key;
    }

    private static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out var address)) return address;

        var resolved = Dns.GetHostAddresses(host)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        return resolved ?? throw new InvalidOperationException($"Could not resolve '{host}' to an IPv4 address.");
    }

    private void ResetSender()
    {
        _sender?.Dispose();
        _sender = null;
        _senderKey = string.Empty;
    }

    /// <summary>
    /// Recomputes every row from the current registry snapshot so the table shows what each
    /// display should be saying. Sending is done separately, one display at a time, by
    /// <see cref="SendNextDisplay"/>.
    /// </summary>
    private void RefreshRows(bool send)
    {
        if (_loading) return;

        ReadConfigFromControls();

        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (gridRow.Tag is not Assignment) continue;
            BuildAndShow(gridRow);
        }

        UpdateSendPacing();

        // A settings or routing change should reach the wire promptly rather than waiting for
        // this display's turn to come round again.
        if (send && _outputRunning) SendNextDisplay();
    }

    /// <summary>Recomputes one row and writes the result into its cells.</summary>
    private UmdRow? BuildAndShow(DataGridViewRow gridRow)
    {
        if (gridRow.Tag is not Assignment assignment) return null;

        var row = UmdEngine.Build(assignment, _snapshot, _config);
        assignment.Dirty = UmdEngine.HasChanged(row);

        SetCell(gridRow.Cells[_colSource.Index], row.SourceText);
        SetCell(gridRow.Cells[_colText.Index], row.Message.Text);

        // Routed reads as normal text, an idle receiver as amber, and one the registry has
        // lost as red - the last of those is a fault, not a state.
        gridRow.Cells[_colSource.Index].Style.ForeColor = row.ReceiverOffline
            ? Color.FromArgb(170, 0, 0)
            : row.Routed ? SystemColors.ControlText : Color.FromArgb(150, 90, 0);

        return row;
    }

    /// <summary>The rows that are switched on and actually point at a receiver.</summary>
    private List<DataGridViewRow> SendableRows() =>
        _grid.Rows.Cast<DataGridViewRow>()
             .Where(r => r.Tag is Assignment { Enabled: true } a && !string.IsNullOrEmpty(a.ReceiverId))
             .ToList();

    /// <summary>
    /// Sends one display per tick, working round the list, so a full pass takes the configured
    /// interval.
    ///
    /// Sending every display in one burst looks harmless but is not: over TCP the packets
    /// coalesce into a single read at the far end, and a receiver that treats each read as one
    /// message - Companion's TSL UMD listener does exactly this - parses the first packet of the
    /// burst and discards the rest. Spacing them out costs nothing and keeps every display fed.
    /// </summary>
    private void SendNextDisplay()
    {
        if (!_outputRunning || _loading) return;

        var rows = SendableRows();
        if (rows.Count == 0) return;

        // A display whose label has just changed goes first; the rest of the time this works
        // steadily round the list, refreshing one display per tick. Changed displays still go
        // one per tick rather than all at once, so a re-route of the whole wall does not turn
        // into the burst that TCP receivers mis-read.
        var gridRow = rows.FirstOrDefault(r => r.Tag is Assignment { Dirty: true });

        if (gridRow is null)
        {
            if (_sendCursor >= rows.Count) _sendCursor = 0;
            gridRow = rows[_sendCursor];
            _sendCursor = (_sendCursor + 1) % rows.Count;
        }

        var row = BuildAndShow(gridRow);
        if (row is null) return;

        if (row.Assignment.Address > UmdEngine.MaxAddress(_config.Version)) return; // warned already

        try
        {
            _sender!.Send(UmdEngine.BuildPacket(row, _config));
            UmdEngine.MarkSent(row);
            _packetsSent++;
            _statusPackets.Text = $"{_packetsSent} packets";
        }
        catch (Exception ex)
        {
            Log($"Send failed: {ex.Message}");
            StopOutput(userRequested: false);
            ResetSender();
        }
    }

    /// <summary>
    /// Divides the send interval between the displays in use, so each one is refreshed once per
    /// interval and no two packets leave back to back.
    /// </summary>
    private void UpdateSendPacing()
    {
        var count = Math.Max(1, SendableRows().Count);
        var interval = (int)_numSendInterval.Value;

        // 10 ms is about as fine as a WinForms timer resolves; past roughly a hundred displays
        // the cycle simply takes longer than the interval asks for.
        _sendTimer.Interval = Math.Max(10, interval / count);
    }

    private static void SetCell(DataGridViewCell cell, string value)
    {
        if (Convert.ToString(cell.Value) == value) return;
        cell.Value = value;
    }

    // ---------------------------------------------------------------- config

    private static AppConfig LoadConfigSafely()
    {
        try { return ConfigStore.Load(); }
        catch { return new AppConfig(); }
    }

    private void ApplyConfig()
    {
        _loading = true;

        _rbDiscover.Checked = _config.UseDiscovery;
        _rbManual.Checked = !_config.UseDiscovery;
        _txtRegistry.Text = _config.RegistryAddress;
        _chkHttps.Checked = _config.RegistryHttps;
        _chkInsecure.Checked = _config.AllowInvalidCertificates;
        _cmbApiVersion.SelectedIndex = string.IsNullOrWhiteSpace(_config.ApiVersion)
            ? 0
            : Math.Max(0, _cmbApiVersion.Items.IndexOf(_config.ApiVersion));
        _numPoll.Value = Clamp(_config.PollIntervalMs, _numPoll);

        _txtTslHost.Text = _config.TslHost;
        _numTslPort.Value = Clamp(_config.TslPort, _numTslPort);
        _rbTcp.Checked = _config.UseTcp;
        _rbUdp.Checked = !_config.UseTcp;
        _chkFraming.Checked = _config.StreamFraming;
        _cmbVersion.SelectedIndex = _config.Version switch
        {
            TslVersion.V31 => 0,
            TslVersion.V40 => 1,
            _ => 2
        };
        _numScreen.Value = Clamp(_config.Screen, _numScreen);
        _chkScreenBroadcast.Checked = _config.ScreenBroadcast;
        _numScreen.Enabled = !_config.ScreenBroadcast;
        _chkUnicode.Checked = _config.Unicode;
        _numSendInterval.Value = Clamp(_config.SendIntervalMs, _numSendInterval);
        _cmbRoutedLamp.SelectedItem = _config.RoutedLamp;
        _cmbUnroutedLamp.SelectedItem = _config.UnroutedLamp;
        _chkTextTally.Checked = _config.DriveTextTally;
        _cmbBrightness.SelectedIndex = Math.Clamp(_config.Brightness, 0, 3);

        _txtRouted.Text = _config.RoutedTemplate;
        _txtUnrouted.Text = _config.UnroutedTemplate;
        _cmbFit.SelectedIndex = (int)_config.FitMode;
        _chkUppercase.Checked = _config.Uppercase;

        _grid.Rows.Clear();
        foreach (var assignment in _config.Assignments) AddRow(assignment.Clone());

        _loading = false;

        OnVersionChanged();
    }

    private void ReadConfigFromControls()
    {
        _config.UseDiscovery = _rbDiscover.Checked;
        _config.RegistryAddress = _rbDiscover.Checked && _cmbDiscovered.SelectedItem is MdnsService service
            ? service.HostPort
            : _txtRegistry.Text.Trim();
        _config.RegistryHttps = _chkHttps.Checked;
        _config.AllowInvalidCertificates = _chkInsecure.Checked;
        _config.ApiVersion = _cmbApiVersion.SelectedIndex <= 0 ? string.Empty : Convert.ToString(_cmbApiVersion.SelectedItem) ?? string.Empty;
        _config.PollIntervalMs = (int)_numPoll.Value;

        _config.TslHost = _txtTslHost.Text.Trim();
        _config.TslPort = (int)_numTslPort.Value;
        _config.UseTcp = _rbTcp.Checked;
        _config.StreamFraming = _chkFraming.Checked;
        _config.Version = SelectedVersion();
        _config.Screen = (int)_numScreen.Value;
        _config.ScreenBroadcast = _chkScreenBroadcast.Checked;
        _config.Unicode = _chkUnicode.Checked;
        _config.SendIntervalMs = (int)_numSendInterval.Value;
        _config.RoutedLamp = _cmbRoutedLamp.SelectedItem is TallyColour routed ? routed : TallyColour.Off;
        _config.UnroutedLamp = _cmbUnroutedLamp.SelectedItem is TallyColour unrouted ? unrouted : TallyColour.Off;
        _config.DriveTextTally = _chkTextTally.Checked;
        _config.Brightness = Math.Max(0, _cmbBrightness.SelectedIndex);

        _config.RoutedTemplate = _txtRouted.Text;
        _config.UnroutedTemplate = _txtUnrouted.Text;
        _config.FitMode = (FitMode)Math.Max(0, _cmbFit.SelectedIndex);
        _config.Uppercase = _chkUppercase.Checked;

        _config.Assignments = Assignments().Select(a => a.Clone()).ToList();
    }

    private void SaveConfig(string? path, bool announce)
    {
        try
        {
            ReadConfigFromControls();
            ConfigStore.Save(_config, path);
            if (announce) Log($"Saved to {path ?? ConfigStore.DefaultPath}");
        }
        catch (Exception ex)
        {
            Log($"Save failed: {ex.Message}");
            if (announce)
                MessageBox.Show(this, ex.Message, "Save", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExportConfig()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "NMOS UMD configuration (*.json)|*.json|All files (*.*)|*.*",
            FileName = "nmos-umd.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SaveConfig(dialog.FileName, announce: true);
    }

    private void ImportConfig()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "NMOS UMD configuration (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var loaded = ConfigStore.Load(dialog.FileName);
            CopyInto(loaded, _config);
            ApplyConfig();
            RebuildReceiverOptions(force: true);
            RefreshRows(send: false);
            Log($"Loaded {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log($"Load failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void CopyInto(AppConfig from, AppConfig to)
    {
        to.UseDiscovery = from.UseDiscovery;
        to.RegistryAddress = from.RegistryAddress;
        to.RegistryHttps = from.RegistryHttps;
        to.AllowInvalidCertificates = from.AllowInvalidCertificates;
        to.ApiVersion = from.ApiVersion;
        to.PollIntervalMs = from.PollIntervalMs;
        to.TslHost = from.TslHost;
        to.TslPort = from.TslPort;
        to.UseTcp = from.UseTcp;
        to.StreamFraming = from.StreamFraming;
        to.Version = from.Version;
        to.Screen = from.Screen;
        to.ScreenBroadcast = from.ScreenBroadcast;
        to.Unicode = from.Unicode;
        to.SendIntervalMs = from.SendIntervalMs;
        to.AutoStart = from.AutoStart;
        to.RoutedLamp = from.RoutedLamp;
        to.UnroutedLamp = from.UnroutedLamp;
        to.DriveTextTally = from.DriveTextTally;
        to.Brightness = from.Brightness;
        to.RoutedTemplate = from.RoutedTemplate;
        to.UnroutedTemplate = from.UnroutedTemplate;
        to.FitMode = from.FitMode;
        to.Uppercase = from.Uppercase;
        to.Assignments = from.Assignments;
    }

    // ---------------------------------------------------------------- helpers

    private TslVersion SelectedVersion() => _cmbVersion.SelectedIndex switch
    {
        0 => TslVersion.V31,
        1 => TslVersion.V40,
        _ => TslVersion.V50
    };

    private static string VersionName(TslVersion version) => version switch
    {
        TslVersion.V31 => "TSL V3.1",
        TslVersion.V40 => "TSL V4.0",
        _ => "TSL V5.0"
    };

    private void UpdateEnabledState()
    {
        var discovering = _rbDiscover.Checked;
        _cmbDiscovered.Enabled = discovering;
        _btnRescan.Enabled = discovering;
        _txtRegistry.Enabled = !discovering;
        _chkHttps.Enabled = !discovering;
        _chkInsecure.Enabled = true;

        var connected = _registry.IsRunning;
        _rbDiscover.Enabled = !connected;
        _rbManual.Enabled = !connected;
        _cmbApiVersion.Enabled = !connected;
        _numPoll.Enabled = !connected;

        var version = SelectedVersion();
        _numScreen.Enabled = version == TslVersion.V50 && !_chkScreenBroadcast.Checked;
        _chkScreenBroadcast.Enabled = version == TslVersion.V50;
        _chkUnicode.Enabled = version == TslVersion.V50;
        _chkFraming.Enabled = _rbTcp.Checked;

        _txtTslHost.Enabled = !_outputRunning;
        _numTslPort.Enabled = !_outputRunning;
        _rbUdp.Enabled = !_outputRunning;
        _rbTcp.Enabled = !_outputRunning;
    }

    private static int ParseInt(object? value, int fallback) =>
        int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) ? parsed : fallback;

    private static decimal Clamp(int value, NumericUpDown control) =>
        Math.Clamp(value, (int)control.Minimum, (int)control.Maximum);

    private void Log(string message)
    {
        if (IsDisposed) return;

        var line = $"{DateTime.Now:HH:mm:ss}  {message}";

        if (_txtLog.Lines.Length > MaxLogLines)
            _txtLog.Lines = _txtLog.Lines.Skip(_txtLog.Lines.Length - MaxLogLines / 2).ToArray();

        _txtLog.AppendText(line + Environment.NewLine);
    }
}

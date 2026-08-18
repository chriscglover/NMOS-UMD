using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NmosUmd.Nmos;

namespace NmosUmd;

/// <summary>
/// Picks receivers out of the registry and lays them onto consecutive UMD addresses.
///
/// A multiviewer commonly registers thirty-odd receivers whose labels already run in input
/// order, so mapping them one row at a time is tedious and easy to get out of step. This does
/// the whole wall in one go, and the filter box narrows it to a single multiviewer first.
/// </summary>
public sealed class ReceiverPickerForm : Form
{
    private readonly RegistrySnapshot _snapshot;
    private readonly List<NmosReceiver> _receivers;

    private readonly TextBox _txtFilter = new() { Width = 240 };
    private readonly ListView _list = new()
    {
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        Dock = DockStyle.Fill
    };

    private readonly NumericUpDown _numFirstAddress = new() { Minimum = 0, Maximum = 65534, Value = 1, Width = 80 };
    private readonly CheckBox _chkReplace = new() { Text = "Replace the existing mappings", AutoSize = true };
    private readonly Label _lblCount = new() { AutoSize = true };

    public ReceiverPickerForm(RegistrySnapshot snapshot, int firstAddress, int maxAddress)
    {
        _snapshot = snapshot;
        _receivers = snapshot.ReceiversByName.ToList();

        Text = "Add receivers";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? DefaultFont;
        ClientSize = new Size(820, 520);
        MinimumSize = new Size(600, 400);

        _numFirstAddress.Maximum = maxAddress;
        _numFirstAddress.Value = Math.Min(Math.Max(firstAddress, 0), maxAddress);

        BuildLayout();
        Populate();

        _txtFilter.TextChanged += (_, _) => Populate();
        _list.ItemChecked += (_, _) => UpdateCount();
    }

    /// <summary>Receivers the user ticked, in the order they are shown.</summary>
    public List<NmosReceiver> SelectedReceivers { get; } = new();

    public int FirstAddress => (int)_numFirstAddress.Value;

    public bool ReplaceExisting => _chkReplace.Checked;

    private void BuildLayout()
    {
        _list.Columns.Add("Receiver", 260);
        _list.Columns.Add("Device", 220);
        _list.Columns.Add("Format", 80);
        _list.Columns.Add("Currently routed from", 220);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 8, 8, 4),
            WrapContents = false
        };
        top.Controls.Add(new Label { Text = "Filter", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        top.Controls.Add(_txtFilter);

        var btnAll = MakeButton("Select all", (_, _) => SetChecked(_ => true));
        var btnNone = MakeButton("Select none", (_, _) => SetChecked(_ => false));
        var btnRouted = MakeButton("Routed only", (_, _) => SetChecked(item => item.SubItems[3].Text.Length > 0 &&
                                                                               !item.SubItems[3].Text.StartsWith("(")));
        top.Controls.Add(btnAll);
        top.Controls.Add(btnNone);
        top.Controls.Add(btnRouted);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 8),
            WrapContents = false
        };
        bottom.Controls.Add(new Label { Text = "First UMD address", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        bottom.Controls.Add(_numFirstAddress);
        bottom.Controls.Add(_chkReplace);
        _chkReplace.Margin = new Padding(16, 6, 6, 0);
        bottom.Controls.Add(_lblCount);
        _lblCount.Margin = new Padding(16, 6, 6, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };

        var ok = MakeButton("Add", (_, _) => Accept());
        var cancel = MakeButton("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        ok.DialogResult = DialogResult.None;
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
        host.Controls.Add(_list);

        Controls.Add(host);
        Controls.Add(bottom);
        Controls.Add(buttons);
        Controls.Add(top);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(90, 27),
            Margin = new Padding(6, 3, 0, 3)
        };
        button.Click += onClick;
        return button;
    }

    private void Populate()
    {
        var filter = _txtFilter.Text.Trim();

        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var receiver in _receivers)
        {
            var device = _snapshot.DeviceNameOf(receiver);
            var route = _snapshot.Route(receiver);
            var source = route.IsRouted ? route.Sender!.DisplayName
                       : route.Receiver.SubscriptionActive ? "(active, sender unknown)"
                       : "(not routed)";

            if (filter.Length > 0 &&
                !Contains(receiver.DisplayName, filter) &&
                !Contains(device, filter) &&
                !Contains(source, filter) &&
                !Contains(receiver.GroupHint, filter))
            {
                continue;
            }

            var item = new ListViewItem(receiver.DisplayName) { Tag = receiver };
            item.SubItems.Add(device);
            item.SubItems.Add(ShortFormat(receiver.Format));
            item.SubItems.Add(source);
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        UpdateCount();
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string ShortFormat(string urn)
    {
        var last = urn.LastIndexOf(':');
        return last >= 0 && last < urn.Length - 1 ? urn[(last + 1)..] : urn;
    }

    private void SetChecked(Func<ListViewItem, bool> predicate)
    {
        _list.BeginUpdate();
        foreach (ListViewItem item in _list.Items) item.Checked = predicate(item);
        _list.EndUpdate();
    }

    private void UpdateCount()
    {
        var count = _list.CheckedItems.Count;
        _lblCount.Text = count == 0
            ? "Nothing selected"
            : $"{count} receiver{(count == 1 ? string.Empty : "s")} -> addresses {FirstAddress}-{FirstAddress + count - 1}";
    }

    private void Accept()
    {
        SelectedReceivers.Clear();
        foreach (ListViewItem item in _list.CheckedItems)
            if (item.Tag is NmosReceiver receiver) SelectedReceivers.Add(receiver);

        if (SelectedReceivers.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one receiver.", "Add receivers",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

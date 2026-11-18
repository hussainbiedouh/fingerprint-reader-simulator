using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace WindowsFingerprintSimulator;

public sealed class MainForm : Form
{
    private readonly List<FingerprintRecord> _records;
    private readonly ImageList _thumbnails;
    private readonly List<Bitmap> _thumbnailBitmaps = new();
    private readonly TextBox _nameBox;
    private readonly Button _addButton;
    private readonly ListView _list;
    private readonly TextBox _log;
    private readonly ComboBox _modeBox;

    private const string ShimMode = "ZKTeco shim";
    private ZkShimPipe? _zkPipe;

    private bool _busy;
    private ListViewItem? _lastProbed;
    private long _lastProbeTick;

    public MainForm()
    {
        Text = "Windows Fingerprint Simulator";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(900, 520);
        MinimumSize = new Size(640, 400);

        _records = FingerprintStore.Load();
        _thumbnails = new ImageList { ImageSize = new Size(48, 48), ColorDepth = ColorDepth.Depth32Bit };

        _nameBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Fingerprint name" };
        _addButton = new Button { Dock = DockStyle.Fill, Text = "Add fingerprint" };
        _addButton.Click += AddButton_Click;

        _modeBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _modeBox.Items.AddRange(new[] { "WinBio", ShimMode });
        _modeBox.SelectedIndex = 0;

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Add a fingerprint, then click one to probe the virtual sensor.",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
        };

        var modeGroup = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        modeGroup.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        modeGroup.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        modeGroup.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Reader mode", TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        modeGroup.Controls.Add(_modeBox, 0, 1);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(10, 8, 10, 2),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(_nameBox, 0, 0);
        top.Controls.Add(_addButton, 1, 0);
        top.Controls.Add(modeGroup, 2, 0);
        top.Controls.Add(hint, 3, 0);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.LargeIcon,
            LargeImageList = _thumbnails,
            MultiSelect = false,
            HideSelection = false,
        };
        _list.Click += List_Click;
        _list.ItemActivate += List_ItemActivate;

        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = true,
            Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
        };

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Status log", TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        right.Controls.Add(_log, 0, 1);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
        };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(right);

        Controls.Add(split);
        Controls.Add(top);

        // Splitter sizing needs the laid-out width, unavailable in the constructor.
        Shown += (_, _) =>
        {
            split.SplitterDistance = Math.Min(420, Math.Max(1, split.Width - 240));
            split.Panel1MinSize = 200;
            split.Panel2MinSize = 240;
        };

        Log("Windows Fingerprint Simulator ready.");
        RefreshList();
        ZkShimPipe.SelfCheck();
        EnsurePipeServer();
    }

    private bool IsShimMode => (string?)_modeBox.SelectedItem == ShimMode;

    private void EnsurePipeServer()
    {
        _zkPipe ??= new ZkShimPipe(idHex => Log($"ZKTeco shim acquired template (id {idHex})."));
    }

    private void RefreshList()
    {
        List<Bitmap> previous = new(_thumbnailBitmaps);
        _thumbnailBitmaps.Clear();
        _thumbnails.Images.Clear();
        _list.Items.Clear();

        for (int i = 0; i < _records.Count; i++)
        {
            FingerprintRecord record = _records[i];
            byte[] template;
            try
            {
                template = Convert.FromBase64String(record.TemplateBase64);
            }
            catch (FormatException)
            {
                template = Array.Empty<byte>();
            }

            Bitmap thumbnail = CreateThumbnail(template);
            _thumbnailBitmaps.Add(thumbnail);
            _thumbnails.Images.Add(thumbnail);
            _list.Items.Add(new ListViewItem(record.Name, i) { Tag = record });
        }

        // ImageList clones lazily on handle creation; bitmaps must outlive Images.Clear().
        foreach (Bitmap bitmap in previous)
        {
            bitmap.Dispose();
        }

        Log($"Loaded {_records.Count} fingerprint(s).");
    }

    private static Bitmap CreateThumbnail(byte[] template)
    {
        const int size = 48;
        const int cells = 5;
        var bitmap = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(28, 30, 34));
            if (template.Length < 35)
            {
                return bitmap;
            }

            Color ink = Color.FromArgb(
                120 + template[4] % 136,
                120 + template[5] % 136,
                120 + template[6] % 136);
            int unit = size / cells;
            using var brush = new SolidBrush(ink);
            for (int row = 0; row < cells; row++)
            {
                for (int col = 0; col < cells; col++)
                {
                    int mirror = col < 3 ? col : cells - 1 - col;
                    if ((template[20 + row * 3 + mirror] & 1) == 0)
                    {
                        continue;
                    }
                    g.FillRectangle(brush, col * unit, row * unit, unit, unit);
                }
            }
        }
        return bitmap;
    }

    private async void AddButton_Click(object? sender, EventArgs e)
    {
        string name = _nameBox.Text.Trim();
        if (name.Length == 0)
        {
            Log("Enter a name before adding a fingerprint.");
            return;
        }
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            byte[] template = WinBioEnroller.BuildTemplate(name);
            string idHex = Convert.ToHexString(template, WinBioEnroller.TemplateIdOffset, WinBioEnroller.TemplateIdSize);
            Log($"Enrolling '{name}' (id {idHex})...");
            using var driver = new DriverClient();
            try
            {
                string identity = await WinBioEnroller.EnrollAsync(template, driver, Handle, Log);
                Log($"Enrolled '{name}'. Identity: {identity}");
            }
            catch (Exception ex)
            {
                // WBS enrollment is best-effort; the template still drives read simulation.
                Log($"WBS enrollment unavailable ({ex.Message}); saved for read simulation only.");
            }

            var record = new FingerprintRecord
            {
                Name = name,
                IdHex = idHex,
                TemplateBase64 = Convert.ToBase64String(template),
            };
            FingerprintStore.Save(_records.Append(record).ToList());
            _records.Add(record);
            RefreshList();
            _nameBox.Clear();
        }
        catch (Exception ex)
        {
            Log($"Add failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void List_Click(object? sender, EventArgs e) => ProbeSelection();

    private void List_ItemActivate(object? sender, EventArgs e) => ProbeSelection();

    private void ProbeSelection()
    {
        if (_busy)
        {
            return;
        }

        ListViewItem? item = _list.FocusedItem ?? (_list.SelectedItems.Count > 0 ? _list.SelectedItems[0] : null);
        if (item?.Tag is not FingerprintRecord record)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (ReferenceEquals(item, _lastProbed) && now - _lastProbeTick < 400)
        {
            return;
        }
        _lastProbed = item;
        _lastProbeTick = now;

        if (IsShimMode)
        {
            StageShim(record);
            return;
        }

        SetBusy(true);
        byte[] template;
        try
        {
            template = Convert.FromBase64String(record.TemplateBase64);
        }
        catch (FormatException)
        {
            Log($"Probe failed: stored template for '{record.Name}' is invalid.");
            SetBusy(false);
            return;
        }
        _ = ProbeAsync(record, template);
    }

    private async Task ProbeAsync(FingerprintRecord record, byte[] template)
    {
        try
        {
            using var driver = new DriverClient();
            await Task.Run(() => driver.SendRead(template));
            Log($"Probe '{record.Name}': template staged and read triggered (id {record.IdHex}).");
        }
        catch (Exception ex)
        {
            Log($"Probe failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void StageShim(FingerprintRecord record)
    {
        try
        {
            byte[] stored = Convert.FromBase64String(record.TemplateBase64);
            byte[] frame = new byte[8 + stored.Length];
            Encoding.ASCII.GetBytes("MZKF").CopyTo(frame, 0);
            BitConverter.GetBytes((uint)stored.Length).CopyTo(frame, 4);
            stored.CopyTo(frame, 8);

            string idHex = record.IdHex.Length > 16 ? record.IdHex[..16] : record.IdHex;
            EnsurePipeServer();
            _zkPipe!.SetPending(idHex, Convert.ToBase64String(frame));
            Log($"Ready for ZKTeco shim acquisition (id {idHex}).");
        }
        catch (Exception ex)
        {
            Log($"ZKTeco shim staging failed: {ex.Message}");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        ZkShimPipe? pipe = _zkPipe;
        _zkPipe = null;
        pipe?.Dispose();
        base.OnFormClosing(e);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _addButton.Enabled = !busy;
        _nameBox.Enabled = !busy;
        _list.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void Log(string message)
    {
        void write() => _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        if (IsDisposed)
        {
            return;
        }
        if (InvokeRequired)
        {
            try { BeginInvoke(write); }
            catch (InvalidOperationException) { /* handle destroyed while closing */ }
        }
        else
        {
            // TextBox buffers text before its handle exists, so constructor logs survive.
            write();
        }
    }
}

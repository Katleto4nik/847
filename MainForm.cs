using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace ZipToJar;

internal sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(24, 24, 30);
    private static readonly Color Card = Color.FromArgb(34, 34, 44);
    private static readonly Color CardHover = Color.FromArgb(44, 40, 70);
    private static readonly Color Accent = Color.FromArgb(124, 92, 255);
    private static readonly Color Muted = Color.FromArgb(150, 150, 170);

    private readonly Panel _zone = new();
    private readonly Label _zoneText = new();
    private readonly Label _status = new();
    private readonly ProgressBar _bar = new();
    private readonly TextBox _log = new();
    private readonly Button _openBtn = new();
    private readonly Button _copyBtn = new();

    private bool _busy;
    private bool _hover;
    private string? _lastJar;

    public MainForm(string[] args)
    {
        Text = "Zip → JAR";
        ClientSize = new Size(640, 540);
        MinimumSize = new Size(520, 460);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        BuildUi();

        Shown += (_, _) =>
        {
            // якщо перетягнули zip прямо на .exe — одразу запускаємо
            if (args.Length > 0) StartConversion(args);
        };
        FormClosing += (_, _) => Converter.KillCurrent();
    }

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
            BackColor = Bg
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

        // зона для перетягування
        _zone.Dock = DockStyle.Fill;
        _zone.BackColor = Bg;
        _zone.Padding = new Padding(8);
        _zone.Margin = new Padding(0, 0, 0, 8);
        _zone.Cursor = Cursors.Hand;
        EnableDoubleBuffer(_zone);
        _zone.Paint += PaintZone;
        _zone.Resize += (_, _) => _zone.Invalidate();
        _zone.Click += (_, _) => Browse();

        _zoneText.Dock = DockStyle.Fill;
        _zoneText.TextAlign = ContentAlignment.MiddleCenter;
        _zoneText.BackColor = Color.Transparent;
        _zoneText.ForeColor = Color.White;
        _zoneText.Font = new Font("Segoe UI", 13f);
        _zoneText.Cursor = Cursors.Hand;
        _zoneText.Text = "Перетягни ZIP з модом сюди\r\nабо натисни, щоб вибрати файл";
        _zoneText.Click += (_, _) => Browse();
        _zone.Controls.Add(_zoneText);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Muted;
        _status.Text = "Очікую ZIP-файл...";

        _bar.Dock = DockStyle.Fill;
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Margin = new Padding(0);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.FromArgb(14, 14, 18);
        _log.ForeColor = Color.FromArgb(200, 205, 215);
        _log.Font = new Font("Consolas", 9f);
        _log.Margin = new Padding(0, 8, 0, 8);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Padding = new Padding(0, 6, 0, 0),
            WrapContents = false
        };
        StyleButton(_openBtn, "Відкрити папку з .jar", OpenLast);
        _openBtn.Enabled = false;
        StyleButton(_copyBtn, "Копіювати лог", CopyLog);
        buttons.Controls.Add(_openBtn);
        buttons.Controls.Add(_copyBtn);

        layout.Controls.Add(_zone, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        layout.Controls.Add(_bar, 0, 2);
        layout.Controls.Add(_log, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);

        // drag & drop працює по всьому вікну
        foreach (var c in new Control[] { this, layout, _zone, _zoneText, _status, _log })
            Hook(c);
    }

    private static void StyleButton(Button b, string text, Action onClick)
    {
        b.Text = text;
        b.Width = 220;
        b.Height = 34;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Accent;
        b.BackColor = Card;
        b.ForeColor = Color.White;
        b.Margin = new Padding(0, 0, 10, 0);
        b.Cursor = Cursors.Hand;
        b.Click += (_, _) => onClick();
    }

    private static void EnableDoubleBuffer(Control c)
    {
        typeof(Control)
            .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?
            .SetValue(c, true);
    }

    private void PaintZone(object? sender, PaintEventArgs e)
    {
        if (_zone.Width < 60 || _zone.Height < 60) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(3, 3, _zone.Width - 7, _zone.Height - 7);
        using var path = RoundedRect(rect, 16);
        using var fill = new SolidBrush(_hover ? CardHover : Card);
        e.Graphics.FillPath(fill, path);
        using var pen = new Pen(_hover ? Accent : Color.FromArgb(90, 90, 115), 2f)
        {
            DashStyle = DashStyle.Dash
        };
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ------------------------------------------------------- drag & drop

    private void Hook(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += OnDragOver;
        c.DragOver += OnDragOver;
        c.DragLeave += (_, _) => SetHover(false);
        c.DragDrop += OnDragDrop;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!_busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            SetHover(true);
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        SetHover(false);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            StartConversion(files);
    }

    private void SetHover(bool hover)
    {
        if (_hover == hover) return;
        _hover = hover;
        _zone.Invalidate();
    }

    private void Browse()
    {
        if (_busy) return;
        using var dlg = new OpenFileDialog
        {
            Title = "Вибери ZIP з модом",
            Filter = "ZIP архів (*.zip)|*.zip",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            StartConversion(dlg.FileNames);
    }

    // --------------------------------------------------------- робота

    private async void StartConversion(string[] files)
    {
        if (_busy) return;

        var zips = files
            .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
            .ToArray();

        if (zips.Length == 0)
        {
            AppendLog("Потрібен файл у форматі .zip");
            return;
        }

        SetBusy(true);
        var ok = 0;
        string? produced = null;

        foreach (var zip in zips)
        {
            try
            {
                var jars = await Task.Run(() => Converter.Run(zip, AppendLog));
                foreach (var jar in jars)
                {
                    AppendLog("ГОТОВО: " + jar);
                    produced = jar;
                }
                ok++;
            }
            catch (Exception ex)
            {
                AppendLog("ПОМИЛКА: " + ex.Message);
            }
        }

        SetBusy(false);

        if (produced != null)
        {
            _lastJar = produced;
            _openBtn.Enabled = true;
            OpenInExplorer(produced);
        }

        _status.Text = ok == zips.Length
            ? "Готово. .jar лежить поруч із ZIP."
            : $"Готово: {ok} з {zips.Length}. Дивись лог.";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _bar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        _bar.MarqueeAnimationSpeed = busy ? 30 : 0;
        _zone.Cursor = busy ? Cursors.WaitCursor : Cursors.Hand;
        _zoneText.Cursor = _zone.Cursor;
        if (busy) _status.Text = "Працюю...";
    }

    private void AppendLog(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(AppendLog), line); } catch { }
            return;
        }

        _log.AppendText(line + Environment.NewLine);

        if (line.StartsWith("> Task ", StringComparison.Ordinal))
            _status.Text = "Gradle: " + line.Substring(7);
        else if (line.StartsWith("» ", StringComparison.Ordinal))
            _status.Text = line.Substring(2);
    }

    private void OpenLast()
    {
        if (_lastJar != null) OpenInExplorer(_lastJar);
    }

    private void CopyLog()
    {
        if (_log.TextLength == 0) return;
        try { Clipboard.SetText(_log.Text); } catch { }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // не критично
        }
    }
}

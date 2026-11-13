using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using Apfs.Windows.Ipc;
using Apfs.Windows.Localization;

namespace Apfs.Tray.Ui;

/// <summary>
/// Disk listesi; ana pencere ve tepsi paneli aynı denetimi kullanır (<see cref="Compact"/> panel için). Kart yok: her disk bir
/// defter kaydı gibi başlık + ince çizgi, birimler aralarında noktalı çizgiyle satır satır (橋戸 marka).
/// Durum yalnızca değişince yeniden kurulur (<see cref="AppController.Changed"/>).
/// </summary>
internal sealed class DiskListView : Panel
{
    private readonly AppController _app;
    private readonly TableLayoutPanel _stack;

    public DiskListView(AppController app, bool compact)
    {
        _app = app;
        Compact = compact;
        AutoScroll = true;
        BackColor = Theme.Paper;
        DoubleBuffered = true;
        Theme.DarkScrollbars(this);
        _stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            BackColor = Theme.Paper, Margin = Padding.Empty, Padding = Padding.Empty,
        };
        _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_stack);
        _app.SpaceChanged += UpdateSpace;
        Rebuild();
    }

    /// <summary>Sürücü harfine göre satırların doluluk göstergeleri (her yeniden kurmada sıfırlanır).</summary>
    private readonly Dictionary<string, SpaceView> _spaceViews = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alan değişti: liste yeniden kurulmaz, yalnız çubuk ve yazı güncellenir.</summary>
    private void UpdateSpace()
    {
        if (IsDisposed) return;
        foreach (var (drive, view) in _spaceViews) view.Show(_app.Space(drive));
    }

    public bool Compact { get; }

    /// <summary>Listeyi içeren pencerenin sahibi (onay ve ayrıntı pencereleri için); panelde null (panel odak kaybında kapanır).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IWin32Window? DialogOwner { get; set; }

    /// <summary>
    /// İçeriğin kaydırmasız yüksekliği (panel boyunu buna göre ayarlar). Satırlar dolgunun içinde dizilir: ölçüm o genişlikte
    /// yapılmazsa uzun metinlerin (ör. Almanca) satır kaydırması sayılmaz ve panel kısa kalır.
    /// </summary>
    public int ContentHeight => _stack.GetPreferredSize(new Size(Math.Max(0, ClientSize.Width - Padding.Horizontal), 0)).Height;

    public void Rebuild()
    {
        SuspendLayout();
        _stack.SuspendLayout();
        var old = _stack.Controls.Cast<Control>().ToList();
        _spaceViews.Clear();
        _stack.Controls.Clear();
        _stack.RowStyles.Clear();
        foreach (var c in old) c.Dispose();

        var s = _app.Status;
        if (_app.ServiceDown)
            AddRow(Message(false, WinText.U("List.ServiceDownHeading"),
                WinText.U("List.ServiceDownBody")));
        else if (s is null)
            AddRow(Message(true, WinText.U("List.Connecting"), ""));
        else if (s.Disks.Count == 0)
            AddRow(Message(true, WinText.U("List.NoDiskHeading"),
                WinText.U("List.NoDiskBody")));
        else
            foreach (var d in s.Disks) AddRow(DiskBlock(d));

        _stack.ResumeLayout(true);
        ResumeLayout(true);
    }

    private void AddRow(Control c)
    {
        _stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        c.Dock = DockStyle.Fill;
        _stack.Controls.Add(c);
    }

    // ---------------- Disk kaydı ----------------

    private Control DiskBlock(DiskStatus d)
    {
        var block = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Paper,
            Margin = new Padding(0, 0, 0, this.S(Compact ? 10 : 22)), Padding = Padding.Empty,
        };
        block.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control c, bool fill = true)
        {
            block.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (fill) c.Dock = DockStyle.Fill;
            block.Controls.Add(c);
        }

        // Başlık: disk adı, sağda "Güvenle çıkar"; altında ince çizgi
        var header = new RuleRow(RuleRow.Line.SolidBottom)
        {
            ColumnCount = 2, Padding = new Padding(0, 0, 0, this.S(Compact ? 2 : 4)),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = Label(d.Description, Compact ? Theme.Small : Theme.Heading(12f), Compact ? Theme.InkSoft : Theme.Ink);
        title.Anchor = AnchorStyles.Left;
        header.Controls.Add(title, 0, 0);
        var eject = Button(WinText.U("Action.SafelyEject"), Compact ? FriendlyButton.Kind.Icon : FriendlyButton.Kind.Quiet,
            async () => await _app.EjectAsync(d, DialogOwner), Glyphs.Icon.Eject);
        eject.BusyText = WinText.U("Action.Ejecting");
        eject.Busy = _app.IsEjecting(d.Id);
        eject.Anchor = AnchorStyles.Right;
        eject.Margin = Padding.Empty;
        header.Controls.Add(eject, 1, 0);
        Add(header);

        var visible = d.Volumes.Where(v => v.State != VolumeStatus.Hidden).ToList();
        if (visible.Count == 0)
        {
            // Yazma durumu değişirken bölümün birimleri kısa süre listede olmaz (denetlenip yeniden bağlanır)
            var none = Label(_app.IsSwitching(d.Id) ? WinText.U("Card.Checking") : WinText.U("Card.NoVolumes"),
                Theme.Small, Theme.InkSoft);
            none.Margin = new Padding(0, this.S(10), 0, this.S(4));
            none.MaximumSize = new Size(this.S(Compact ? 300 : 460), 0);
            Add(none);
        }
        // Yazma bölüm başınadır: tek bölümlü diskte eskisi gibi (düğme altta); çok bölümlüde her bölümün düğmesi ve ret nedeni
        // o bölümün biriminin hemen altında (yan yana sığmıyor)
        var containers = visible.GroupBy(v => v.Container).OrderBy(g => g.Key).ToList();
        bool multi = containers.Count > 1;
        bool first = true;
        foreach (var g in containers)
        {
            foreach (var v in g)
            {
                var (state, status, statusColor, button) = v.State switch
                {
                    VolumeStatus.Mounted => (v.Writable ? Glyphs.State.Writable : Glyphs.State.Open,
                        Capitalize(v.Writable ? WinText.U("Card.Writable") : WinText.U("Card.ReadOnly")),
                        v.Writable ? Theme.Shu : Theme.InkSoft,
                        Button(WinText.U("Action.Open"), FriendlyButton.Kind.Secondary, () => AppController.OpenDrive(v.MountPoint!))),
                    VolumeStatus.NeedsPassword => (Glyphs.State.Locked, WinText.U("Card.NeedsPassword"), Theme.InkSoft,
                        Button(WinText.U("Action.Unlock"), FriendlyButton.Kind.Secondary, async () => await _app.UnlockAsync(d, v))),
                    _ => (Glyphs.State.Problem, WinText.U("Card.Failed"), Theme.Shu,
                        Button(WinText.U("Action.Details"), FriendlyButton.Kind.Secondary, () => AppController.ShowDetails(DialogOwner, v))),
                };
                if (v.State == VolumeStatus.Mounted) button.Enabled = !_app.IsEjecting(d.Id) && !_app.IsSwitching(d.Id);
                string? drive = v.State == VolumeStatus.Mounted ? v.MountPoint : null;
                Add(VolumeRow(state, v.Name, drive, status, statusColor, button, dotted: !first));
                first = false;
            }
            if (!multi) continue;
            if (WriteBlockLabel(d, g, multi) is { } why)
            {
                why.Margin = new Padding(GlyphIndent, 0, 0, this.S(6));
                Add(why);
            }
            if (WriteToggle(d, g, multi) is { } toggle)
            {
                toggle.Anchor = AnchorStyles.Right;
                toggle.Margin = new Padding(this.S(4), 0, 0, this.S(4));
                Add(toggle, fill: false);
            }
        }
        if (!multi && containers.Count == 1 && WriteBlockLabel(d, containers[0], multi) is { } reasonLabel)
        {
            reasonLabel.Margin = new Padding(GlyphIndent, 0, 0, this.S(4));
            Add(reasonLabel);
        }

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Right,
            BackColor = Theme.Paper, Margin = Padding.Empty,
        };
        // Denetlenen bölümün birimleri o sırada listede olmaz: düğmesi meşgul olarak dursun
        foreach (int c in _app.SwitchingContainers(d.Id).Where(c => containers.All(g => g.Key != c)))
        {
            var waiting = Button(WinText.U("Action.Checking"), FriendlyButton.Kind.Quiet, () => { });
            waiting.BusyText = WinText.U("Action.Checking");
            waiting.Busy = true;
            waiting.Margin = new Padding(this.S(4), 0, 0, 0);
            actions.Controls.Add(waiting);
            break; // tek satır: aynı anda birden çoğu denetleniyorsa da bir gösterge yeter
        }
        if (!multi && containers.Count == 1 && WriteToggle(d, containers[0], multi) is { } single)
        {
            single.Margin = new Padding(this.S(4), 0, 0, 0);
            actions.Controls.Add(single);
        }
        if (actions.Controls.Count > 0) Add(actions, fill: false);
        else actions.Dispose();
        return block;
    }

    /// <summary>Durum işaretinin genişliği + boşluk: ret nedeni birim adının hizasından başlasın.</summary>
    private int GlyphIndent => this.S(Compact ? 20 + 12 : 24 + 14);

    /// <summary>Birim satırı: solda durum işareti, ortada ad + sürücü harfi ve durum, sağda tek düğme.</summary>
    private Control VolumeRow(Glyphs.State state, string name, string? drive, string status, Color statusColor, FriendlyButton button, bool dotted)
    {
        var row = new RuleRow(dotted ? RuleRow.Line.DottedTop : RuleRow.Line.None)
        {
            ColumnCount = 3, Padding = new Padding(0, this.S(Compact ? 6 : 10), 0, this.S(Compact ? 6 : 10)),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var glyph = new StatusGlyph(state, this.S(Compact ? 20 : 24))
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, this.S(4), this.S(Compact ? 12 : 14), 0),
        };
        var info = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill,
            Margin = new Padding(0, this.S(2), this.S(8), 0), BackColor = Theme.Paper,
        };
        var nameLine = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = Padding.Empty, BackColor = Theme.Paper,
        };
        nameLine.Controls.Add(Label(name, Compact ? Theme.Body : Theme.Font(11f), Theme.Ink));
        if (drive is { Length: > 0 })
        {
            var letter = Label(drive, Compact ? Theme.Body : Theme.Font(11f), Theme.InkSoft);
            letter.Margin = new Padding(this.S(2), 0, 0, 0);
            nameLine.Controls.Add(letter);
        }
        info.Controls.Add(nameLine);
        // Bağlı birimde ad satırıyla durum arasında doluluk çubuğu, durumdan sonra "· 465 GB içinde 153 GB boş" (panelde
        // yalnız boş); alan bilinmiyorsa (henüz sorulmadı, hata, zaman aşımı) ikisi de gizli. Değerler yerinde güncellenir.
        var space = drive is { Length: > 0 } ? new SpaceView(this) : null;
        if (space is not null) info.Controls.Add(space.Bar);
        var statusLine = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, Margin = new Padding(0, this.S(3), 0, 0),
            BackColor = Theme.Paper,
        };
        var statusLabel = Label(status, Theme.Small, statusColor);
        statusLine.Controls.Add(statusLabel);
        if (space is not null)
        {
            statusLine.Controls.Add(space.Group);
            space.Attach(statusLine, statusLabel);
            _spaceViews[drive!] = space;
            space.Show(_app.Space(drive));
        }
        info.Controls.Add(statusLine);
        // Dar pencerede alan yazısı alt satıra geçsin, çubuk satırdan taşmasın
        info.SizeChanged += (_, _) =>
        {
            int width = Math.Max(this.S(40), info.ClientSize.Width);
            statusLine.MaximumSize = new Size(width, 0);
            space?.Fit(width, this.S(Compact ? 200 : 240));
        };
        button.Anchor = AnchorStyles.Right;
        button.Margin = Padding.Empty;
        row.Controls.Add(glyph, 0, 0);
        row.Controls.Add(info, 1, 0);
        row.Controls.Add(button, 2, 0);
        return row;
    }

    /// <summary>"salt okunur" → "Salt okunur" (satırın başında; arayüz dilinin büyük harf kuralıyla).</summary>
    private static string Capitalize(string s) =>
        s.Length == 0 ? s : CultureInfo.CurrentUICulture.TextInfo.ToUpper(s[0]) + s[1..];

    /// <summary>Bölümün "Yazmaya izin ver" / "Salt okunura dön" düğmesi; bağlı (açılabilmiş) birimi yoksa null.</summary>
    private FriendlyButton? WriteToggle(DiskStatus d, IGrouping<int, VolumeStatus> g, bool multi)
    {
        if (!g.Any(v => v.State == VolumeStatus.Mounted)) return null;
        bool writable = g.Any(v => v.Writable);
        string name = g.First().Name;
        var toggle = Button(writable ? WinText.U("Action.BackToReadOnly") : WinText.U("Action.AllowWriting"), FriendlyButton.Kind.Quiet,
            async () => await _app.SetWritableAsync(d, g.Key, multi ? $"{d.Description} · \"{name}\"" : d.Description, !writable, DialogOwner));
        toggle.BusyText = writable ? WinText.U("Action.Saving") : WinText.U("Action.Checking");
        toggle.Busy = _app.IsSwitching(d.Id, g.Key);
        toggle.Enabled = !_app.IsEjecting(d.Id);
        return toggle;
    }

    /// <summary>Yazma kapısı bu bölümü reddettiyse nedeni (sade Türkçe); yoksa null.</summary>
    private Label? WriteBlockLabel(DiskStatus d, IGrouping<int, VolumeStatus> g, bool multi)
    {
        if (g.Any(v => v.Writable)) return null;
        // Eski hizmet bölüm başına neden göndermez: tek bölümlüde disk düzeyindeki neden
        string? reason = g.Select(v => v.WriteBlockReason).FirstOrDefault(r => r is { Length: > 0 }) ?? (multi ? null : d.WriteBlockReason);
        if (reason is not { Length: > 0 }) return null;
        var why = Label(WinText.U("Card.WriteRefused", reason), Theme.Small, Theme.InkSoft);
        why.MaximumSize = new Size(this.S(Compact ? 280 : 440), 0);
        why.Margin = new Padding(0, this.S(4), 0, this.S(2));
        return why;
    }

    /// <summary>Boş, bağlanıyor ve hizmet kapalı ekranı: büyük, soluk işaret (hizmet kapalıysa kesik çizgi), başlık, açıklama.</summary>
    private Control Message(bool friendly, string heading, string body)
    {
        var p = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Paper,
            Padding = new Padding(this.S(8), this.S(Compact ? 14 : 36), this.S(8), this.S(16)),
        };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var mark = new MarkView(seal: false, this.S(Compact ? 64 : 120), dashed: !friendly)
        {
            ForeColor = Theme.Hair, Anchor = AnchorStyles.Top, Margin = new Padding(0, 0, 0, this.S(Compact ? 10 : 18)),
        };
        p.Controls.Add(mark);
        var h = Label(heading, Theme.Heading(Compact ? 12f : 15.75f), Theme.Ink);
        h.Anchor = AnchorStyles.Top;
        h.TextAlign = ContentAlignment.TopCenter;
        p.Controls.Add(h);
        if (body.Length > 0)
        {
            var b = Label(body, Theme.Body, Theme.InkSoft);
            b.Anchor = AnchorStyles.Top;
            b.TextAlign = ContentAlignment.TopCenter;
            b.Margin = new Padding(0, this.S(6), 0, 0);
            p.Controls.Add(b);
        }
        // Metinler liste genişliğinde (ana pencerede okunaklı bir satır boyuyla) kaydırılsın
        p.SizeChanged += (_, _) =>
        {
            int w = Math.Max(this.S(120), Math.Min(p.Width - p.Padding.Horizontal, this.S(Compact ? 320 : 380)));
            foreach (Control c in p.Controls)
                if (c is Label l) l.MaximumSize = new Size(w, 0);
        };
        return p;
    }

    private static Label Label(string text, Font font, Color color) => new()
    {
        Text = text, Font = font, ForeColor = color, AutoSize = true, Margin = Padding.Empty, UseMnemonic = false,
        BackColor = Color.Transparent,
    };

    private FriendlyButton Button(string text, FriendlyButton.Kind kind, Action onClick, Glyphs.Icon icon = Glyphs.Icon.None)
    {
        var b = new FriendlyButton(text, kind, icon);
        // İş tıklama olayı bittikten sonra başlasın: durum değişince liste yeniden kurulur ve bu düğme elden çıkarılır.
        b.Click += (_, _) => BeginInvoke(onClick);
        return b;
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        _stack.MaximumSize = new Size(ClientSize.Width, 0); // içerik yatay kaydırmasın
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _app.SpaceChanged -= UpdateSpace;
        base.Dispose(disposing);
    }

    /// <summary>
    /// Bir birimin doluluk çubuğu ve alan yazısı ("·" ayıracıyla durumun yanında). Yazı durumun yanına sığmazsa satır
    /// durumdan sonra kırılır ve ayıraç gizlenir (alt satır "·" ile başlamasın).
    /// </summary>
    private sealed class SpaceView
    {
        private readonly bool _compact;
        private readonly Label _dot;
        private readonly Label _text;
        private FlowLayoutPanel? _line;
        private Label? _status;
        private int _maxWidth = int.MaxValue;

        public SpaceView(DiskListView owner)
        {
            _compact = owner.Compact;
            // Sol boşluk: etiket metni kenardan birkaç piksel içeride başlar; çubuk metinle hizalı dursun
            Bar = new SpaceBar
            {
                Height = owner.S(2), Width = owner.S(40), Visible = false,
                Margin = new Padding(owner.S(2), owner.S(_compact ? 6 : 8), 0, owner.S(3)),
            };
            Group = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = Padding.Empty,
                BackColor = Theme.Paper, Visible = false,
            };
            _dot = Label("·", Theme.Small, Theme.InkSoft);
            _dot.Margin = new Padding(owner.S(3), 0, owner.S(3), 0);
            _text = Label("", Theme.Small, Theme.InkSoft);
            Group.Controls.Add(_dot);
            Group.Controls.Add(_text);
        }

        public SpaceBar Bar { get; }
        public FlowLayoutPanel Group { get; }

        /// <summary>Durum satırı ve durum etiketi (kırılma kararı için).</summary>
        public void Attach(FlowLayoutPanel line, Label status)
        {
            _line = line;
            _status = status;
        }

        /// <summary>Bilgi sütununun genişliği değişti: çubuk en çok <paramref name="barMax"/>, satır kırılması yeniden.</summary>
        public void Fit(int width, int barMax)
        {
            _maxWidth = width;
            Bar.Width = Math.Max(1, Math.Min(width - Bar.Margin.Horizontal, barMax));
            Arrange();
        }

        public void Show(DriveSpace? space)
        {
            Bar.Visible = Group.Visible = space is not null;
            if (space is { } s)
            {
                Bar.Set(s.Used, s.Low);
                string free = SizeText.Format(s.Free);
                _text.Text = _compact ? WinText.U("Card.Free", free) : WinText.U("Card.Space", SizeText.Format(s.Total), free);
                _text.ForeColor = s.Low ? Theme.Shu : Theme.InkSoft;
            }
            Arrange();
        }

        /// <summary>Karar ayıraç dahil genişlikle verilir: ayıracı gizlemek satırı yeniden sığdırıp gidip gelmeye yol açmasın.</summary>
        private void Arrange()
        {
            if (_line is null || _status is null) return;
            int need = Width(_status) + Width(_dot) + Width(_text);
            bool wrap = Group.Visible && need > _maxWidth;
            _dot.Visible = !wrap;
            _line.SetFlowBreak(_status, wrap);
        }

        private static int Width(Control c) => c.PreferredSize.Width + c.Margin.Horizontal;
    }

    /// <summary>2 px doluluk çubuğu: yol Track, dolu kısım Bar (boş alan %10'dan azsa zinober).</summary>
    private sealed class SpaceBar : Control
    {
        private double _used;
        private bool _low;

        public SpaceBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            AccessibleRole = AccessibleRole.ProgressBar;
        }

        public void Set(double used, bool low)
        {
            used = Math.Clamp(used, 0, 1);
            if (used == _used && low == _low) return;
            _used = used;
            _low = low;
            AccessibleDescription = used.ToString("P0", CultureInfo.CurrentCulture);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Track);
            int filled = (int)Math.Round(Width * _used);
            if (_used > 0 && filled == 0) filled = 1; // birazcık dolu disk boş görünmesin
            using var brush = new SolidBrush(_low ? Theme.Shu : Theme.Bar);
            e.Graphics.FillRectangle(brush, 0, 0, filled, Height);
        }
    }

    /// <summary>Altında düz ya da üstünde noktalı ince çizgi olan satır (defter düzeni).</summary>
    private sealed class RuleRow : TableLayoutPanel
    {
        public enum Line { None, SolidBottom, DottedTop }

        private readonly Line _line;

        public RuleRow(Line line)
        {
            _line = line;
            DoubleBuffered = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Theme.Paper;
            Margin = Padding.Empty;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_line == Line.None) return;
            using var pen = new Pen(Theme.Hair, 1);
            if (_line == Line.DottedTop)
            {
                pen.DashStyle = DashStyle.Custom;
                pen.DashPattern = [1f, 2f];
                e.Graphics.DrawLine(pen, 0, 0, Width, 0);
            }
            else e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }
}

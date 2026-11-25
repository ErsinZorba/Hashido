using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Apfs.Windows.Ipc;
using Apfs.Windows.Tray;

namespace Apfs.Tray.Ui;

/// <summary>
/// Disk listesi; ana pencere (kartlar) ve tepsi paneli (<see cref="Compact"/>: sıkışık satırlar) aynı modeli çizer. Ne çizileceği
/// (işaret, metin, düğme, bölüm grupları, yazma düğmesi, ret nedeni) <see cref="DiskViewModel"/>'den (winuiPlan P1); burası yalnız
/// WPF çizimidir (görünüş wpfPlan prototipi). Liste yalnız durum imzası değişince yeniden kurulur (<see cref="AppController.Changed"/>;
/// Ek A B4); boş alan yerinde güncellenir (B13). Renkler paylaşılan rol fırçalarından: açık/koyu değişince yeniden kurulmaz.
/// </summary>
internal sealed class DiskList
{
    private readonly AppController _app;
    private readonly StackPanel _stack = new();
    private readonly Dictionary<string, SpaceView> _spaces = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="owner">Kutuların sahibi (ana pencere); panelde null (panel odak kaybında gizlenir: kutular sahipsiz; Ek A D8).</param>
    public DiskList(AppController app, bool compact, Func<Window?> owner)
    {
        _app = app;
        Compact = compact;
        Owner = owner;
        _app.SpaceChanged += UpdateSpace;
    }

    public bool Compact { get; }
    public Func<Window?> Owner { get; }
    public FrameworkElement View => _stack;

    public void Detach() => _app.SpaceChanged -= UpdateSpace;

    private void UpdateSpace()
    {
        foreach (var (drive, view) in _spaces) view.Show(_app.Space(drive));
    }

    public void Rebuild()
    {
        _spaces.Clear();
        _stack.Children.Clear();
        var model = DiskViewModel.From(_app);
        if (model.Screen != ListScreen.Disks) _stack.Children.Add(Message(model));
        else foreach (var d in model.Disks) _stack.Children.Add(DiskBlock(d));
    }

    // ---------------- Boş ekranlar (B6) ----------------

    private FrameworkElement Message(ListModel m)
    {
        var p = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = Compact ? 320 : 380, Margin = new Thickness(8, Compact ? 14 : 48, 8, 16),
        };
        double mark = Compact ? 64 : 112;
        p.Children.Add(new GateGlyph(m.Dashed ? GlyphKind.MarkDashed : GlyphKind.Mark, mark)
        {
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, Compact ? 12 : 20),
        });
        var h = UiKit.Heading(m.Heading, Compact ? 16 : 20);
        h.TextAlignment = TextAlignment.Center;
        h.HorizontalAlignment = HorizontalAlignment.Center;
        p.Children.Add(h);
        if (m.Body.Length > 0)
        {
            var b = UiKit.Text(m.Body, role: Role.InkSoft);
            b.TextAlignment = TextAlignment.Center;
            b.Margin = new Thickness(0, Compact ? 8 : 12, 0, 0);
            p.Children.Add(b);
        }
        return p;
    }

    // ---------------- Disk kaydı ----------------

    private FrameworkElement DiskBlock(DiskModel m)
    {
        var d = m.Disk;
        var block = new StackPanel { Margin = Compact ? new Thickness(0, 0, 0, 6) : new Thickness(0, 6, 0, 10) };

        // Başlık: disk adı ("Disk 3 (298 GB)"), sağda "Güvenle çıkar" (geri alınmaz eylem: Alert tonu; panelde yalnız ikon)
        var header = new DockPanel { Margin = Compact ? new Thickness(0) : new Thickness(4, 0, 0, 8), MinHeight = Compact ? 32 : 36 };
        var eject = Look.Tone(new ActionButton(m.EjectText, ButtonKind.Quiet, EjectGlyph, iconOnly: Compact) { BusyText = m.EjectBusyText, Context = m.Title }, Role.Alert);
        eject.Busy = m.Ejecting;
        eject.Invoked += async () => await _app.EjectAsync(d, Owner());
        eject.VerticalAlignment = VerticalAlignment.Center;
        // Panelde taşırma yok: kaydırma alanı taşanı keser, üstüne gelince çıkan zeminin sağ köşeleri düz kalıyordu (2026-11-22)
        eject.Margin = new Thickness(0, 0, Compact ? 0 : -4, 0);
        DockPanel.SetDock(eject, Dock.Right);
        header.Children.Add(eject);
        var title = Compact ? UiKit.Text(m.Title, 12, Role.InkSoft, wrap: false) : Look.Heading(UiKit.Text(m.Title, 17, wrap: false));
        title.VerticalAlignment = VerticalAlignment.Center;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(title);
        block.Children.Add(header);

        if (m.Placeholder is { } placeholder)
        {
            // Yazma durumu değişirken bölümün birimleri kısa süre listede olmaz (denetlenip yeniden bağlanır) / birim yok
            var none = UiKit.Text(placeholder, Compact ? 12 : 14, Role.InkSoft);
            block.Children.Add(Compact ? UiKit.Margin(none, 0, 4, 0, 4) : Card(none));
        }
        // Yazma bölüm başınadır: tek bölümlü diskte neden birimlerin altında, düğme altta sağda; çok bölümlüde her bölümün nedeni ve
        // düğmesi o bölümün SON biriminin altında (B14)
        foreach (var g in m.Partitions)
        {
            foreach (var v in g.Volumes) block.Children.Add(VolumeRow(d, v));
            if (!m.MultiPartition) continue;
            if (g.RefusedText is { } why) block.Children.Add(Refused(why));
            if (g.Toggle is { } t) block.Children.Add(Right(WriteToggle(d, t)));
        }
        if (!m.MultiPartition && m.Partitions.Count == 1 && m.Partitions[0].RefusedText is { } reason) block.Children.Add(Refused(reason));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        // Denetlenen bölümün birimleri o sırada listede olmaz: düğmesi meşgul olarak dursun (tek gösterge; B18)
        if (!m.MultiPartition && m.Partitions.Count == 1 && m.Partitions[0].Toggle is { } single)
        {
            var toggle = WriteToggle(d, single);
            toggle.Context = m.Title; // çok bölümlüde düğme metni bölümün adını zaten taşır
            actions.Children.Add(toggle);
        }
        if (m.Checking) actions.Children.Add(new ActionButton(m.CheckingText, ButtonKind.Quiet) { BusyText = m.CheckingText, Busy = true }); // en sağda
        if (actions.Children.Count > 0) block.Children.Add(Right(actions));
        return block;
    }

    private const string EjectGlyph = ""; // Segoe Fluent "Eject"

    /// <summary>Kart: Fluent'in kart zemini ve kenarı (ana pencere).</summary>
    private static Border Card(FrameworkElement content)
    {
        var card = new Border
        {
            Child = content, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 14, 14),
            Margin = new Thickness(0, 0, 0, 6),
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    /// <summary>
    /// Sağa yaslı (yazma düğmesi, gösterge). Ana pencerede metni kartın sağ kenarıyla hizalı (-4, kart dışında yer var); panelde
    /// düğmenin kendisi "Aç" sütunuyla hizalı (0): kaydırma alanından taşan kısım kesilir, üstüne gelince çıkan zeminin sağ köşeleri
    /// düz görünüyordu (kullanıcı 2026-11-22; düzenek `fit.txt` "overflow" satırıyla yakalar).
    /// </summary>
    private FrameworkElement Right(FrameworkElement e)
    {
        e.HorizontalAlignment = HorizontalAlignment.Right;
        e.Margin = new Thickness(0, Compact ? 0 : 2, Compact ? 0 : -4, Compact ? 2 : 0);
        return e;
    }

    /// <summary>Ret nedeni: birim adının hizasından başlar.</summary>
    private FrameworkElement Refused(string text)
    {
        var why = UiKit.Text(text, 12, Role.InkSoft);
        why.Margin = Compact ? new Thickness(38, 0, 0, 4) : new Thickness(16 + 30 + 18, 2, 0, 4);
        return why;
    }

    /// <summary>Bölümün "Yazmaya izin ver" (dikkatli eylem tonu) / "Salt okunura dön" (nötr) düğmesi (B15).</summary>
    private ActionButton WriteToggle(DiskStatus d, WriteToggleModel t)
    {
        var b = new ActionButton(t.Text, ButtonKind.Quiet) { BusyText = t.BusyText, Busy = t.Busy, IsEnabled = t.Enabled };
        if (t.Caution) Look.Tone(b, Role.Caution);
        b.Invoked += async () => await _app.SetWritableAsync(d, t.Container, t.Label, t.Enable, Owner());
        return b;
    }

    /// <summary>Birimin tek düğmesi: Aç (Gezgin) / Kilidi aç / Ayrıntı (B9, B10).</summary>
    private ActionButton VolumeButton(DiskStatus d, VolumeModel v)
    {
        var volume = v.Volume;
        // "Kilidi aç" vurgulu; bölüm yazmaya açıkken (kilit açılamaz, "önce Salt okunura dön") sakin
        bool blocked = v.Action == VolumeAction.Unlock && d.Volumes.Any(x => x.Container == volume.Container && x.Writable);
        var kind = v.Action == VolumeAction.Unlock && !blocked ? ButtonKind.Accent : ButtonKind.Secondary;
        var b = new ActionButton(v.ActionText, kind) { Context = v.Drive is { Length: > 0 } letter ? $"{volume.Name} ({letter})" : volume.Name };
        if (v.Action == VolumeAction.Open) b.Invoked += async () => await _app.OpenVolumeAsync(Owner(), volume);
        else if (v.Action == VolumeAction.Unlock) b.Invoked += async () => await _app.UnlockAsync(d, volume, Owner());
        else b.Invoked += async () => await _app.ShowDetailsAsync(Owner(), volume);
        if (v.Action == VolumeAction.Open) b.IsEnabled = v.ActionEnabled;
        if (Compact) b.Padding = new Thickness(10, 3, 10, 4);
        else
        {
            b.MinWidth = 88;
            b.Padding = new Thickness(12, 5, 12, 6);
        }
        return b;
    }

    /// <summary>Birim: solda durum işareti, ortada ad + harf, doluluk çubuğu, durum (+ alan), sağda tek düğme.</summary>
    private FrameworkElement VolumeRow(DiskStatus d, VolumeModel v)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new GateGlyph(GateGlyph.Of(v.Glyph), Compact ? 26 : 30)
        {
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Compact ? 12 : 18, 0),
        });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = UiKit.Text(v.Volume.Name, Compact ? 14 : 16, strong: true, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        // Harf soluk (B11)
        var drive = v.Drive is { Length: > 0 } letter ? UiKit.Text(letter, Compact ? 14 : 16, Role.InkSoft, wrap: false) : null;
        info.Children.Add(new NameLine(name, drive));
        var status = UiKit.Text(v.StatusText, Compact ? 12 : 13, RoleOf(v.StatusRole));
        if (v.Drive is { Length: > 0 } mounted)
        {
            var space = new SpaceView(Compact, status);
            _spaces[mounted] = space;
            info.Children.Add(space.Bar);
            info.Children.Add(space.Line);
            space.Show(_app.Space(mounted));
        }
        else info.Children.Add(UiKit.Margin(status, 0, 4, 0, 0));
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        var button = VolumeButton(d, v);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(Compact ? 12 : 16, 0, 0, 0);
        Grid.SetColumn(button, 2);
        row.Children.Add(button);

        if (Compact)
        {
            row.Margin = new Thickness(0, 5, 0, 7);
            return row;
        }
        row.MinHeight = 44;
        return Card(row);
    }

    private static Role RoleOf(TextRole r) => r switch
    {
        TextRole.Writable => Role.Writable,
        TextRole.Alert => Role.Alert,
        _ => Role.InkSoft,
    };

    /// <summary>
    /// Bir birimin doluluk çubuğu ve alan yazısı ("·" ayıracıyla durumun yanında; ana pencerede "465 GB içinde 153 GB boş", panelde
    /// "153 GB boş"). Sığmazsa yazı durumdan sonra alt satıra geçer ve ayıraç gizlenir; alan bilinmiyorsa ikisi de gizli; boş alan
    /// %10'dan azsa çubuk ve yazı sorun renginde (B12). Değerler yerinde güncellenir (liste yeniden kurulmaz).
    /// </summary>
    private sealed class SpaceView
    {
        private readonly bool _compact;
        private readonly TextBlock _text;

        public SpaceView(bool compact, TextBlock status)
        {
            _compact = compact;
            Bar = new ProgressBar
            {
                Minimum = 0, Maximum = 100, Height = compact ? 4 : 3, MinHeight = 0, Margin = new Thickness(0, 6, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed,
            };
            var dot = UiKit.Text("·", compact ? 12 : 13, Role.InkSoft, wrap: false);
            _text = UiKit.Text("", compact ? 12 : 13, Role.InkSoft);
            Line = new FlowLine(status, dot, _text);
        }

        public ProgressBar Bar { get; }
        public FlowLine Line { get; }

        public void Show(DriveSpace? space)
        {
            Bar.Visibility = UiKit.Vis(space is not null);
            Line.SpaceVisible = space is not null;
            Line.Margin = new Thickness(0, space is null ? 4 : 0, 0, 0);
            if (space is not { } s) return;
            Bar.Value = Math.Clamp(s.Used, 0, 1) * 100;
            AutomationProperties.SetName(Bar, s.Used.ToString("P0", System.Globalization.CultureInfo.CurrentCulture));
            if (s.Low) Bar.Foreground = Look.Brush(Role.Alert);
            else Bar.ClearValue(Control.ForegroundProperty);
            _text.Text = DiskViewModel.SpaceText(s, _compact);
            _text.Foreground = Look.Brush(s.Low ? Role.Alert : Role.InkSoft);
        }
    }

    /// <summary>
    /// Durum + "·" + alan tek satırda; sığmazsa alan alt satıra geçer, ayıraç gizlenir (alt satır "·" ile başlamasın). Karar
    /// ayıraç dahil genişlikle verilir.
    /// </summary>
    private sealed class FlowLine : Panel
    {
        private readonly TextBlock _status, _dot, _space;
        private bool _wrapped;

        public FlowLine(TextBlock status, TextBlock dot, TextBlock space)
        {
            _status = status;
            _dot = dot;
            _space = space;
            dot.Margin = new Thickness(4, 0, 4, 0);
            Children.Add(status);
            Children.Add(dot);
            Children.Add(space);
        }

        public bool SpaceVisible
        {
            get => _space.Visibility == Visibility.Visible;
            set
            {
                _space.Visibility = _dot.Visibility = UiKit.Vis(value);
                InvalidateMeasure();
            }
        }

        protected override Size MeasureOverride(Size available)
        {
            var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
            _status.Measure(inf);
            _dot.Measure(inf);
            _space.Measure(inf);
            double one = _status.DesiredSize.Width + (SpaceVisible ? _dot.DesiredSize.Width + _space.DesiredSize.Width : 0);
            _wrapped = SpaceVisible && one > available.Width;
            if (!_wrapped)
            {
                if (!SpaceVisible && _status.DesiredSize.Width > available.Width) _status.Measure(new Size(available.Width, double.PositiveInfinity));
                return new(Math.Min(one, available.Width), Math.Max(_status.DesiredSize.Height, SpaceVisible ? _space.DesiredSize.Height : 0));
            }
            _status.Measure(new Size(available.Width, double.PositiveInfinity));
            _space.Measure(new Size(available.Width, double.PositiveInfinity));
            return new(available.Width, _status.DesiredSize.Height + _space.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            if (!_wrapped)
            {
                double x = 0;
                foreach (var e in new[] { _status, _dot, _space })
                {
                    if (e.Visibility != Visibility.Visible) { e.Arrange(default); continue; }
                    double w = Math.Min(e.DesiredSize.Width, Math.Max(0, final.Width - x));
                    e.Arrange(new Rect(x, 0, w, e.DesiredSize.Height));
                    x += e.DesiredSize.Width;
                }
                return final;
            }
            _status.Arrange(new Rect(0, 0, final.Width, _status.DesiredSize.Height));
            _dot.Arrange(default);
            _space.Arrange(new Rect(0, _status.DesiredSize.Height, final.Width, _space.DesiredSize.Height));
            return final;
        }
    }

    /// <summary>Birim adı + sürücü harfi tek satırda: ad sığmazsa üç noktayla kısalır, harf hep adın hemen sağında görünür.</summary>
    private sealed class NameLine : Panel
    {
        private const double Gap = 8;
        private readonly TextBlock _name;
        private readonly TextBlock? _drive;

        public NameLine(TextBlock name, TextBlock? drive)
        {
            _name = name;
            _drive = drive;
            Children.Add(name);
            if (drive is not null) Children.Add(drive);
        }

        protected override Size MeasureOverride(Size available)
        {
            var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
            double driveWidth = 0, height = 0;
            if (_drive is not null)
            {
                _drive.Measure(inf);
                driveWidth = _drive.DesiredSize.Width + Gap;
                height = _drive.DesiredSize.Height;
            }
            _name.Measure(new Size(Math.Max(0, available.Width - driveWidth), double.PositiveInfinity));
            return new(Math.Min(available.Width, _name.DesiredSize.Width + driveWidth), Math.Max(height, _name.DesiredSize.Height));
        }

        protected override Size ArrangeOverride(Size final)
        {
            double driveWidth = _drive is null ? 0 : _drive.DesiredSize.Width + Gap;
            double nameWidth = Math.Min(_name.DesiredSize.Width, Math.Max(0, final.Width - driveWidth));
            _name.Arrange(new Rect(0, 0, nameWidth, final.Height));
            _drive?.Arrange(new Rect(nameWidth + Gap, 0, _drive.DesiredSize.Width, final.Height));
            return final;
        }
    }
}

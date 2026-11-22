using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Apfs.Tray.Ui;

/// <summary>
/// WinUI'nin kendiliğinden verdiği üç hareket, WPF'te yok (kabul turu bulgusu, kullanıcı 2026-11-21: "WinUI'deki gibi ekle"):
/// Diskler ⇄ Ayarlar sayfa geçişi (WinUI <c>DrillInNavigationTransitionInfo</c>), tekerlekle yumuşak kaydırma (WinUI
/// <c>ScrollViewer</c>) ve açılan menünün düğmeden açılışı (WinUI <c>MenuFlyout</c>). Ölçüler WinUI'nin Fluent hareket
/// değerlerinden; Windows'un "Animasyon efektleri" kapalıysa (Erişilebilirlik › Görsel efektler) hiçbiri oynamaz, anında olur.
/// </summary>
internal static class Motion
{
    /// <summary>Windows'un "Animasyon efektleri" ayarı (WinUI de buna uyar); görüntü düzeneğinde kapalı (çekimler hareketin ortasına denk gelmesin).</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation && !Off;

    /// <summary>Görüntü düzeneği kapatır (<see cref="Dev.ShotRig"/>).</summary>
    public static bool Off { get; set; }

    // Fluent "decelerate" eğrisi (WinUI sayfa ve menü geçişleri) ve "accelerate" (çıkış)
    private static readonly KeySpline Decelerate = Frozen(new KeySpline(0.1, 0.9, 0.2, 1.0));
    private static readonly KeySpline Accelerate = Frozen(new KeySpline(0.7, 0.0, 1.0, 0.5));
    // Menü açılışı: WinUI MenuFlyout'un ekranda ölçülen eğrisi (2026-11-21, `localization-tool/wpf/popup-anim.cs`: yarı açık
    // başlar, ~230 ms'de tamamlanır) Fluent "fast out, slow in" (0,0,0,1) 250 ms ile kare kare örtüşüyor (fark ≤ 0,05)
    private static readonly KeySpline FastOutSlowIn = Frozen(new KeySpline(0, 0, 0, 1));

    private static KeySpline Frozen(KeySpline k) { k.Freeze(); return k; }

    // ---- Sayfa geçişi (DrillIn) ----

    /// <summary>
    /// <paramref name="host"/>'ta <paramref name="from"/>'dan <paramref name="to"/>'ya geçer. İleri (Ayarlar'a): eski sayfa
    /// büyüyerek solar (100 ms), yeni sayfa %94'ten büyüyerek belirir; geri: eski küçülerek solar, yeni %106'dan küçülerek gelir
    /// (belirme 333 ms, ölçek 783 ms — WinUI DrillIn). Yeni sayfa HEMEN ağaçtadır (odak verilebilir); eski sayfa geçiş boyunca
    /// tıklanmaz, sonunda kaldırılır.
    /// </summary>
    public static void DrillIn(Grid host, UIElement? from, UIElement to, bool forward)
    {
        host.Tag = to; // geçerli sayfa: yarıda kalan geçişin bitişi bunu kaldırmasın/bozmasın (hızlı ileri-geri)
        host.Children.Clear(); // yarıda kalmış geçişin sayfası dahil
        Reset(to);
        host.Children.Add(to);
        if (!Enabled || from is null || from == to) { if (from is not null && from != to) Reset(from); return; }

        host.Children.Insert(0, from); // altta kalır, yeni sayfa üstte
        from.IsHitTestVisible = false;
        var exit = Scale(from);
        double exitTo = forward ? 1.04 : 0.96;
        Animate(from, UIElement.OpacityProperty, 1, 0, 0, 100, Accelerate);
        Animate(exit, ScaleTransform.ScaleXProperty, 1, exitTo, 0, 100, Accelerate);
        Animate(exit, ScaleTransform.ScaleYProperty, 1, exitTo, 0, 100, Accelerate).Completed += (_, _) =>
        {
            if (host.Tag == from) return; // bu arada geri dönüldü: sayfa yeniden geçerli
            host.Children.Remove(from);
            Reset(from);
        };

        var enter = Scale(to);
        double enterFrom = forward ? 0.94 : 1.06;
        to.Opacity = 0;
        Animate(to, UIElement.OpacityProperty, 0, 1, 100, 333, Decelerate);
        Animate(enter, ScaleTransform.ScaleXProperty, enterFrom, 1, 100, 783, Decelerate);
        Animate(enter, ScaleTransform.ScaleYProperty, enterFrom, 1, 100, 783, Decelerate).Completed += (_, _) =>
        {
            if (host.Tag == to) Reset(to);
        };
    }

    private static ScaleTransform Scale(UIElement e)
    {
        var s = new ScaleTransform(1, 1);
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        e.RenderTransform = s;
        return s;
    }

    /// <summary>Animasyonsuz son hâl: tam görünür, dönüşümsüz (metin keskin kalsın), tıklanır.</summary>
    private static void Reset(UIElement e)
    {
        e.BeginAnimation(UIElement.OpacityProperty, null);
        e.Opacity = 1;
        e.RenderTransform = Transform.Identity;
        e.IsHitTestVisible = true;
    }

    private static AnimationClock Animate(DependencyObject target, DependencyProperty p, double from, double to, int delayMs, int ms, KeySpline curve)
    {
        var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(delayMs), FillBehavior = FillBehavior.HoldEnd };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), curve));
        var clock = a.CreateClock();
        ((IAnimatable)target).ApplyAnimationClock(p, clock);
        return clock;
    }

    // ---- Yumuşak kaydırma ----

    /// <summary>
    /// Tekerlek iletisi, büyüklüğüyle orantılı kaydırır: tam çentik (120) Windows'un satır ayarı kadar (satır × 16 px; WPF'in kendi
    /// adımı), yüksek çözünürlüklü tekerlek ve dokunmatik yüzeyin küçük adımları o oranda. WPF bunu yapmıyor: HER iletiyi tam çentik
    /// sayıp 48 px atlıyordu (küçük adımlı aygıtta sarsıntılı, aşırı hızlı kaydırma — kabul turu 2026-11-21). İçerik hedefe her karede
    /// kritik sönümlü bir yayla gider (~260 ms'de oturur): art arda gelen iletiler hızı sıçratmadan hedefi büyütür. Açık bir açılır listenin (ayrı pencere) tekerleği ona kalır.
    /// <para>Kare başına yerleşim YOK: kaydırma ofseti tekerlek iletisinde bir kez hedefe atlar, aradaki yol içeriğin çizim
    /// kaydırmasıyla (GPU) kapanır. Her karede ofset değiştirmek her karede yerleşim demekti ve kare kaçırıyordu (ölçüldü,
    /// 2026-11-21 <c>Dev/MotionProbe</c>: kare aralığı ort. 9,7 ms, en çok 32 ms; 160 Hz ekranda 6,25 ms olmalı).</para>
    /// </summary>
    public static void SmoothWheel(ScrollViewer view)
    {
        double target = 0, pos = 0, vel = 0; // target: gerçek ofset (yerleşim), pos: görünen ofset, vel: DIP/ms
        bool running = false;
        TimeSpan last = TimeSpan.Zero;
        var shift = new TranslateTransform();
        // Kritik sönümlü yay: yeni çentik hedefi büyütünce hız sıçramaz, sürekli artar (üstel yaklaşımda her çentik hızı birden
        // değiştiriyordu: adımlar 1,5 → 6,1 DIP; ölçüldü). Tek çentik ~260 ms'de oturur.
        const double Omega = 0.025; // 1/ms

        // İçerik, görünen ofsete göre çizilir: gerçek ofset → görünen ofset farkı kadar kaydırılır
        void Apply() => shift.Y = view.VerticalOffset - pos;

        void Step(object? sender, EventArgs e)
        {
            var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
            if (now == last) return; // aynı kare için ikinci çağrı
            double dt = last == TimeSpan.Zero ? 1000.0 / 60 : Math.Min(50, (now - last).TotalMilliseconds);
            last = now;
            // Kesin çözüm: e(t) = (e0 + (v0 + ωe0)t)·e^(−ωt), v(t) = (v0 − ω(v0 + ωe0)t)·e^(−ωt)
            double e0 = pos - target, c = vel + Omega * e0, k = Math.Exp(-Omega * dt);
            pos = target + (e0 + c * dt) * k;
            vel = (vel - Omega * c * dt) * k;
            if (Math.Abs(target - pos) < 0.25 && Math.Abs(vel) < 0.005) { pos = target; vel = 0; }
            Apply();
            if (pos == target) Stop();
        }
        void Stop()
        {
            if (!running) return;
            running = false;
            CompositionTarget.Rendering -= Step;
            pos = target = view.VerticalOffset;
            vel = 0;
            shift.Y = 0;
        }

        view.PreviewMouseWheel += (_, e) =>
        {
            if (view.ScrollableHeight <= 0) { Stop(); return; }
            if (e.OriginalSource is Visual v && PresentationSource.FromVisual(v) != PresentationSource.FromVisual(view)) return;
            if (view.Content is UIElement content && content.RenderTransform != shift) content.RenderTransform = shift;
            int lines = SystemParameters.WheelScrollLines;
            double notch = lines > 0 ? lines * 16 : view.ViewportHeight; // -1 = "bir ekran"
            double delta = -e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * notch;
            e.Handled = true;
            if (!running) { target = pos = view.VerticalOffset; vel = 0; } // klavye/çubuk kaydırmış olabilir
            target = Math.Clamp(target + delta, 0, view.ScrollableHeight);
            view.ScrollToVerticalOffset(target); // yerleşim bir kez; çizim kayması ScrollChanged'de ona uyar
            if (!Enabled) { Stop(); return; }
            if (!running && target != pos)
            {
                running = true;
                last = TimeSpan.Zero;
                CompositionTarget.Rendering += Step;
            }
        };
        view.ScrollChanged += (_, e) =>
        {
            if (!running) return;
            // Klavye, kaydırma çubuğu ya da odaklanan öğenin görünür kılınması başka bir yere kaydırdıysa onlar kazanır
            if (e.VerticalChange != 0 && Math.Abs(view.VerticalOffset - target) > 0.5) { Stop(); return; }
            Apply(); // ofset hedefe vardığı yerleşimde, aynı karede çizim kayması da ona göre
        };
        view.PreviewMouseDown += (_, _) => Stop();
        view.PreviewKeyDown += (_, _) => Stop();
        view.Unloaded += (_, _) => Stop();
    }

    // ---- Açan düğmeye basmak yalnız kapatır ----

    /// <summary>
    /// WinUI'de açık menü/liste dışarı tıklanınca o tık yalnız kapatır; açan düğmeye basmak listeyi kapatıp yeniden AÇMAZ. WPF'te
    /// dışarı basış listeyi kapatıp aynı basışla düğmeye de ulaşıyor, liste yeniden açılıyordu (kabul turu 2026-11-21). WPF'in olay
    /// sırasına (yakalama, açılır pencerenin kapanışı, olayın yeniden yönlendirilmesi) bel bağlamamak için basış GİRDİ katmanında
    /// (<see cref="InputManager.PreProcessInput"/>, hiçbir öğe görmeden) yakalanır: liste açıkken açanın üstüne (listenin kendi
    /// üstüne değil) gelen sol basış listeyi kapatır ve iptal edilir — ne açılır pencere ne düğme onu görür.
    /// </summary>
    public static void PressOnlyCloses(FrameworkElement opener, Func<bool> isOpen, Action close, Func<FrameworkElement?> list)
    {
        static bool Inside(FrameworkElement f)
        {
            if (!f.IsVisible || PresentationSource.FromVisual(f) is null) return false;
            var p = Mouse.GetPosition(f);
            return p.X >= 0 && p.Y >= 0 && p.X < f.ActualWidth && p.Y < f.ActualHeight;
        }
        void Pre(object? sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is not MouseButtonEventArgs mb || mb.RoutedEvent != Mouse.PreviewMouseDownEvent
                || mb.ChangedButton != MouseButton.Left || !isOpen()) return;
            if (list() is { } l && Inside(l)) return; // listenin kendi öğesine basış
            if (!Inside(opener)) return;
            close();
            e.Cancel();
        }
        // Genel girdi olayına yalnız açan ağaçtayken bağlı kalınır (Ayarlar her açılışta yeniden kurulur)
        opener.Loaded += (_, _) => { InputManager.Current.PreProcessInput -= Pre; InputManager.Current.PreProcessInput += Pre; };
        opener.Unloaded += (_, _) => InputManager.Current.PreProcessInput -= Pre;
    }

    public static void PressOnlyCloses(Button opener, ContextMenu menu) =>
        PressOnlyCloses(opener, () => menu.IsOpen, () => menu.IsOpen = false, () => menu);

    public static void PressOnlyCloses(ComboBox combo) =>
        PressOnlyCloses(combo, () => combo.IsDropDownOpen, () => combo.IsDropDownOpen = false,
            () => (combo.Template?.FindName("PART_Popup", combo) as Popup)?.Child as FrameworkElement);

    // ---- Açılır liste (ComboBox) ----

    /// <summary>
    /// WinUI ComboBox gibi: liste kutunun ALTINA kaymaz, kutunun üstüne seçili öğe kutunun yerine gelecek biçimde ANINDA açılır
    /// (ekranda ölçüldü, 2026-11-21: WinUI'de liste tek karede tam boy; WPF Windows'un "açılır kutuları kaydırarak aç" hareketiyle
    /// aşağı kayıyordu) ve en az kutu genişliğindedir. Ekrana sığmazsa WPF'in kendi yerleşimi (altına) kalır.
    /// </summary>
    public static void WinUiDropDown(ComboBox combo)
    {
        if (Application.Current is { } app) app.Resources[SystemParameters.ComboBoxPopupAnimationKey] = PopupAnimation.None;
        combo.Loaded += (_, _) =>
        {
            combo.ApplyTemplate();
            if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup || popup.Placement == PlacementMode.Custom) return;
            popup.PopupAnimation = PopupAnimation.None;
            if (popup.Child is FrameworkElement child)
                child.SetBinding(FrameworkElement.MinWidthProperty, new System.Windows.Data.Binding(nameof(FrameworkElement.ActualWidth)) { Source = combo });
            popup.Placement = PlacementMode.Custom;
            popup.PlacementTarget = combo;
            popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                // Geri çağrının ölçüleri aygıt pikseli; öğe konumu DIP → ölçekle
                double scale = VisualTreeHelper.GetDpi(combo).DpiScaleY;
                double y = 0;
                int index = Math.Max(0, combo.SelectedIndex);
                if (popup.Child is UIElement root && combo.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement item
                    && item.IsVisible && root.IsAncestorOf(item))
                {
                    var p = item.TranslatePoint(new Point(0, 0), root);
                    y = -(p.Y + item.ActualHeight / 2 - combo.ActualHeight / 2) * scale;
                }
                else if (combo.Items.Count > 0)
                    y = -(popupSize.Height / combo.Items.Count * (index + 0.5) - targetSize.Height / 2);
                return
                [
                    new CustomPopupPlacement(new Point(0, y), PopupPrimaryAxis.None),
                    new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.Horizontal),
                ];
            };
        };
    }

    // ---- Menü açılışı ----

    /// <summary>
    /// Düğmenin üstünde açılan menü, düğmeye bitişik kenarından yukarı doğru açılır: içerik yarı boyundan yerine kayarken
    /// kenarın ötesi kırpılır (250 ms, 0,0,0,1 — WinUI <c>MenuFlyout</c>, ölçüldü). Kapanış anında (WinUI'de de öyle). Windows'un klasik menü solması bu menüde kapatılır
    /// (iki hareket üst üste binmesin). Menüyü açmadan ÖNCE çağrılır.
    /// </summary>
    public static void PrepareMenuReveal(ContextMenu menu)
    {
        if (!Enabled) return;
        menu.Opacity = 0; // ilk kare (yerleşim ölçülmeden) görünmesin
        if (menu.Parent is Popup p) p.PopupAnimation = PopupAnimation.None;
    }

    /// <summary><see cref="PrepareMenuReveal"/> ile hazırlanan menüyü açılınca oynatır (<c>Opened</c>'a bağlanır, bir kez).</summary>
    public static void AttachMenuReveal(ContextMenu menu)
    {
        // Menünün açılır penceresi ilk açılışta kurulur ve solmasını uygulama kaynağından alır: ilk açılışta da solmasın
        if (Application.Current is { } app) app.Resources[SystemParameters.MenuPopupAnimationKey] = PopupAnimation.None;
        EventHandler? frame = null;
        menu.Opened += (_, _) =>
        {
            if (menu.Parent is Popup p) p.PopupAnimation = PopupAnimation.None; // sonraki açılışlar için de
            if (frame is not null) CompositionTarget.Rendering -= frame;
            double h = menu.ActualHeight, w = menu.ActualWidth;
            if (!Enabled || h <= 0) { menu.Opacity = 1; return; }
            // İçerik aşağıdan (düğme tarafından) yerine kayar; kırpma kayan içeriğin koordinatında olduğundan görünen kısım hep
            // asıl alt kenarda biter: ikisi tek eğriyle sürülür
            var shift = new TranslateTransform(0, h / 2);
            var clip = new RectangleGeometry(new Rect(0, 0, w, h / 2));
            menu.RenderTransform = shift;
            menu.Clip = clip;
            menu.Opacity = 1;
            var clock = Stopwatch.StartNew();
            frame = (_, _) =>
            {
                double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / 250);
                double s = h / 2 * (1 - FastOutSlowIn.GetSplineProgress(t));
                shift.Y = s;
                clip.Rect = new Rect(0, 0, w, h - s);
                if (t < 1) return;
                CompositionTarget.Rendering -= frame;
                frame = null;
                menu.Clip = null;
                menu.RenderTransform = Transform.Identity;
            };
            CompositionTarget.Rendering += frame;
        };
        menu.Closed += (_, _) =>
        {
            if (frame is not null) CompositionTarget.Rendering -= frame;
            frame = null;
            menu.Clip = null;
            menu.RenderTransform = Transform.Identity;
        };
    }
}

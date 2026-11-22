using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Apfs.Tray.Ui;
using Apfs.Localization;
using Apfs.Windows.Localization;
using Apfs.Windows.Tray;

namespace Apfs.Tray.Dev;

/// <summary>
/// Hareket ölçümü (wpfPlan 3.2 kabul turu bulgusu, 2026-11-21; yalnız geliştirme kanalı, görüntü düzeneğinin <c>--view probe</c>
/// görünümü): ana pencere EKRAN DIŞINDA açılır; açılır listeler ve dil menüsü (ayrı pencereler; WPF onları ekrana kıstırır)
/// SAYDAM açılır, yerleri ekran koordinatıyla yazılır; kaydırma tekerlek iletileri gönderilerek kare kare izlenir. Fare yok.
/// Çıktı: <c>--shot</c> yolundaki metin dosyası (İngilizce: geliştirme çıktısı, kullanıcı metni değil).
/// </summary>
internal static class MotionProbe
{
    public static async Task RunAsync(MainWindow main, string outPath)
    {
        var o = new List<string> { $"animation effects: {SystemParameters.ClientAreaAnimation}; dpi scale {VisualTreeHelper.GetDpi(main).DpiScaleY}" };
        // ---- Dil menüsü ----
        string native = Languages.Supported.FirstOrDefault(l => l.Name == System.Globalization.CultureInfo.CurrentUICulture.Name).NativeName ?? "English";
        var language = Find<Button>(main).FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == native);
        if (language is not null)
        {
            language.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, language));
            var menu = Opened<ContextMenu>();
            await Task.Delay(30);
            if (menu is not null) Hide(menu);
            await Task.Delay(400);
            if (menu is { IsOpen: true })
            {
                o.Add($"language button: {Rect(language)}");
                o.Add($"language menu:   {Rect(menu)}  (bottom edge {Rect(language).Top - Rect(menu).Bottom:0} px above the button)");
                menu.IsOpen = false;
            }
            else o.Add("language menu DID NOT OPEN");
        }
        else o.Add("language button not found");

        // ---- Ayarlar: açılır listeler ----
        main.OpenSettings();
        await Task.Delay(1200);
        var combos = Find<ComboBox>(main).ToList();
        foreach (var (combo, k) in combos.Select((c, k) => (c, k)))
        {
            foreach (int pick in new[] { 0, combo.Items.Count - 1 })
            {
                combo.SelectedIndex = pick;
                combo.ApplyTemplate();
                if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup) { o.Add($"combo {k}: no PART_Popup"); continue; }
                if (popup.Child is UIElement c0) c0.Opacity = 0;
                combo.IsDropDownOpen = true;
                await Task.Delay(400);
                var child = (FrameworkElement)popup.Child;
                var item = combo.ItemContainerGenerator.ContainerFromIndex(pick) as FrameworkElement;
                o.Add($"combo {k} ({combo.Items.Count} items, selected {pick}): box {Rect(combo)}; list {Rect(child)}; selected item {(item is null ? "-" : Rect(item).ToString())}; placement {popup.Placement}, animation {popup.PopupAnimation}, transparent {popup.AllowsTransparency}");
                combo.IsDropDownOpen = false;
                child.Opacity = 1;
                await Task.Delay(150);
            }
        }

        // ---- Kaydırma ----
        var scroll = Find<ScrollViewer>(main).FirstOrDefault(s => s.ScrollableHeight > 0);
        if (scroll is null) o.Add("no scrollable area");
        else
        {
            o.Add($"scroll: viewport {scroll.ViewportHeight:0}, scrollable {scroll.ScrollableHeight:0} DIP");
            await Wheel(o, scroll, "3 full notches (40 ms apart), down", Enumerable.Repeat((-120, 40), 3));
            await Wheel(o, scroll, "3 full notches, up", Enumerable.Repeat((120, 40), 3));
            await Wheel(o, scroll, "high-resolution wheel: 12 x 30 (8 ms apart), down", Enumerable.Repeat((-30, 8), 12));
        }
        await File.WriteAllLinesAsync(outPath, o);
    }

    private static async Task Wheel(List<string> o, ScrollViewer scroll, string title, IEnumerable<(int Delta, int GapMs)> steps)
    {
        var frames = new List<(double T, double Offset, double Gap)>();
        var sw = Stopwatch.StartNew();
        double lastWall = 0;
        int layouts = 0;
        EventHandler onLayout = (_, _) => layouts++;
        scroll.LayoutUpdated += onLayout;
        EventHandler tick = (_, e) =>
        {
            double now = sw.Elapsed.TotalMilliseconds;
            // görünen ofset = gerçek ofset − içeriğin çizim kayması (Motion.SmoothWheel)
            double shown = scroll.VerticalOffset - ((((scroll.Content as UIElement)?.RenderTransform) as TranslateTransform)?.Y ?? 0);
            frames.Add((now, shown, now - lastWall));
            lastWall = now;
        };
        CompositionTarget.Rendering += tick;
        double start = scroll.VerticalOffset;
        var target = scroll.Content as IInputElement ?? scroll;
        foreach (var (delta, gap) in steps)
        {
            var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            target.RaiseEvent(preview);
            if (!preview.Handled) target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent });
            await Task.Delay(gap);
        }
        await Task.Delay(700);
        CompositionTarget.Rendering -= tick;
        scroll.LayoutUpdated -= onLayout;
        // Hareket bittikten sonra WPF boşta kare üretmez (uzun aralık hata değil): yalnız son hareketli kareye kadar sayılır
        int lastMoving = frames.FindLastIndex(f => frames.IndexOf(f) > 0 && Math.Abs(f.Offset - frames[frames.IndexOf(f) - 1].Offset) > 0.001);
        if (lastMoving > 0) frames = frames.Take(lastMoving + 1).ToList();
        var moving = frames.Where((f, i) => i > 0 && f.Offset != frames[i - 1].Offset).ToList();
        double maxGap = frames.Skip(1).Select(f => f.Gap).DefaultIfEmpty().Max();
        var steps2 = frames.Select((f, i) => i == 0 ? 0 : f.Offset - frames[i - 1].Offset).Where(d => d != 0).ToList();
        o.Add($"-- {title}: {start:0} -> {scroll.VerticalOffset:0} DIP; moving frames {moving.Count}, duration {(moving.Count > 0 ? moving[^1].T - moving[0].T : 0):0} ms; " +
              $"frame gap max {maxGap:0.0} ms (avg {frames.Skip(1).Select(f => f.Gap).DefaultIfEmpty().Average():0.0}; over 12 ms: {frames.Skip(1).Count(f => f.Gap > 12)}); layouts {layouts}");
        o.Add("   long frames (ms): " + string.Join(" ", frames.Select((f, i) => (f, i)).Where(x => x.i > 0 && x.f.Gap > 12).Select(x => $"#{x.i}:{x.f.Gap:0}")));
        o.Add("   step per frame (DIP): " + string.Join(" ", steps2.Select(d => d.ToString("0.0"))));
    }

    private static T? Opened<T>() where T : DependencyObject =>
        PresentationSource.CurrentSources.OfType<PresentationSource>().Select(s => s.RootVisual).SelectMany(r => r is null ? [] : Find<T>(r)).FirstOrDefault();

    private static void Hide(UIElement e) => e.Opacity = 0;

    private static Rect Rect(FrameworkElement e)
    {
        var p = e.PointToScreen(new Point(0, 0));
        var q = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return new Rect(p, q);
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Find<T>(c)) yield return d;
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using Apfs.Windows;
using Apfs.Windows.Tray;

namespace Apfs.Tray.Ui;

/// <summary>
/// Onay, bilgi ve Hakkında kutusu (1.6.0'dan beri temalı; 1.8.0'a kadar WinForms MessageDialog). Pencere başlığı "Hashido";
/// içerikte solda ikon (uyarı üçgeni / bilgi dairesi rol renginde, Hakkında'da mühür), başlık, kaydırılan metin.
/// <list type="bullet">
/// <item><see cref="ConfirmAsync"/>: devam düğmesi tonlu ikincil, "Vazgeç" birincil ve VARSAYILAN — açılışta odak Vazgeç'te,
/// Enter da Esc de kapatma da Vazgeç; Tab ile devam düğmesine gelinirse Enter onu basar (Ek A S1).</item>
/// <item><see cref="InformAsync"/>: tek "Tamam" (Enter/Esc).</item>
/// <item><see cref="AboutAsync"/>: sürüm, açıklama, depo bağlantısı ve WinFsp dipnotu (WinFsp lisansının şartı, kaldırılmaz).</item>
/// </list>
/// </summary>
internal sealed class MessageWindow : DialogWindow
{
    public const string RepoUrl = "https://github.com/ErsinZorba/Hashido";
    public const string WinFspUrl = "https://github.com/winfsp/winfsp";
    public const string WinFspNotice = "WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos";

    private bool _proceed;
    private readonly StackPanel _texts = new();
    private readonly StackPanel _body = new();

    private MessageWindow(Window? owner, string? heading, string text, FrameworkElement icon) : base(Branding.ProductName, owner)
    {
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 16, 0);
        top.Children.Add(icon);
        if (!string.IsNullOrEmpty(heading)) _texts.Children.Add(UiKit.Margin(UiKit.Heading(heading, 20), 0, 0, 0, 8));
        _texts.Children.Add(UiKit.Text(text));
        Grid.SetColumn(_texts, 1);
        top.Children.Add(_texts);
        _body.Children.Add(top);
    }

    /// <summary>Varsayılan düğme: onayda Vazgeç, bilgide Tamam — ikisi de yalnız kapatır, devam DEĞİL.</summary>
    protected override void OnEnter() => Close();

    private static FrameworkElement IconOf(string glyph, Role role)
    {
        var i = UiKit.Icon(glyph, 28);
        i.Foreground = Look.Brush(role);
        return i;
    }

    private const string WarningGlyph = "", InfoGlyph = "";

    /// <returns>Devam düğmesine basıldıysa true (Vazgeç, Esc, Enter ya da kapatma: false).</returns>
    public static async Task<bool> ConfirmAsync(Window? owner, string heading, string text, string proceed, ConfirmTone tone)
    {
        var role = tone == ConfirmTone.Caution ? Role.Caution : Role.Alert;
        var d = new MessageWindow(owner, heading, text, IconOf(WarningGlyph, role));
        var go = Look.Tone(Button(proceed, ButtonKind.Secondary, () => { d._proceed = true; d.Close(); }), role);
        var cancel = Button(WinText.U("Action.Cancel"), ButtonKind.Accent, d.Close);
        d.Layout(d._body, go, cancel);
        await d.ShowAndWaitAsync(cancel);
        return d._proceed;
    }

    public static Task InformAsync(Window? owner, string? heading, string text, InformIcon icon)
    {
        var d = new MessageWindow(owner, heading, text, icon == InformIcon.Warning ? IconOf(WarningGlyph, Role.Alert) : IconOf(InfoGlyph, Role.Ink));
        var ok = Button(WinText.U("Action.OK"), ButtonKind.Accent, d.Close);
        d.Layout(d._body, ok);
        return d.ShowAndWaitAsync(ok);
    }

    /// <summary>Hakkında: mühür, "Hashido &lt;sürüm&gt;", açıklama, depo bağlantısı, WinFsp dipnotu ve bağlantısı.</summary>
    public static Task AboutAsync(Window? owner, string version)
    {
        var d = new MessageWindow(owner, $"{Branding.ProductName} {version}", WinText.U("About.Text"), Look.Seal(32));
        d._texts.Children.Add(UiKit.Margin(UiKit.Link("github.com/ErsinZorba/Hashido", RepoUrl), 0, 8, 0, 0));
        var rule = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 16) };
        rule.SetResourceReference(Border.BackgroundProperty, "CardStrokeColorDefaultBrush");
        d._body.Children.Add(rule);
        d._body.Children.Add(UiKit.Text(WinFspNotice, 12, Role.InkSoft));
        d._body.Children.Add(UiKit.Margin(UiKit.Link("github.com/winfsp/winfsp", WinFspUrl, 12), 0, 2, 0, 0));
        var ok = Button(WinText.U("Action.OK"), ButtonKind.Accent, d.Close);
        d.Layout(d._body, ok);
        return d.ShowAndWaitAsync(ok);
    }
}

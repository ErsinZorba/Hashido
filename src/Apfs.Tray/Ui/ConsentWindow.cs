using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Apfs.Windows;
using Apfs.Windows.Reporting;

namespace Apfs.Tray.Ui;

/// <summary>
/// İlk açılışta bir kez sorulan raporlama onayı (Ek A M2; 1.8.0'a kadar WinForms ConsentForm). Ne gönderildiği ve neyin asla
/// gönderilmediği listelenir. "Evet" ile "Hayır" AYNI boyda ve aynı görünüşte; "Evet" yalnız varsayılan odaktır (kullanıcı kararı
/// 2026-11-07; odak çerçevesi fareyle açılışta da görünür). Ekranın ortasında, görev çubuğunda, en üstte DEĞİL (sahipliyken de).
/// Kapatılırsa (Esc, X) cevap verilmemiş sayılır: hiçbir şey toplanmaz, bir sonraki açılışta yine sorulur.
/// </summary>
internal sealed class ConsentWindow : DialogWindow
{
    private bool? _answer;
    private readonly Button _yes;

    private ConsentWindow(Window? owner) : base(Branding.ProductName, owner, topmost: false, inTaskbar: true, centerOnScreen: true)
    {
        ContentWidth = 512;
        var body = new StackPanel();
        body.Children.Add(UiKit.Heading(WinText.U("Consent.Heading", Branding.ProductName), 20));
        body.Children.Add(UiKit.Margin(UiKit.Text(WinText.U("Consent.Text", Branding.ProductName)), 0, 8, 0, 0));
        body.Children.Add(UiKit.Margin(UiKit.Text(WinText.U("Consent.SentHeading"), strong: true), 0, 14, 0, 0));
        body.Children.Add(UiKit.Margin(UiKit.Text(WinText.U("Consent.SentList"), role: Role.InkSoft), 0, 4, 0, 0));
        body.Children.Add(UiKit.Margin(UiKit.Text(WinText.U("Consent.NeverHeading"), strong: true), 0, 10, 0, 0));
        body.Children.Add(UiKit.Margin(UiKit.Text(WinText.U("Consent.NeverList"), role: Role.InkSoft), 0, 4, 0, 0));
        body.Children.Add(UiKit.Margin(PrivacyLink(), 0, 10, 0, 0));
        _yes = Button(WinText.U("Consent.Yes"), ButtonKind.Secondary, () => { _answer = true; Close(); });
        var no = Button(WinText.U("Consent.No"), ButtonKind.Secondary, () => { _answer = false; Close(); });
        Layout(body, _yes, no);
    }

    /// <summary>Varsayılan düğme "Evet" (odak bir düğmede değilken Enter).</summary>
    protected override void OnEnter()
    {
        _answer = true;
        Close();
    }

    /// <summary>Gizlilik açıklaması bağlantısı (Ayarlar da kullanır, kendi metniyle).</summary>
    public static TextBlock PrivacyLink(string? text = null) => UiKit.Link(text ?? WinText.U("Consent.Privacy"), ReportClient.PrivacyUrl);

    /// <param name="owner">Ana pencere görünürse o, değilse null.</param>
    /// <returns>true: evet, false: hayır, null: cevaplanmadı (kapatıldı).</returns>
    public static async Task<bool?> AskAsync(Window? owner)
    {
        var w = new ConsentWindow(owner);
        await w.ShowAndWaitAsync(w._yes);
        return w._answer;
    }
}

/// <summary>Gönderilen son raporlar, gönderildiği biçimiyle (en yenisi başta; salt okunur). "Kapat" ve Esc kapatır (Ek A N5).</summary>
internal sealed class SentWindow : DialogWindow
{
    private readonly Button _close;

    private SentWindow(Window? owner, SentLog sent) : base(WinText.U("Sent.Title"), owner)
    {
        ContentWidth = 600;
        var lines = sent.Read();
        var body = new StackPanel();
        body.Children.Add(UiKit.Text(lines.Count == 0 ? WinText.U("Sent.None") : WinText.U("Sent.Intro")));
        var pretty = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var text = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 360, Margin = new Thickness(0, 12, 0, 0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = string.Join("\r\n\r\n", Enumerable.Reverse(lines).Select(l =>
            {
                try { return JsonNode.Parse(l)!.ToJsonString(pretty).Replace("\n", "\r\n"); } catch (JsonException) { return l; }
            })),
        };
        if (lines.Count > 0) body.Children.Add(text);
        _close = Button(WinText.U("Action.Close"), ButtonKind.Secondary, Close);
        Layout(body, _close);
    }

    public static Task ShowAsync(Window? owner, SentLog sent)
    {
        var w = new SentWindow(owner, sent);
        return w.ShowAndWaitAsync(w._close); // açılışta seçim yok: odak "Kapat"ta
    }
}

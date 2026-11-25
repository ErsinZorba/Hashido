using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Apfs.Windows;
using Apfs.Windows.Tray;

namespace Apfs.Tray.Ui;

/// <summary>
/// Şifreli birimin parola penceresi (Ek A H16; 1.8.0'a kadar WinForms PasswordForm). Her zaman sahipsiz: ekranın ortasında, en
/// üstte, görev çubuğunda. Kilit işareti, "«ad» kilitli" + istem, ipucu, gizli alan + göster/gizle, hata satırı, "Bu
/// bilgisayarda hatırla" + not, güven satırı; düğmeler solda "İptal", sağda "Kilidi aç". Esc = İptal; Enter odaktaki düğmeyi basar
/// (başta odak alanda → Kilidi aç). Boş parola engellenmez (H20). "Kilidi aç"ta pencere HEMEN kapanır. Parola hizmete gider;
/// kaydı yalnız kutu işaretliyse çağıran yapar.
/// </summary>
internal sealed class PasswordWindow : DialogWindow
{
    private readonly PasswordBox _hidden = new();
    private readonly TextBox _shown = new() { Visibility = Visibility.Collapsed };
    private readonly CheckBox? _remember;
    private PasswordAnswer? _answer;

    private PasswordWindow(PasswordRequest r) : base(WinText.U("Password.Title", Branding.ProductName), owner: null)
    {
        ContentWidth = 432;
        var body = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.Children.Add(new GateGlyph(GlyphKind.Locked, 32) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 16, 0) });
        var heads = new StackPanel();
        heads.Children.Add(UiKit.Heading(WinText.U("Password.Locked", r.VolumeName), 20));
        heads.Children.Add(UiKit.Margin(UiKit.Text(WinText.U("Password.Prompt"), role: Role.InkSoft), 0, 4, 0, 0));
        Grid.SetColumn(heads, 1);
        top.Children.Add(heads);
        body.Children.Add(top);
        if (!string.IsNullOrEmpty(r.Hint)) body.Children.Add(UiKit.Margin(UiKit.Text(WinText.U("Password.Hint", r.Hint), 12, Role.InkSoft), 0, 16, 0, 0));

        // Parola alanı + göster/gizle (gizli alan ↔ açık metin alanı; ad ve araç ipucu Göster ↔ Gizle; odak alanın sonuna döner)
        var field = new Grid { Margin = new Thickness(0, string.IsNullOrEmpty(r.Hint) ? 16 : 6, 0, 0) };
        field.ColumnDefinitions.Add(new ColumnDefinition());
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetName(_hidden, WinText.U("Password.Prompt"));
        AutomationProperties.SetName(_shown, WinText.U("Password.Prompt"));
        field.Children.Add(_hidden);
        field.Children.Add(_shown);
        var eye = new Button { Content = UiKit.Icon(EyeGlyph), Width = 40, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Stretch };
        UiKit.Quiet(eye);
        eye.Padding = new Thickness(0);
        void Label(string text) { AutomationProperties.SetName(eye, text); eye.ToolTip = text; }
        Label(WinText.U("Password.Show"));
        eye.Click += (_, _) =>
        {
            bool show = _shown.Visibility != Visibility.Visible;
            if (show) _shown.Text = _hidden.Password; else _hidden.Password = _shown.Text;
            _shown.Visibility = UiKit.Vis(show);
            _hidden.Visibility = UiKit.Vis(!show);
            eye.Content = UiKit.Icon(show ? EyeOffGlyph : EyeGlyph);
            Label(show ? WinText.U("Password.Hide") : WinText.U("Password.Show"));
            Field.Focus();
            if (show) _shown.CaretIndex = _shown.Text.Length;
        };
        Grid.SetColumn(eye, 1);
        field.Children.Add(eye);
        body.Children.Add(field);

        if (r.Error is not null)
        {
            body.Children.Add(UiKit.Margin(UiKit.Text(r.Error, role: Role.Alert), 0, 8, 0, 0));
            // Ekran okuyucu hatayı alana gelince okusun (görünen satır alanın altında; 1.9.0)
            AutomationProperties.SetHelpText(_hidden, r.Error);
            AutomationProperties.SetHelpText(_shown, r.Error);
        }
        if (r.Remember is { } on)
        {
            _remember = new CheckBox
            {
                Content = new TextBlock { Text = WinText.U("Password.Remember"), TextWrapping = TextWrapping.Wrap }, IsChecked = on,
                Margin = new Thickness(0, 12, 0, 0),
            };
            body.Children.Add(_remember);
            // Not satırı kutunun metniyle aynı hizada
            body.Children.Add(UiKit.Margin(UiKit.Text(r.Hello ? WinText.U("Password.RememberHello") : WinText.U("Password.RememberNoHello"), 12, Role.InkSoft), 28, 0, 0, 0));
        }
        body.Children.Add(UiKit.Margin(UiKit.Text(WinText.U("Password.Reassurance"), 12, Role.InkSoft), 0, 12, 0, 0));

        var cancel = Button(WinText.U("Action.CancelDialog"), ButtonKind.Secondary, Close);
        var unlock = Button(WinText.U("Action.Unlock"), ButtonKind.Accent, Submit);
        Layout(body, cancel, unlock);
    }

    private const string EyeGlyph = "", EyeOffGlyph = "";

    /// <summary>Görünen parola alanı.</summary>
    private Control Field => _shown.Visibility == Visibility.Visible ? _shown : _hidden;

    private void Submit()
    {
        string password = _shown.Visibility == Visibility.Visible ? _shown.Text : _hidden.Password;
        _answer = new PasswordAnswer(password, _remember is null ? null : _remember.IsChecked == true);
        Close();
    }

    protected override void OnEnter() => Submit();

    /// <summary>İptal (Esc, kapatma) → null; "Kilidi aç" → parola ve kutunun son hâli.</summary>
    public static async Task<PasswordAnswer?> AskAsync(PasswordRequest r)
    {
        var w = new PasswordWindow(r);
        await w.ShowAndWaitAsync(w._hidden);
        return w._answer;
    }
}

/// <summary>
/// Windows Hello onayının SAHİBİ olan küçük, görünür pencere (Ek A H14; parolaPlan 2.7 f): gizli pencereye bağlanan Hello isteği
/// ekrana gelmiyordu. Önde, ortada, en üstte, görev çubuğunda; başlık çubuğu ve kapatma düğmesi YOK (vazgeçmek Hello'nun kendi
/// düğmesiyle); kalıcı DEĞİL (Hello beklerken ana pencere ve panel kullanılabilir). Hello sonuçlanınca çağıran kapatır.
/// </summary>
internal sealed class HelloOwnerWindow : DialogWindow
{
    private HelloOwnerWindow(string message) : base(WinText.U("Password.Title", Branding.ProductName), owner: null, modal: false, titleBar: false)
    {
        ContentWidth = 432;
        var body = new StackPanel { Margin = new Thickness(24, 16, 24, 24) };
        body.Children.Add(UiKit.Text(Title, 12, Role.InkSoft, wrap: false)); // başlık çubuğu yok: pencerenin adı içeride
        var row = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new GateGlyph(GlyphKind.Locked, 32) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
        var text = UiKit.Text(message);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        body.Children.Add(row);
        Root.Children.Add(body);
    }

    public IntPtr Handle => OpenWindows.Handle(this);

    /// <summary>Gösterir (etkin, önde); çağıran Hello bitince <see cref="Window.Close"/> der.</summary>
    public static async Task<HelloOwnerWindow> ShowAsync(string message)
    {
        var w = new HelloOwnerWindow(message);
        await w.PresentAsync();
        return w;
    }

    protected override void Cancel() { } // Esc ile kapanmaz: Hello'nun kendi Vazgeç'i
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using Apfs.Windows.Localization;

namespace Apfs.Tray.Ui;

/// <summary>
/// Korece arayüzde satır yalnız sözcük arasından kırılır (1.9.0; <see cref="KoreanText"/>): tepsinin çizdiği her düz metin bloğu
/// ilk yerleşiminde ve metni değişince hecelerin arasına sözcük birleştirici alır. Yalnız arayüz dili Korece iken kurulur. Dokunulmayan:
/// şablonun ürettiği ya da bağlı metin (açılır liste, araç ipucu: kaynağı bağlamadan gelir) ve bağlantı içeren metin (metni yeniden
/// yazmak bağlantıyı silerdi). Kayıt yüklüyken tutulur, kaldırılınca bırakılır (liste yeniden kurulurken birikmesin).
/// </summary>
internal static class KoreanLines
{
    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    /// <summary>Takip edilen metin blokları (metin değişince yeniden uygulanır); kaldırılınca bırakılır.</summary>
    private static readonly HashSet<TextBlock> Watched = [];

    /// <summary>Yüklenme/kaldırılma işleyicisi eklenmiş bloklar (bir kez; zayıf: blok atılınca kayıt da gider).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, object> Hooked = new();

    /// <summary>
    /// Tema kurulduktan SONRA çağrılır (önce çağrılınca <c>TextBlock</c> türü temadan önce başlıyor, açılır liste ve seçenek metinleri
    /// küçük çiziliyordu — ölçüldü). <c>Loaded</c> WPF'te yalnız örnek işleyicisi olan öğeye gelir; ilk yerleşimde her öğeye gelen
    /// <c>SizeChanged</c> sınıf işleyicisiyle yakalanır, sonra örnek işleyicileri eklenir.
    /// </summary>
    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnSizeChanged));

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not TextBlock t || Watched.Contains(t) || !Eligible(t)) return;
        Watch(t);
        if (!Hooked.TryAdd(t, Hooked)) return; // kaldırılıp yeniden eklenen blok: işleyiciler zaten bağlı (birikmesin)
        t.Loaded += (_, _) => Watch(t);
        t.Unloaded += (_, _) =>
        {
            TextDescriptor.RemoveValueChanged(t, OnTextChanged);
            Watched.Remove(t);
        };
    }

    private static void Watch(TextBlock t)
    {
        if (!Eligible(t) || !Watched.Add(t)) return;
        TextDescriptor.AddValueChanged(t, OnTextChanged);
        Apply(t);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock t && Eligible(t)) Apply(t);
    }

    private static bool Eligible(TextBlock t) =>
        t.TemplatedParent is null && !BindingOperations.IsDataBound(t, TextBlock.TextProperty)
        && (t.Inlines.Count == 0 || t.Inlines.Count == 1 && t.Inlines.FirstInline is Run);

    private static void Apply(TextBlock t)
    {
        string text = t.Text;
        if (string.IsNullOrEmpty(text)) return;
        string kept = KoreanText.KeepWords(text);
        if (!ReferenceEquals(kept, text)) t.Text = kept; // yeniden çağrıda değişmez: döngü yok
    }
}

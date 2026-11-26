using Apfs.Volume;

namespace Apfs.Mount;

/// <summary>Windows yolunun birimde çözümü (iki sürücü de kullanır; her bileşen klasörün <see cref="WindowsNameMap"/>'iyle).</summary>
internal static class WindowsPath
{
    public static string[] Split(string fileName) => fileName.Split('\\', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Yolu bileşen bileşen çözer (APFS adında "\" geçebileceği için yol bir bütün olarak bölünmez). <paramref name="shown"/>:
    /// bileşenlerin Windows'ta gösterilen adları; <paramref name="name"/>: son bileşenin Mac adı (kökte boş);
    /// <paramref name="canonical"/>: BÜTÜN bileşenler bugünkü yolla bulundu — false ise en az biri haritadan (tuhaf ad ya da tuhaf
    /// adlı klasörün altı; yazılabilir sürücüde dokunulmaz: harita yeniden kurulunca "~2" başka bir girdiye kayabilir, yazma yanlış
    /// yere giderdi — 1.9.1). Bulunamazsa ya da aradaki bileşen klasör değilse NotFound.
    /// </summary>
    public static Inode Resolve(ApfsVolume volume, IReadOnlyList<string> parts, out List<string> shown, out string name, out bool canonical)
    {
        var inode = volume.Root;
        shown = new List<string>(parts.Count);
        name = "";
        canonical = true;
        foreach (string part in parts)
        {
            if (!inode.IsDirectory) throw new ApfsException(ApfsErrorCode.NotFound, string.Join('\\', parts));
            var dir = volume.GetDirectory(inode.Id);
            var entry = WindowsNameMap.Find(dir, part, out bool found) ?? throw new ApfsException(ApfsErrorCode.NotFound, string.Join('\\', parts));
            canonical &= found;
            shown.Add(Shown(dir, entry, part));
            name = entry.Name;
            inode = volume.GetInode(entry.FileId);
        }
        return inode;
    }

    /// <summary>
    /// Bulunan girdinin WinFsp'ye "gerçek ad" olarak bildirilecek gösterilen adı. Sonu nokta/boşluk olan Mac adı SFM eşlemesiyle
    /// (U+F029/U+F028) gösterilir; ama <c>\\?\</c> yolu adı düz biçimiyle de isteyebilir ("son nokta."). WinFsp çekirdekteki adı
    /// gerçek adla yerinde değiştirir ve ikisi yalnız harf büyüklüğünde ayrılabilir: düz istenen adın sonuna eşlenmiş ad
    /// bildirilince açılış "ad geçersiz" (0x7B) alıyordu (oluşturulan ad aynı adla açılamıyor, taşınamıyordu). Sondaki eşlenmiş
    /// nokta/boşluklar istenen biçimde bırakılır.
    /// </summary>
    public static string Shown(DirectoryIndex dir, DirEntry entry, string requested)
    {
        string shown = WindowsNameMap.Display(dir, entry);
        if (shown.Length != requested.Length) return shown;
        char[]? c = null;
        for (int i = shown.Length - 1; i >= 0 && shown[i] is '' or ''; i--)
            if (requested[i] == (shown[i] == '' ? ' ' : '.')) (c ??= shown.ToCharArray())[i] = requested[i];
        return c is null ? shown : new string(c);
    }

    /// <summary>
    /// Klasörde ek alarak gösterilen ad varsa günlüğe BİR kez (sürücü başına; <paramref name="logged"/> bayrağı) yazılacak ileti,
    /// yoksa null.
    /// </summary>
    public static string? SuffixedNotice(DirectoryIndex dir, ref int logged)
    {
        if (Volatile.Read(ref logged) != 0) return null;
        var map = WindowsNameMap.Of(dir);
        if (map.SuffixedCount == 0 || Interlocked.Exchange(ref logged, 1) != 0) return null;
        return CoreText.T("Fs.NamesSuffixed", map.SuffixedCount, map.FirstSuffixed!);
    }
}

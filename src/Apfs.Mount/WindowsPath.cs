using Apfs.Volume;

namespace Apfs.Mount;

/// <summary>Windows yolunun birimde çözümü (iki sürücü de kullanır; her bileşen klasörün <see cref="WindowsNameMap"/>'iyle).</summary>
internal static class WindowsPath
{
    public static string[] Split(string fileName) => fileName.Split('\\', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Yolu bileşen bileşen çözer (APFS adında "\" geçebileceği için yol bir bütün olarak bölünmez). <paramref name="shown"/>:
    /// bileşenlerin Windows'ta gösterilen adları; <paramref name="name"/>: son bileşenin Mac adı (kökte boş);
    /// <paramref name="canonical"/>: son bileşen bugünkü yolla bulundu — false ise haritadan (tuhaf ad; yazılabilir sürücüde
    /// dokunulmaz). Bulunamazsa ya da aradaki bileşen klasör değilse NotFound.
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
            var entry = WindowsNameMap.Find(dir, part, out canonical) ?? throw new ApfsException(ApfsErrorCode.NotFound, string.Join('\\', parts));
            shown.Add(WindowsNameMap.Display(dir, entry));
            name = entry.Name;
            inode = volume.GetInode(entry.FileId);
        }
        return inode;
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

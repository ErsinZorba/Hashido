using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Apfs.Windows;

/// <summary>Hatırlanan bir birim (parolasız: liste yalnız ad ve UUID taşır).</summary>
public sealed record RememberedVolume(string Uuid, string Name);

/// <summary>
/// "Bu bilgisayarda hatırla" (parolaPlan 1.3): şifreli birimin parolası Windows Kimlik Bilgileri Yöneticisi'nde, birim UUID'sine
/// bağlı genel (generic) kayıt olarak durur. Kasa Windows'undur (kullanıcı başına, DPAPI ile şifreli, <c>CRED_PERSIST_LOCAL_MACHINE</c>
/// = dolaşmaz); kullanıcı kayıtları Windows'un kendi Kimlik Bilgileri Yöneticisi'nde de görüp silebilir. Yalnız tepsi kullanır (hizmet
/// SYSTEM'dir, kullanıcının kasasını görmez). Hedef adı SABİT önekli (<see cref="Prefix"/>): ürün adından türetilmez, ad değişse de
/// kayıtlar sahipsiz kalmaz. Parola hiçbir günlüğe yazılmaz; okunan bayt dizisi kullanılınca sıfırlanır.
/// </summary>
public sealed class CredentialStore(string prefix)
{
    /// <summary>Ürünün kayıt öneki (kaldırıcı <c>--forget-passwords</c> ile yalnız bunları siler).</summary>
    public const string Prefix = "Hashido/volume/";

    public static CredentialStore Default { get; } = new(Prefix);

    private readonly string _prefix = prefix;

    /// <summary>Kaydeder (varsa üzerine yazar). UUID geçerli bir GUID değilse <see cref="ArgumentException"/>.</summary>
    public void Save(string uuid, string volumeName, string password)
    {
        string target = Target(uuid);
        byte[] blob = Encoding.Unicode.GetBytes(password);
        IntPtr mem = Marshal.AllocHGlobal(Math.Max(blob.Length, 1));
        try
        {
            Marshal.Copy(blob, 0, mem, blob.Length);
            var c = new Credential
            {
                Type = CredTypeGeneric, TargetName = target, CredentialBlobSize = (uint)blob.Length, CredentialBlob = mem,
                Persist = CredPersistLocalMachine, UserName = volumeName,
            };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, mem, blob.Length);
            Marshal.FreeHGlobal(mem);
            Array.Clear(blob);
        }
    }

    /// <summary>Kayıtlı parola; kayıt yoksa (ya da okunamıyorsa) null.</summary>
    public string? TryRead(string uuid)
    {
        if (!CredRead(Target(uuid), CredTypeGeneric, 0, out IntPtr p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<Credential>(p);
            if (c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize == 0) return null;
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                // UTF-16LE (bizim yazdığımız ve cmdkey /pass'in yazdığı biçim); olası sonlandırıcı sıfır atılır
                return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(p); }
    }

    /// <summary>Kayıttaki birim adı (Ayarlar listesinde görünen); kayıt yoksa null. Parola blob'u kopyalanmaz.</summary>
    public string? NameOf(string uuid)
    {
        if (!CredRead(Target(uuid), CredTypeGeneric, 0, out IntPtr p)) return null;
        try { return Marshal.PtrToStructure<Credential>(p).UserName ?? ""; }
        finally { CredFree(p); }
    }

    /// <summary>Kaydı siler; silindiyse true (yoksa false).</summary>
    public bool Delete(string uuid) => CredDelete(Target(uuid), CredTypeGeneric, 0);

    /// <summary>Bu önekli bütün kayıtlar (parola okunmaz).</summary>
    public List<RememberedVolume> List()
    {
        var list = new List<RememberedVolume>();
        if (!CredEnumerate(_prefix + "*", 0, out uint count, out IntPtr arr)) return list; // ERROR_NOT_FOUND: kayıt yok
        try
        {
            for (int i = 0; i < count; i++)
            {
                var c = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(arr, i * IntPtr.Size));
                // Joker eşleşmesi harf büyüklüğüne duyarsız; yalnız tam önekli, geçerli UUID'li kayıtlar bizimdir
                if (c.Type != CredTypeGeneric || c.TargetName is not { } t || !t.StartsWith(_prefix, StringComparison.Ordinal)) continue;
                if (!Guid.TryParse(t.AsSpan(_prefix.Length), out var g)) continue;
                list.Add(new RememberedVolume(g.ToString(), c.UserName ?? ""));
            }
        }
        finally { CredFree(arr); }
        return list;
    }

    /// <summary>Bu önekli bütün kayıtları siler (kaldırma); silinen sayısı.</summary>
    public int DeleteAll() => List().Count(v => Delete(v.Uuid));

    private string Target(string uuid) =>
        Guid.TryParse(uuid, out var g) ? _prefix + g.ToString() : throw new ArgumentException("not a volume UUID", nameof(uuid));

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

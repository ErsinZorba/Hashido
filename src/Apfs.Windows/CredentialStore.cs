using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Apfs.Windows;

/// <summary>Kasadan okumanın sonucu (1.9.1): kayıt var, kayıt yok (Windows: 1168 ERROR_NOT_FOUND) ya da okunamadı (başka hata).</summary>
public enum CredentialRead { Found, Missing, Failed }

/// <summary>Hatırlanan bir birim (parolasız: liste yalnız ad ve UUID taşır).</summary>
public sealed record RememberedVolume(string Uuid, string Name);

/// <summary>
/// "Bu bilgisayarda hatırla" (parolaPlan 1.3): şifreli birimin parolası Windows Kimlik Bilgileri Yöneticisi'nde, birim UUID'sine
/// bağlı genel (generic) kayıt olarak durur. Kasa Windows'undur (kullanıcı başına, DPAPI ile şifreli, <c>CRED_PERSIST_LOCAL_MACHINE</c>
/// = dolaşmaz); kullanıcı kayıtları Windows'un kendi Kimlik Bilgileri Yöneticisi'nde de görüp silebilir. Yalnız tepsi kullanır (hizmet
/// SYSTEM'dir, kullanıcının kasasını görmez). Hedef adı SABİT önekli (<see cref="Prefix"/>): ürün adından türetilmez, ad değişse de
/// kayıtlar sahipsiz kalmaz. Parola hiçbir günlüğe yazılmaz; okunan bayt dizisi kullanılınca sıfırlanır.
/// <para>Bütün kasa çağrıları süreç içinde TEK kilitle sıradadır (<see cref="Gate"/>): Windows'un kasası aynı anda gelen
/// yazma/silmelerde güncelleme kaybediyor — 8 iş parçacığı × 200 yaz-oku-sil turunda "başarılı" silinen 97 kayıt kasada kaldı,
/// iki süreçte başarıyla yazılmış kayıt hemen ardından okunamadı (1168); kilitle 16 × 300 turda 0 (tamDuzeltmePlan P0, 2026-11-23).
/// Süreçler arası yarışı bu kilit önleyemez.</para>
/// </summary>
public sealed class CredentialStore(string prefix)
{
    /// <summary>Ürünün kayıt öneki (kaldırıcı <c>--forget-passwords</c> ile yalnız bunları siler).</summary>
    public const string Prefix = "Hashido/volume/";

    public static CredentialStore Default { get; } = new(Prefix);

    private readonly string _prefix = prefix;

    /// <summary>Bütün örneklerin bütün kasa çağrıları bu kilitle sıradadır (önekten bağımsız: kasa tektir).</summary>
    private static readonly object Gate = new();

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
            bool ok;
            int error;
            lock (Gate)
            {
                ok = CredWrite(ref c, 0);
                error = Marshal.GetLastWin32Error();
            }
            if (!ok) throw new Win32Exception(error);
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, mem, blob.Length);
            Marshal.FreeHGlobal(mem);
            Array.Clear(blob);
        }
    }

    /// <summary>Kayıtlı parola; kayıt yoksa (ya da okunamıyorsa) null. Ayrım gerekiyorsa <see cref="Read"/>.</summary>
    public string? TryRead(string uuid) => Read(uuid, out var password) == CredentialRead.Found ? password : null;

    /// <summary>
    /// Kayıtlı parolayı okur ve "kayıt yok"u okuma hatasından ayırır (1.9.1): tepsi hata hâlinde kaydı var sayar (kutu işaretli),
    /// yoksa elle açışta işaretsiz kutu var olan kaydı siliyordu. Hata kodu <paramref name="error"/>'da (günlük için).
    /// </summary>
    public CredentialRead Read(string uuid, out string? password) => Read(uuid, out password, out _);

    /// <inheritdoc cref="Read(string, out string?)"/>
    public CredentialRead Read(string uuid, out string? password, out int error)
    {
        string target = Target(uuid);
        password = null;
        if (TestReadError is { } fault)
        {
            error = fault;
            return fault == ErrorNotFound ? CredentialRead.Missing : CredentialRead.Failed;
        }
        lock (Gate) return ReadLocked(target, out password, out error);
    }

    /// <summary>Test kancası: verilirse okuma bu Windows hata koduyla düşer (1168 = kayıt yok). Yalnız bu örnekte.</summary>
    internal int? TestReadError { get; set; }

    private const int ErrorNotFound = 1168;

    private static CredentialRead ReadLocked(string target, out string? password, out int error)
    {
        password = null;
        error = 0;
        if (!CredRead(target, CredTypeGeneric, 0, out IntPtr p))
        {
            error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound ? CredentialRead.Missing : CredentialRead.Failed;
        }
        password = ReadBlob(p);
        return password is null ? CredentialRead.Missing : CredentialRead.Found;
    }

    private static string? ReadBlob(IntPtr p)
    {
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
        string target = Target(uuid);
        lock (Gate)
        {
            if (!CredRead(target, CredTypeGeneric, 0, out IntPtr p)) return null;
            try { return Marshal.PtrToStructure<Credential>(p).UserName ?? ""; }
            finally { CredFree(p); }
        }
    }

    /// <summary>Kaydı siler; silindiyse true (yoksa false).</summary>
    public bool Delete(string uuid)
    {
        string target = Target(uuid);
        lock (Gate) return CredDelete(target, CredTypeGeneric, 0);
    }

    /// <summary>Bu önekli bütün kayıtlar (parola okunmaz).</summary>
    public List<RememberedVolume> List()
    {
        var list = new List<RememberedVolume>();
        lock (Gate) ListLocked(list);
        return list;
    }

    private void ListLocked(List<RememberedVolume> list)
    {
        if (!CredEnumerate(_prefix + "*", 0, out uint count, out IntPtr arr)) return; // ERROR_NOT_FOUND: kayıt yok
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

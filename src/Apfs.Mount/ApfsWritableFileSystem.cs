using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Apfs.Util;
using Apfs.Volume;
using Apfs.Write;
using Fsp;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace Apfs.Mount;

/// <summary>Yazma oturumu sayaçları (ham; rapora yalnız aralık olarak geçer).</summary>
public readonly record struct WriteStats(long Commits, long Bytes, long Operations);

/// <summary>
/// APFS birimini WinFsp üzerinden YAZILABİLİR sürücü olarak sunar (Faz 3). Salt okunur sınıf (<see cref="ApfsFileSystem"/>)
/// değişmeden kalır; bu sınıf yalnızca yazma kapısından geçmiş bir oturumla kurulur.
/// <list type="bullet">
/// <item>Okumalar oturumun güncel görüntüsünden (<see cref="VolumeWriter.View"/>), değişiklikler <see cref="VolumeWriter"/>'dan.</item>
/// <item>Bütün çağrılar OTURUMUN kilidi (<see cref="WriteSession.SyncRoot"/>) altında: yazıcı iş parçacığı güvenli değildir ve
/// aynı oturumu (aynı kapsayıcının) başka birimlerin sürücüleri de kullanabilir.</item>
/// <item>Değişiklikler ~5 sn'de bir, Flush'ta ve ayırmada commit edilir. Windows önbelleği kapalıdır (bkz. Init): her yazma
/// doğrudan buraya gelir, dosya kapandıktan sonra geç yazma olmaz.</item>
/// </list>
/// </summary>
public sealed class ApfsWritableFileSystem : FileSystemBase, IDisposable
{
    internal const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    internal const int StatusDirectoryNotEmpty = unchecked((int)0xC0000101);
    internal const int StatusDiskFull = unchecked((int)0xC000007F);
    internal const int StatusInvalidParameter = unchecked((int)0xC000000D);
    internal const int StatusNameTooLong = unchecked((int)0xC0000106);
    internal const int StatusObjectNameInvalid = unchecked((int)0xC0000033);
    internal const int StatusFileIsADirectory = unchecked((int)0xC00000BA);

    private const uint FileDirectoryFile = 0x1;
    private const uint FileWriteData = 0x2, FileAppendData = 0x4; // erişim hakları (grantedAccess)
    private const uint FileAttributeHidden = 0x2, FileAttributeDirectory = 0x10, FileAttributeNormal = 0x80;


    /// <summary>
    /// Yazılabilir birim: tam erişim yalnız SYSTEM, Yöneticiler ve oturum açmış (etkileşimli) kullanıcılara; sahibi Yöneticiler.
    /// Everyone (WD) değil: ağ oturumları ve hizmet hesapları yazamaz (açık depo bildirimi #2, 1.8.0; kanalın modeliyle aynı,
    /// <c>Protocol.cs</c>). Salt okunur sürücünün (<see cref="ApfsFileSystem"/>) okuma ACL'si değişmedi.
    /// </summary>
    internal const string SecurityDescriptorSddl = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;IU)";

    private readonly WriteSession _session;
    private readonly VolumeWriter _vol;
    private readonly ApfsVolume _view;
    private readonly Lock _gate; // = oturumun kilidi (aynı kapsayıcının bütün sürücüleri paylaşır)
    private readonly byte[] _securityDescriptor;
    private readonly Timer _commitTimer;
    private readonly uint _bs;
    private int _openFiles;
    private long _commits, _bytesWritten, _operations; // _gate altında; raporlama için (yazma oturumu sayaçları)
    private long _lastIoTicks = long.MinValue / 2;
    private bool _disposed;

    public event Action<string>? Error;

    /// <summary>
    /// Yalnız günlüğe giden bilgi (bildirim değil): bir klasörde Windows'ta aynı ada düşen Mac adları ek alarak gösteriliyor (
    /// sürücü başına bir kez).
    /// </summary>
    public event Action<string>? Log;

    private int _namesLogged; // ek alan adlar günlüğe bir kez

    /// <summary>
    /// Kullanıcının başka yerden göremeyeceği değişiklik kaybı: bekleyen değişiklikler diske yazılamadı (commit zamanlayıcısı/
    /// Flush) ya da kapatılırken silinmek istenen dosya silinemedi. <see cref="Error"/>'dan ayrı: hizmet bunları seyreltmeden
    /// bildirir (1.6.4).
    /// </summary>
    public event Action<string>? ChangeLost;

    /// <summary>
    /// Oturumun bir commit'i yarıda kaldı ya da değiştiren bir işlem değişiklik evresinde düştü (1.8.1; <see cref="WriteSession.IsFaulted"/>):
    /// oturum bir daha yazmaz, tek doğru yol kaydetmeden bırakmak. Oturum başına BİR kez gelir (arızayı yaşayan sürücüden; çok birimde
    /// öbür sürücüler sessizce hata döner). Bir
    /// dinleyici varsa bu hata <see cref="ChangeLost"/>'a GİTMEZ (çift uyarı olmasın); yoksa eskisi gibi ChangeLost. İleti hatanın
    /// kendisidir ("Değişiklikler diske yazılamadı" öneki yok).
    /// </summary>
    public event Action<string>? Faulted;

    public ApfsWritableFileSystem(WriteSession session, int volumeIndex, TimeSpan? commitInterval = null)
    {
        _session = session;
        _gate = session.SyncRoot;
        _vol = session.Volume(volumeIndex);
        _view = _vol.View(session.Container);
        _bs = session.Container.BlockSize;
        var sd = new RawSecurityDescriptor(SecurityDescriptorSddl);
        _securityDescriptor = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_securityDescriptor, 0);
        var interval = commitInterval ?? TimeSpan.FromSeconds(5);
        // Zamanlayıcı kurulduğu anın yürütme bağlamını (AsyncLocal'ları) taşımaz: hizmette yazmaya açan isteğin dili ve sayı biçimi
        // oturum boyunca zamanlayıcının ürettiği iletilere ("kaydedilemedi") sızmasın; arka plan işi varsayılanı kullanır
        if (ExecutionContext.IsFlowSuppressed()) _commitTimer = new Timer(_ => CommitIfDirty(), null, interval, interval);
        else
            using (ExecutionContext.SuppressFlow())
                _commitTimer = new Timer(_ => CommitIfDirty(), null, interval, interval);
    }

    public int OpenFileCount => Volatile.Read(ref _openFiles);

    /// <summary>
    /// Kullanımda mı: açık dosya ya da son 3 sn'de okuma/yazma (kopyalama sürüyor olabilir). Commit bekleyen değişiklik sayılmaz:
    /// ayırma onları zaten kalıcı yapar.
    /// </summary>
    public bool IsBusy => OpenFileCount > 0 || Environment.TickCount64 - Interlocked.Read(ref _lastIoTicks) < 3000;

    private long _lastOpenTicks = long.MinValue / 2;

    /// <summary>Son okuma/yazmanın ya da dosya açılışının anı; Güvenle çıkar'ın ikinci bakışı için (IsBusy açılışı saymaz).</summary>
    public long LastIoTicks => Math.Max(Interlocked.Read(ref _lastIoTicks), Interlocked.Read(ref _lastOpenTicks));

    private int _refuseOpens, _opening;

    /// <summary>
    /// Ayırma kararı verildi (1.8.2): yeni açılış ve oluşturmalar "aygıt bağlı değil" ile reddedilir (bkz.
    /// <see cref="ApfsFileSystem.RefuseOpens"/>; yazma tam bariyerli). Çıkarma vazgeçilirse geri alınır.
    /// </summary>
    public bool RefuseOpens
    {
        get => Volatile.Read(ref _refuseOpens) != 0;
        set => Interlocked.Exchange(ref _refuseOpens, value ? 1 : 0);
    }

    /// <summary>Şu anda süren açılış/oluşturma sayısı (bkz. <see cref="ApfsFileSystem.OpeningCount"/>).</summary>
    public int OpeningCount => Volatile.Read(ref _opening);

    public bool HasPendingChanges { get { lock (_gate) return _vol.IsDirty; } }

    /// <summary>Oturumun sayaçları: başarılı commit, yazılan bayt, değiştiren işlem (oluştur/yaz/boyut/sil/ad/öznitelik) sayısı.</summary>
    public WriteStats Stats { get { lock (_gate) return new WriteStats(_commits, _bytesWritten, _operations); } }

    /// <summary>
    /// Bekleyen değişiklikleri kalıcı yapar (zamanlayıcı, Flush, ayırma). Döner: başarı, ya da oturum arızalıysa (bu commit ya da
    /// daha önceki bir commit düştüyse) STATUS_DEVICE_NOT_CONNECTED — Flush bunu uygulamaya dürüstçe iletir. Sürücü durdurulduktan
    /// sonra (<see cref="Dispose"/>; terk etmede ayrılmadan önce) Flush ve zamanlayıcı commit etmez: kaydedilmemiş değişiklik varsa
    /// STATUS_DEVICE_NOT_CONNECTED (önceden başarı dönüyordu), yoksa başarı.
    /// </summary>
    public int CommitIfDirty()
    {
        lock (_gate)
        {
            if (_disposed) return _vol.IsDirty ? ApfsFileSystem.StatusDeviceNotConnected : ApfsFileSystem.StatusSuccess;
            if (_session.IsFaulted) return ApfsFileSystem.StatusDeviceNotConnected; // bildirildi (oturum başına bir kez)
            if (!_vol.IsDirty) return ApfsFileSystem.StatusSuccess;
            if (Held) return ApfsFileSystem.StatusMediaWriteProtected; // bekletmede commit yok (zamanlayıcı, Flush; 1.9.1)
            return CommitLocked();
        }
    }

    /// <summary>Oturumu commit eder (<see cref="_gate"/> altında); düşerse arızayı bildirir (oturum bir daha yazmaz).</summary>
    private int CommitLocked()
    {
        try
        {
            _session.Commit();
            _commits++;
            return ApfsFileSystem.StatusSuccess;
        }
        catch (Exception e)
        {
            if (Faulted is { } faulted) faulted(e.Message); // hatanın kendisi (hizmet "son değişikliklerin bir kısmı…" ayrıntısına koyar)
            else ChangeLost?.Invoke(CoreText.T("Fs.WriteBackFailed", e.Message));
            return ApfsFileSystem.StatusDeviceNotConnected;
        }
    }

    /// <summary>
    /// Değiştiren bir işlemi yürütür; yer yetmediği için reddedilirse (denetim evresi: hiçbir şey değişmedi) ve oturumda kaydedilmemiş
    /// değişiklik varsa bir kez commit edip yeniden dener (1.8.2). Bu işlemde bırakılan bloklar (silinen dosyalar, üzerine
    /// yazılan veri) ancak commit'te boşa çıkar ve değişmiş düğümlerin payı sıfırlanır: dolu diskte silerek açılan yer hemen
    /// kullanılır, çok sayıda silme "disk dolu" ile kesilmez. <see cref="_gate"/> altında çağrılır.
    /// </summary>
    private void WithRoom(Action op)
    {
        try
        {
            op();
            return;
        }
        catch (ApfsException e) when (e.Code == ApfsErrorCode.DiskFull)
        {
            // Durdurulmuş sürücü commit etmez (girişteki Stopped denetimi zaten buraya getirmez)
            if (_disposed || _session.IsFaulted || Held || !_session.HasUncommittedChanges) throw;
            // Commit düştüyse oturum arızalı ve CommitLocked bunu bildirdi: çağrı "aygıt bağlı değil" döner, ikinci uyarı yok
            if (CommitLocked() != ApfsFileSystem.StatusSuccess) throw new CommitFailedException();
        }
        op();
    }

    /// <summary><see cref="WithRoom"/>'un commit'i düştü (bildirildi): <see cref="ExceptionHandler"/> yalnız durum kodunu döner.</summary>
    private sealed class CommitFailedException : Exception;

    /// <summary>
    /// Oturum arızalıysa ya da sürücü durdurulduysa (<see cref="Dispose"/>; terk etmede ayrılmadan önce) değiştiren çağrılar
    /// diske hiçbir şey vermeden STATUS_DEVICE_NOT_CONNECTED döner (_gate altında okunur; <see cref="Dispose"/> da _gate altında
    /// işaretler: denetimi geçen çağrı bitene dek sürücü durdurulmuş sayılmaz).
    /// </summary>
    private bool Stopped => _disposed || _session.IsFaulted;

    private int _held;

    /// <summary>
    /// Yazma bekletmesi (1.9.1): uykudan önce ve disk kimliği bir kez okunamayınca hizmet sürücüyü bekletir. Bekletmede
    /// değiştiren çağrılar STATUS_MEDIA_WRITE_PROTECTED döner (diske hiçbir şey vermeden), okuma sürer; commit YOK (zamanlayıcı,
    /// Flush, "disk dolu"daki yeniden deneme) — tek istisna hizmetin kendi uyku kaydı (<see cref="CommitForSleep"/>). Kilit almadan
    /// kurulur (süren uzun bir yazma bekletmeyi geciktirmesin); kilidi geçmiş süren çağrı biter, sonrakiler reddedilir.
    /// </summary>
    public void SetWriteHold(bool hold) => Interlocked.Exchange(ref _held, hold ? 1 : 0);

    /// <summary>Sürücü bekletmede mi (<see cref="SetWriteHold"/>).</summary>
    public bool Held => Volatile.Read(ref _held) != 0;

    /// <summary>
    /// Değiştiren çağrının reddi: durdurulmuş/arızalı → STATUS_DEVICE_NOT_CONNECTED; bekletmede → STATUS_MEDIA_WRITE_PROTECTED;
    /// yoksa başarı (yapılabilir). <see cref="_gate"/> altında okunur.
    /// </summary>
    private int Refused => Stopped ? ApfsFileSystem.StatusDeviceNotConnected : Held ? ApfsFileSystem.StatusMediaWriteProtected : ApfsFileSystem.StatusSuccess;

    /// <summary>
    /// Uyku kaydı (1.9.1): bekletmeden SONRA oturumun tek commit'i; oturum kilidi en çok <paramref name="wait"/> beklenir (süren
    /// uzun bir yazma onu tutuyor olabilir). true = değişiklikler diskte (ya da kaydedilecek değişiklik yoktu); false = kilit
    /// alınamadı, oturum arızalı ya da commit düştü (oturum arızalı olur; bildirimi uyanışta hizmet verir, <see cref="Faulted"/> gelmez).
    /// Oturum başına bir kez çağrılır (commit oturumun bütün birimlerini kaydeder).
    /// </summary>
    /// <param name="mayWrite">Kilit alındıktan sonra yeniden sorulur: false ise yazılmaz (ör. hizmet bu arada oturumu disk kimliği
    /// okunamadığı için bekletti).</param>
    public bool CommitForSleep(TimeSpan wait, Func<bool>? mayWrite = null)
    {
        if (!_gate.TryEnter(wait)) return false;
        try
        {
            if (_disposed || _session.IsFaulted) return false;
            if (mayWrite is not null && !mayWrite()) return false;
            if (!_session.HasUncommittedChanges) return true;
            try
            {
                _session.Commit();
                _commits++;
                return true;
            }
            catch (Exception) { return false; } // oturum arızalı: diskte önceki checkpoint sağlam
        }
        finally { _gate.Exit(); }
    }

    /// <summary>
    /// Değiştiren bir işlem değişiklik evresinde düşüp oturumu arızalı yaptıysa (<paramref name="ex"/> oturumun
    /// <see cref="WriteSession.FaultCause"/>'u; 1.8.1): <see cref="Faulted"/> hatanın kendisiyle. Oturumu arızalı
    /// yapan istisna tek bir çağrıdan çıkar → oturum başına BİR kez (çok birimde öbür sürücüler girişte arızayı görüp sessizce döner);
    /// commit arızası bu yoldan gelmez (<see cref="CommitIfDirty"/> kendisi bildirir). Dinleyici yoksa false: eski yol (Error/ChangeLost).
    /// </summary>
    private bool ReportFault(Exception ex)
    {
        if (!ReferenceEquals(ex, _session.FaultCause) || Faulted is not { } faulted) return false;
        faulted(ex.Message);
        return true;
    }

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        // Windows önbelleği KAPALI (WinFsp yalnız "sonsuz"da açar): yavaş USB bellekte Gezgin'in büyük kopyası önbellek
        // frenlemesinde kilitlendi — istekler hizmete hiç ulaşmadı, dosyayı açmak isteyen her süreç de askıda kaldı (2026-10-05,
        // yavaşlatılmış görüntüyle yeniden üretildi). Önbelleksiz her yazma doğrudan buraya gelir.
        host.FileInfoTimeout = 1_000;
        host.DirInfoTimeout = 1_000;
        host.VolumeInfoTimeout = 1_000;
        host.SecurityTimeout = 60_000;
        host.CaseSensitiveSearch = !_view.IsCaseInsensitive;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = false;
        host.FlushAndPurgeOnCleanup = true;
        host.PassQueryDirectoryPattern = false;
        host.FileSystemName = "APFS";
        host.VolumeCreationTime = (ulong)ApfsFileSystem.ToFileTime(_view.LastModTimeNs);
        host.VolumeSerialNumber = BitConverter.ToUInt32(_view.Uuid.ToByteArray(), 0);
        return ApfsFileSystem.StatusSuccess;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        lock (_gate)
        {
            volumeInfo.TotalSize = _session.Container.Superblock.BlockCount * _bs;
            // Yedek pay ve commit'in ayıracağı üst veri düşülür: Gezgin'in kopyadan önceki "yer var mı" denetimi doğru çıksın
            volumeInfo.FreeSize = _vol.AvailableBytes();
        }
        volumeInfo.SetVolumeLabel(Apfs.Util.TextClamp.Prefix(_view.Name, 32)); // Windows etiket sınırı; vekil çifti bölünmez
        return ApfsFileSystem.StatusSuccess;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Düğümler ve yol çözümü

    private sealed class Node(ulong id)
    {
        public ulong Id { get; } = id;
    }

    private sealed class OpenHandle(bool isDirectory, bool contentReadOnly = false, bool locked = false)
    {
        public bool IsDirectory { get; } = isDirectory;
        /// <summary>
        /// Klon/sıkıştırılmış dosya yazma hakkı açıkça istenmeden (ör. MAXIMUM_ALLOWED) açıldı: açılış geçer, bu tanıtıcıdan
        /// içerik değişikliği (yazma, boyut, üzerine yazma) erişim engellendi alır (NTFS'teki READONLY dosya gibi).
        /// </summary>
        public bool ContentReadOnly { get; } = contentReadOnly;
        /// <summary>
        /// Adı eşlemeden geri dönmeyen (tuhaf; haritadan bulunan) girdi: içeriği de, adı, silinmesi ve öznitelikleri de
        /// değişmez — adla yapılan işlemler eşlemeden geri çevrilmiş adı hedeflerdi.
        /// </summary>
        public bool Locked { get; } = locked;
        public int Released;
        public List<(string Name, FileInfo Info)>? Listing;
        /// <summary>
        /// Bu tanıtıcıda yazma zamanı açıkça ayarlandı: sonraki yazım ve boy değişiklikleri değiştirme zamanına dokunmaz (NTFS gibi;
        /// 1.9.1). _gate altında.
        /// </summary>
        public bool KeepWriteTime;
    }

    /// <summary>Yolun girdisi; <paramref name="name"/> son bileşenin Mac adı, <paramref name="canonical"/> bkz. <see cref="WindowsPath.Resolve"/>.</summary>
    private Inode Resolve(string fileName, out List<string> shown, out string name, out bool canonical) =>
        WindowsPath.Resolve(_view, WindowsPath.Split(fileName), out shown, out name, out canonical);

    /// <summary>
    /// Yolun üst klasörü ve son bileşenin APFS adı (eşlemeden geri çevrilmiş; oluşturma, silme ve ad değiştirme bu adı kullanır).
    /// <c>Odd</c>: son bileşen haritadan bulunan (tuhaf) bir girdiye gidiyor — adla işlem o girdiyi değil eşlemeden geri çevrilmiş
    /// adı hedeflerdi; o ada oluşturma/ad değiştirme bir çakışma doğurur. <c>Shadowed</c>: üst yolun bir bileşeni haritadan (tuhaf
    /// adlı klasörün altı): altında oluşturma, ad değiştirme ve silme "erişim engellendi" — klasörün kendisi gibi.
    /// </summary>
    private (Inode Parent, string Name, bool Odd, bool Shadowed) ResolveParent(string fileName)
    {
        var parts = WindowsPath.Split(fileName);
        if (parts.Length == 0) throw new ApfsException(ApfsErrorCode.InvalidArgument, CoreText.T("Fs.RootDirectory"));
        var parent = WindowsPath.Resolve(_view, parts[..^1], out _, out _, out bool parentCanonical);
        if (!parent.IsDirectory) throw new ApfsException(ApfsErrorCode.NotFound, fileName);
        bool odd = WindowsNameMap.Find(_view.GetDirectory(parent.Id), parts[^1], out bool canonical) is not null && !canonical;
        return (parent, WindowsNames.FromWindows(parts[^1]), odd, !parentCanonical);
    }

    /// <summary>Hedef ad haritadan bulunan bir girdiye gidiyor: oluşturma/ad değiştirme reddedilir ("ad zaten var").</summary>
    private static void RefuseOddTarget(bool odd, string fileName)
    {
        if (odd) throw new ApfsException(ApfsErrorCode.AlreadyExists, fileName);
    }

    private FileInfo InfoOf(Inode ino, string name)
    {
        ulong size = ino.IsDirectory ? 0 : ino.IsSymlink ? (ulong)System.Text.Encoding.UTF8.GetByteCount(_view.ReadSymlinkTarget(ino.Id) ?? "") : _view.GetFileSize(ino);
        uint attr = ino.IsDirectory ? FileAttributeDirectory : 0;
        if (ino.IsHidden || (name.StartsWith('.') && name is not "." and not "..")) attr |= FileAttributeHidden;
        var fi = default(FileInfo);
        fi.FileAttributes = attr == 0 ? FileAttributeNormal : attr;
        fi.FileSize = size;
        fi.AllocationSize = (size + _bs - 1) / _bs * _bs;
        fi.CreationTime = (ulong)ApfsFileSystem.ToFileTime(ino.CreateTimeNs);
        fi.LastAccessTime = (ulong)ApfsFileSystem.ToFileTime(ino.AccessTimeNs);
        fi.LastWriteTime = (ulong)ApfsFileSystem.ToFileTime(ino.ModTimeNs);
        fi.ChangeTime = (ulong)ApfsFileSystem.ToFileTime(ino.ChangeTimeNs);
        fi.IndexNumber = ino.Id;
        fi.HardLinks = ino.IsDirectory ? 0 : (uint)Math.Max(1, ino.ChildrenOrLinks);
        return fi;
    }

    private FileInfo InfoOf(ulong id) => InfoOf(_view.GetInode(id), "");

    private void Touched() => Interlocked.Exchange(ref _lastIoTicks, Environment.TickCount64);

    /// <summary>
    /// FILETIME → APFS zaman damgası; null = değiştirme. 0 "değiştirme", −1 "bu tanıtıcıdaki yazımlar zamanı güncellemesin", −2
    /// "yeniden güncellesin" demektir (FILE_BASIC_INFO): üçü de zamanı DEĞİŞTİRMEZ (önceden −2 2185 yılına dönüyordu), öbür
    /// negatif değerler de geçersizdir. APFS'in aralığı (işaretli 64 bit ns: 1677-09-21 … 2262-04-11) dışındaki tarih en yakın
    /// sınıra kırpılır (önceden taşıp anlamsız bir tarihe dönüyordu); 1970 öncesi negatif yazılır.
    /// </summary>
    internal static long? FromFileTime(ulong ft)
    {
        if (ft == 0 || (long)ft < 0) return null;
        Int128 ns = ((Int128)(long)ft - ApfsFileSystem.UnixEpochFileTime) * 100;
        return (long)Int128.Clamp(ns, long.MinValue, long.MaxValue);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Açma, okuma, listeleme

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        lock (_gate)
        {
            var ino = Resolve(fileName, out _, out string name, out _);
            fileAttributes = InfoOf(ino, name).FileAttributes;
        }
        if (securityDescriptor is not null) securityDescriptor = _securityDescriptor;
        return ApfsFileSystem.StatusSuccess;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        Interlocked.Increment(ref _opening); // red denetiminden ÖNCE (Güvenle çıkar süren açılışı bekler)
        try
        {
            return OpenCore(fileName, createOptions, grantedAccess, out fileNode, out fileDesc, out fileInfo, out normalizedName);
        }
        finally { Interlocked.Decrement(ref _opening); }
    }

    private int OpenCore(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _refuseOpens) != 0)
            {
                fileNode = null!;
                fileDesc = null!;
                fileInfo = default;
                normalizedName = null!;
                return ApfsFileSystem.StatusDeviceNotConnected;
            }
            var ino = Resolve(fileName, out var shown, out string name, out bool canonical);
            // Klon / macOS'un sıkıştırdığı dosya ve adı eşlemeden geri dönmeyen (tuhaf) dosya: içeriğe yazma erişimi AÇIKÇA
            // istenmişse açılışta reddedilir (Windows'un READONLY
            // dosyasındaki gibi); yoksa açılış başarılı olur ve ilk yazma uygulamaya "Bad file descriptor" gibi anlaşılmaz bir hatayla
            // düşer. MAXIMUM_ALLOWED gibi yazma istemeyen açılış ise açılır, yalnız o tanıtıcıdan içerik değişikliği reddedilir
            // (1.4.0 P2.7). İstenen erişim okunamazsa eski davranış: yazma hakkı verilmişse ret. Okuma, öznitelik/zaman, ad
            // değiştirme ve silme (DELETE erişimi) etkilenmez.
            bool contentReadOnly = false;
            if (!ino.IsDirectory && (grantedAccess & (FileWriteData | FileAppendData)) != 0 && (!canonical || _vol.IsContentReadOnly(ino.Id)))
            {
                if (!RequestedAccess.TryGet(createOptions, grantedAccess, out uint desired)
                    || (RequestedAccess.MapGeneric(desired) & (FileWriteData | FileAppendData)) != 0)
                {
                    fileNode = null!;
                    fileDesc = null!;
                    fileInfo = default;
                    normalizedName = null!;
                    return ApfsFileSystem.StatusAccessDenied;
                }
                contentReadOnly = true;
            }
            fileNode = new Node(ino.Id);
            // Tuhaf girdinin tanıtıcısından içerik de ad, silme ve öznitelik de değişmez
            fileDesc = new OpenHandle(ino.IsDirectory, contentReadOnly || (!canonical && !ino.IsDirectory), locked: !canonical);
            if (!ino.IsDirectory)
            {
                Interlocked.Increment(ref _openFiles);
                Interlocked.Exchange(ref _lastOpenTicks, Environment.TickCount64); // açılış da etkinlik (yalnız Güvenle çıkar'ın ikinci bakışı)
            }
            fileInfo = InfoOf(ino, name);
            normalizedName = ApfsFileSystem.SameLengthOrNull("\\" + string.Join('\\', shown), fileName);
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor,
        ulong allocationSize, out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        Interlocked.Increment(ref _opening); // red denetiminden ÖNCE
        try
        {
            return CreateCore(fileName, createOptions, fileAttributes, allocationSize, out fileNode, out fileDesc, out fileInfo, out normalizedName);
        }
        finally { Interlocked.Decrement(ref _opening); }
    }

    private int CreateCore(string fileName, uint createOptions, uint fileAttributes, ulong allocationSize,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        lock (_gate)
        {
            int refused = Volatile.Read(ref _refuseOpens) != 0 ? ApfsFileSystem.StatusDeviceNotConnected : Refused;
            if (refused != ApfsFileSystem.StatusSuccess)
            {
                fileNode = null!;
                fileDesc = null!;
                fileInfo = default;
                normalizedName = null!;
                return refused;
            }
            var (parent, name, odd, shadowed) = ResolveParent(fileName);
            if (shadowed)
            {
                fileNode = null!;
                fileDesc = null!;
                fileInfo = default;
                normalizedName = null!;
                return ApfsFileSystem.StatusAccessDenied;
            }
            RefuseOddTarget(odd, fileName);
            bool dir = (createOptions & FileDirectoryFile) != 0;
            if (!dir) WithRoom(() => _vol.EnsureNewFileRoom(allocationSize)); // ön ayırma: sığmazsa dosya hiç oluşturulmaz
            bool hidden = (fileAttributes & FileAttributeHidden) != 0; // gizli bayrağı oluşturmayla tek işlemde (yarım kalmaz)
            ulong id = 0;
            WithRoom(() => id = dir ? _vol.CreateDirectory(parent.Id, name, hidden) : _vol.CreateFile(parent.Id, name, hidden));
            _operations++;
            fileNode = new Node(id);
            fileDesc = new OpenHandle(dir);
            if (!dir) Interlocked.Increment(ref _openFiles);
            fileInfo = InfoOf(id);
            normalizedName = null!;
            Touched();
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, out FileInfo fileInfo)
    {
        if (fileDesc is OpenHandle { ContentReadOnly: true }) { fileInfo = default; return ApfsFileSystem.StatusAccessDenied; }
        lock (_gate)
        {
            if (Refused is var refused and not ApfsFileSystem.StatusSuccess) { fileInfo = default; return refused; }
            ulong id = ((Node)fileNode).Id;
            // macOS'un sıkıştırdığı dosya bu sürümde salt okunur (kullanıcı kararı 2026-10-05: gerçek Mac dosyasında sınanmadı);
            // SetSize bunu RequireWritableFile'da reddeder → "erişim engellendi"
            // İçeriği salt okunursa önce aynı ret (öznitelik de değişmeden)
            if (_vol.IsContentReadOnly(id)) _vol.SetSize(id, 0);
            // Ön ayırma (eski içeriğin blokları sayılır) ve öznitelik içerik kesilmeden ÖNCE: reddedilirse eski içerik kalır
            if (allocationSize > 0) WithRoom(() => _vol.EnsureNewFileRoom(allocationSize, replacing: id));
            bool hidden = (fileAttributes & FileAttributeHidden) != 0;
            if (replaceFileAttributes || hidden) WithRoom(() => _vol.SetHidden(id, hidden));
            WithRoom(() => _vol.SetSize(id, 0));
            _operations++;
            fileInfo = InfoOf(id);
            Touched();
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override void Close(object fileNode, object fileDesc) => Release(fileDesc);

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        Release(fileDesc);
        if ((flags & CleanupDelete) == 0 || fileName is null) return;
        lock (_gate)
        {
            if (Refused != ApfsFileSystem.StatusSuccess) return; // CanDelete zaten reddeder; arada arızalandı, durduruldu ya da bekletildiyse silme bellekte bile yapılmaz
            try
            {
                var (parent, name, odd, shadowed) = ResolveParent(fileName);
                if (odd || shadowed || fileDesc is OpenHandle { Locked: true }) return; // tuhaf girdi ve altı silinmez (CanDelete zaten reddeder)
                WithRoom(() => _vol.Delete(parent.Id, name));
                _operations++;
                Touched();
            }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError)
            {
                if (!ReportFault(e)) ChangeLost?.Invoke(CoreText.T("Fs.DeleteFailed", fileName, e.Message));
            }
            catch (CommitFailedException) { } // WithRoom'un commit'i düştü: bildirildi
        }
    }

    private void Release(object fileDesc)
    {
        if (fileDesc is OpenHandle h && !h.IsDirectory && Interlocked.Exchange(ref h.Released, 1) == 0)
            Interlocked.Decrement(ref _openFiles);
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        lock (_gate) fileInfo = InfoOf(((Node)fileNode).Id);
        return ApfsFileSystem.StatusSuccess;
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _securityDescriptor;
        return ApfsFileSystem.StatusSuccess;
    }

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor) =>
        ApfsFileSystem.StatusSuccess; // APFS'te Windows erişim listesi saklanmaz; kopyalama başarısız olmasın diye sessizce kabul edilir

    public override unsafe int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        lock (_gate)
        {
            Touched();
            var ino = _view.GetInode(((Node)fileNode).Id);
            if (ino.IsDirectory) return ApfsFileSystem.StatusNotSupported;
            var span = new Span<byte>((void*)buffer, (int)Math.Min(length, int.MaxValue));
            if (ino.IsSymlink)
            {
                var target = System.Text.Encoding.UTF8.GetBytes(_view.ReadSymlinkTarget(ino.Id) ?? "");
                if (offset >= (ulong)target.Length) return ApfsFileSystem.StatusEndOfFile;
                int n = (int)Math.Min((ulong)span.Length, (ulong)target.Length - offset);
                target.AsSpan((int)offset, n).CopyTo(span);
                bytesTransferred = (uint)n;
                return ApfsFileSystem.StatusSuccess;
            }
            if (offset >= _view.GetFileSize(ino)) return ApfsFileSystem.StatusEndOfFile;
            bytesTransferred = (uint)_view.OpenFile(ino).Read(offset, span);
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FileInfo fileInfo)
    {
        if (context is not IEnumerator<(string Name, FileInfo Info)> en)
        {
            var h = (OpenHandle)fileDesc;
            // İşaretsiz çağrı baştan taramadır (ilk tarama ya da RestartScan): liste yeniden kurulur — açık tanıtıcıyla yeniden tarayan
            // araç değişiklikleri görsün (1.9.1; önceden tanıtıcı başına bir kez); işaretli çağrı süren taramadır, aynı listeden
            lock (_gate) if (marker is null || h.Listing is null) h.Listing = BuildListing(((Node)fileNode).Id);
            en = ApfsFileSystem.EntriesAfter(h.Listing, marker); // işaret ikili aramayla (liste DirOrder'la sıralı)
            context = en;
        }
        if (en.MoveNext()) { (fileName, fileInfo) = en.Current; return true; }
        fileName = null!;
        fileInfo = default;
        return false;
    }

    private static readonly Comparer<string> DirOrder = Comparer<string>.Create((a, b) =>
    {
        int ra = a == "." ? 0 : a == ".." ? 1 : 2;
        int rb = b == "." ? 0 : b == ".." ? 1 : 2;
        return ra != rb ? ra.CompareTo(rb) : string.CompareOrdinal(a, b);
    });

    private List<(string Name, FileInfo Info)> BuildListing(ulong dirId)
    {
        var dir = _view.GetInode(dirId);
        var list = new List<(string Name, FileInfo Info)>();
        if (dirId != Inode.RootDirId)
        {
            list.Add((".", InfoOf(dir, ".")));
            var parent = _view.TryGetInode(dir.ParentId);
            list.Add(("..", parent is null ? InfoOf(dir, "..") : InfoOf(parent, "..")));
        }
        var index = _view.GetDirectory(dirId);
        bool odd = false;
        foreach (var e in index.Entries)
        {
            try { list.Add((WindowsNameMap.Display(index, e), InfoOf(_view.GetInode(e.FileId), e.Name))); }
            catch (ApfsException ex) when (ex.Code is not ApfsErrorCode.IoError) { Error?.Invoke(CoreText.T("Fs.EntryUnreadable", e.Name, ex.Message)); }
            odd |= WindowsNameMap.IsOdd(e.Name);
        }
        if (odd && WindowsPath.SuffixedNotice(index, ref _namesLogged) is { } notice) Log?.Invoke(notice);
        list.Sort((a, b) => DirOrder.Compare(a.Name, b.Name));
        return list;
    }

    public override int GetDirInfoByName(object parentNode, object fileDesc, string fileName, out string normalizedName, out FileInfo fileInfo)
    {
        lock (_gate)
        {
            var index = _view.GetDirectory(((Node)parentNode).Id);
            var entry = WindowsNameMap.Find(index, fileName, out _);
            if (entry is null) { normalizedName = null!; fileInfo = default; return ApfsFileSystem.StatusObjectNameNotFound; }
            normalizedName = ApfsFileSystem.SameLengthOrNull(WindowsPath.Shown(index, entry, fileName), fileName);
            fileInfo = InfoOf(_view.GetInode(entry.FileId), entry.Name);
            return ApfsFileSystem.StatusSuccess;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Değiştirme

    public override unsafe int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile,
        bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        if (fileDesc is OpenHandle { ContentReadOnly: true }) { bytesTransferred = 0; fileInfo = default; return ApfsFileSystem.StatusAccessDenied; }
        lock (_gate)
        {
            if (Refused is var refused and not ApfsFileSystem.StatusSuccess) { bytesTransferred = 0; fileInfo = default; return refused; }
            ulong id = ((Node)fileNode).Id;
            ulong size = _vol.GetSize(id);
            if (constrainedIo)
            {
                if (offset >= size) { bytesTransferred = 0; fileInfo = InfoOf(id); return ApfsFileSystem.StatusSuccess; }
                length = (uint)Math.Min(length, size - offset);
            }
            if (writeToEndOfFile) offset = size;
            bool keep = fileDesc is OpenHandle { KeepWriteTime: true };
            WithRoom(() => _vol.Write(id, offset, new ReadOnlySpan<byte>((void*)buffer, (int)length), keep));
            _operations++;
            _bytesWritten += length;
            bytesTransferred = length;
            fileInfo = InfoOf(id);
            Touched();
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        if (fileDesc is OpenHandle { ContentReadOnly: true }) { fileInfo = default; return ApfsFileSystem.StatusAccessDenied; }
        lock (_gate)
        {
            if (Refused is var refused and not ApfsFileSystem.StatusSuccess) { fileInfo = default; return refused; }
            ulong id = ((Node)fileNode).Id;
            // Ayırma boyutu yalnızca bir ipucu; dosyadan küçükse dosya kısalır (WinFsp sözleşmesi). Büyükse hiçbir şey ayrılmaz ama
            // o kadar veri şimdi sığmıyorsa "disk dolu" (önceden yer denetlenmeden başarılı dönüyor, kopya sonda kesiliyordu)
            if (!setAllocationSize || newSize < _vol.GetSize(id))
            {
                // Dosya sonunu büyütmek NTFS'teki gibi yer ister (1.9.1): sığmazsa "disk dolu", dosya değişmez; büyüme yine
                // delik olarak yazılır (APFS'te ön ayırma yok) — önceden yer denetlenmeden kabul ediliyor, yazım ortada kesiliyordu
                if (!setAllocationSize && newSize > _vol.GetSize(id)) WithRoom(() => _vol.EnsureAllocationRoom(id, newSize));
                bool keep = fileDesc is OpenHandle { KeepWriteTime: true };
                WithRoom(() => _vol.SetSize(id, newSize, keep));
                _operations++;
            }
            else WithRoom(() => _vol.EnsureAllocationRoom(id, newSize));
            fileInfo = InfoOf(id);
            Touched();
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime,
        ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        if (fileDesc is OpenHandle { Locked: true }) { fileInfo = default; return ApfsFileSystem.StatusAccessDenied; }
        lock (_gate)
        {
            if (Refused is var refused and not ApfsFileSystem.StatusSuccess) { fileInfo = default; return refused; }
            ulong id = ((Node)fileNode).Id;
            if (fileAttributes != uint.MaxValue) WithRoom(() => _vol.SetHidden(id, (fileAttributes & FileAttributeHidden) != 0));
            long? create = FromFileTime(creationTime), write = FromFileTime(lastWriteTime), change = FromFileTime(changeTime), access = FromFileTime(lastAccessTime);
            // Yalnız 0/−1/−2 geldiyse hiçbir zaman değişmez (değişiklik zamanı da)
            if (create is not null || write is not null || change is not null || access is not null)
                WithRoom(() => _vol.SetTimes(id, create, write, change, access));
            // Açıkça verilen yazma zamanı bu tanıtıcının sonraki yazımlarında korunur. WinFsp "-1" (askıya al) değerini 0 olarak
            // iletiyor (ölçüldü): ayırt edilemez, desteklenmez
            if (write is not null && fileDesc is OpenHandle h) h.KeepWriteTime = true;
            _operations++;
            fileInfo = InfoOf(id);
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        if (fileDesc is OpenHandle { Locked: true }) return ApfsFileSystem.StatusAccessDenied; // tuhaf girdi silinmez
        lock (_gate)
        {
            if (Refused is var refused and not ApfsFileSystem.StatusSuccess) return refused;
            var ino = _view.GetInode(((Node)fileNode).Id);
            return ino.IsDirectory && ino.ChildrenOrLinks > 0 ? StatusDirectoryNotEmpty : ApfsFileSystem.StatusSuccess;
        }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        lock (_gate)
        {
            if (Refused is var refused and not ApfsFileSystem.StatusSuccess) return refused;
            // tuhaf girdinin adı değişmez; hedef ad tuhaf bir girdiye gidiyorsa çakışma (üzerine yazma da)
            var (srcParent, srcName, srcOdd, srcShadowed) = ResolveParent(fileName);
            if (srcOdd || srcShadowed || fileDesc is OpenHandle { Locked: true }) return ApfsFileSystem.StatusAccessDenied;
            var (dstParent, dstName, dstOdd, dstShadowed) = ResolveParent(newFileName);
            if (dstShadowed) return ApfsFileSystem.StatusAccessDenied; // tuhaf adlı klasörün altına taşınmaz
            RefuseOddTarget(dstOdd, newFileName);
            WithRoom(() => _vol.Rename(srcParent.Id, srcName, dstParent.Id, dstName, replaceIfExists));
            _operations++;
            Touched();
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        int status = CommitIfDirty();
        lock (_gate) fileInfo = fileNode is Node n ? InfoOf(n.Id) : default;
        return status;
    }

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        return ApfsFileSystem.StatusNotSupported;
    }

    public override int ExceptionHandler(Exception ex)
    {
        if (ex is CommitFailedException || ReportFault(ex)) return ApfsFileSystem.StatusDeviceNotConnected;
        if (ex is ApfsException ae)
        {
            // Sıradan, uygulamanın kendi gösterdiği hatalar bildirim olmaz (ad çok uzun: Gezgin "dosya adı çok uzun" der)
            if (ae.Code is not ApfsErrorCode.NotFound and not ApfsErrorCode.AlreadyExists and not ApfsErrorCode.NameTooLong
                and not ApfsErrorCode.InvalidName)
                Error?.Invoke(ae.Message);
            return ToNtStatus(ae.Code);
        }
        Error?.Invoke(CoreText.T("Fs.UnexpectedError", ex.Message));
        return ApfsFileSystem.StatusUnexpectedIoError;
    }

    internal static int ToNtStatus(ApfsErrorCode code) => code switch
    {
        ApfsErrorCode.AlreadyExists => StatusObjectNameCollision,
        ApfsErrorCode.DirectoryNotEmpty => StatusDirectoryNotEmpty,
        ApfsErrorCode.DiskFull => StatusDiskFull,
        ApfsErrorCode.InvalidArgument => StatusInvalidParameter,
        ApfsErrorCode.NameTooLong => StatusNameTooLong,
        ApfsErrorCode.InvalidName => StatusObjectNameInvalid,
        // Bu sürümde değiştirilemeyen (klon, sıkıştırılmış) dosya: uygulamalar "erişim engellendi" gösterir
        ApfsErrorCode.Unsupported => ApfsFileSystem.StatusAccessDenied,
        _ => ApfsFileSystem.ToNtStatus(code),
    };

    /// <summary>Zamanlayıcıyı durdurur; oturumu KAPATMAZ (bkz. <see cref="ApfsWritableMount"/>).</summary>
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _commitTimer.Dispose();
    }
}

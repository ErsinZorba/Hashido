using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Apfs.Windows;

/// <summary>
/// Hizmetin ProgramData'daki veri klasörünü (<see cref="ServiceLog.PathName"/>'in klasörü: günlük, rapor kuyruğu ve onayı) güvenceye alır (1.8.1).
/// Klasör ProgramData'nın mirasıyla açılıyordu: standart kullanıcı içine dosya/klasör/bağ açabiliyor, SYSTEM'in
/// yazmalarını (rapor ayarı, kuyruk, günlük döndürme) kendi seçtiği yere yönlendirebiliyordu. Hizmet her açılışta, SYSTEM olarak:
/// kökün ve içindeki her öğenin sahibini Yöneticiler yapar, mirası keser, SYSTEM + Yöneticiler tam, Kullanıcılar yalnız okuma;
/// junction/sembolik bağları ve birden çok adı olan (sabit bağlı) dosyaları SİLER (yalnız bağın/adın kendisini; hedefe dokunmaz).
/// </summary>
/// <remarks>
/// Neden setup'taki <c>icacls /T</c> değil (kullanıcı kararı 2026-11-23): <c>/T</c> klasördeki bir junction'dan geçip hedefteki
/// dosyaların izinlerini değiştiriyor ve aynı komut var olan dosyalarda izin listesini boş bırakıyordu.
/// <para>
/// 1.9.1 (bağımsız güvenlik incelemesi): her işlem TANITICIYA GÖRELİDİR — kök yolla bir kez, her şey onun altında
/// <c>NtCreateFile(RootDirectory, OBJ_DONT_REPARSE, FILE_OPEN_REPARSE_POINT)</c> ile açılır, klasör içeriği tanıtıcıdan listelenir
/// (önceden denetlenen klasöre YOLLA iniliyordu: arada bağa çevrilen klasör SYSTEM'i başka bir klasörde izin değiştirmeye ve sabit
/// bağlı dosyaları silmeye götürebilirdi). Silme POSIX anlamlıdır (ad hemen kalkar; klasik "silme bekliyor" işareti bağa önceden
/// DELETE tanıtıcısı tutan kullanıcıca geri alınabiliyordu) ve ardından adın gittiği yeniden denetlenir. Kök ve <c>reports</c>
/// yoksa SON izinleriyle atomik açılır (önceden ProgramData'nın mirasıyla açılıp sonra güvenceye alınıyordu: arada açılan tanıtıcı
/// yazma hakkını korurdu). Var olan kök ya da <c>reports</c> "kirli"yse (sahibi ya da izinleri yönetici olmayan birine yazma
/// veriyor: önceden açılmış tanıtıcılar izinleri geri yazabilir, klasörü taşıyabilir, bağa çevirebilir) yerinde güvenceye
/// alınmaz: kenara taşınır (<c>.old-…</c>), yerine yenisi açılır; kirli klasördeki rapor dosyalarının içeriği alınmaz.
/// </para>
/// </remarks>
public static class DataDirSecurity
{
    private const int MaxDepth = 16;
    private const int MaxItems = 10_000;

    /// <summary>Günlüğün bilinen dosyaları (kökte).</summary>
    private static readonly string[] LogFiles = ["log.txt", "log.txt.1"];

    /// <summary>Rapor kuyruğunun ve onayının bilinen dosyaları (<c>reports</c> altında; <c>ReportRecorder</c>, <c>ReportQueue</c>).</summary>
    private static readonly string[] ReportFiles = ["config.json", "queue.jsonl", "queue.jsonl.tmp", "queue.jsonl.id"];

    /// <summary>Yeni kopyası çıkarılacak bilinen dosyanın en büyük boyu; üstündeki içerik kopyalanmaz (dosya boş başlar).</summary>
    private const long MaxCopyBytes = 64L << 20;

    /// <summary>
    /// Sonuç: güvenceye alınan öğe sayısı, silinen bağ/sabit bağ sayısı, ilk hata (yoksa null) ve iki alanın güveni (1.9.1):
    /// <paramref name="LogTrusted"/> false ise kök ya da günlüğün bilinen dosyalarından biri güvenceye alınamadı (bu çalışmada günlük
    /// başka yere yazılmalı); <paramref name="ReportsTrusted"/> false ise kök, <c>reports</c> ya da onun bilinen dosyalarından biri.
    /// Genel yürüyüşün sınırları (derinlik, öğe sayısı) yalnız süreyi bağlar, güveni etkilemez. <paramref name="MovedAside"/>: kirli
    /// bulunup kenara taşınan klasörler.
    /// </summary>
    public sealed record Result(int Secured, int LinksRemoved, string? FirstError, bool LogTrusted = true, bool ReportsTrusted = true,
        IReadOnlyList<string>? MovedAside = null);

    private static SecurityIdentifier Sid(WellKnownSidType t) => new(t, null);

    /// <summary>TrustedInstaller hizmet SID'i (Windows dosyalarının sahibi).</summary>
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>
    /// Kurulu hizmet (LocalSystem) için: <see cref="ServiceLog.PathName"/>'in klasörü ve altındaki <c>reports</c>. Yalnız SYSTEM'de
    /// çağrılır (yetkiler: sahiplik al, geri yükle, yedekle; sonra eski hâline döner). Hiçbir istisna dışarı çıkmaz.
    /// </summary>
    public static Result SecureServiceDataDirectory()
    {
        string dir = Path.GetDirectoryName(ServiceLog.PathName)!;
        try
        {
            using var privileges = Privileges.Enable("SeTakeOwnershipPrivilege", "SeRestorePrivilege", "SeBackupPrivilege");
            var admins = Sid(WellKnownSidType.BuiltinAdministratorsSid);
            return Secure(dir, admins, [Sid(WellKnownSidType.LocalSystemSid), admins], Sid(WellKnownSidType.BuiltinUsersSid));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            return new Result(0, 0, e.Message, LogTrusted: false, ReportsTrusted: false);
        }
    }

    /// <summary>
    /// Hizmet dışındaki SYSTEM süreci (WinFsp Launcher'ın <c>apfs launcher-mount</c>'u) günlüğe yazmadan önce, HİÇBİR ŞEY
    /// değiştirmeden: kök var, bağ değil ve kirli değil, <c>log.txt</c>/<c>log.txt.1</c> varsa bağ değil, tek adlı, kirli değil. Değilse
    /// çağıran <see cref="ServiceLog.UseFallbackOnly"/> der (1.9.1; önceden güven kararına bakmadan ProgramData'ya yazıyordu).
    /// </summary>
    public static bool IsServiceLogAreaSafe() =>
        IsLogAreaSafe(Path.GetDirectoryName(ServiceLog.PathName)!, []);

    internal static bool IsLogAreaSafe(string dir, IReadOnlyList<SecurityIdentifier> alsoTrusted)
    {
        var trusted = TrustedSet(alsoTrusted);
        try
        {
            using var root = OpenPath(Path.GetFullPath(dir), FileReadAttributes | ReadControl | Synchronize);
            if (root is null) return false; // yok: hizmet henüz güvenceye almadı (ServiceLog ProgramData klasörünü açmaz)
            var info = Info(root);
            if (IsLink(info) || !IsDirectory(info) || IsTainted(root, trusted)) return false;
            foreach (string name in LogFiles)
            {
                using var f = OpenRelative(root, name, FileReadAttributes | ReadControl | Synchronize, 0, out uint status);
                if (f is null)
                {
                    if (IsNotFound(status)) continue;
                    return false;
                }
                var fi = Info(f);
                if (IsLink(fi) || IsDirectory(fi) || fi.Links > 1 || IsTainted(f, trusted)) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException) { return false; }
    }

    /// <summary>
    /// <paramref name="dir"/>'i güvenceye alır. Testler kendi kullanıcısını sahip ve tam yetkili verir (standart kullanıcı sahibi
    /// Yöneticiler yapamaz); güvenilen hesaplar = SYSTEM, Yöneticiler, TrustedInstaller, sahip ve tam yetkililer.
    /// </summary>
    /// <remarks>
    /// Sıra: kök, günlüğün bilinen dosyaları, <c>reports</c> ve onun bilinen dosyaları — bunlar öğe sınırından bağımsızdır; en son
    /// bütün içerik (sınır yalnız süreyi bağlar). Bilinen yol bağ/sabit bağsa silinir; silinemezse o alan güvenilmez. Var olan
    /// bilinen dosya aynı klasörde yeni kopyasıyla değiştirilir (POSIX anlamlı yeniden adlandırma): izinler değişmeden önce yazma
    /// erişimiyle açılmış bir tanıtıcı adı olmayan eski kopyaya yazar; değiştirilemezse o alan güvenilmez.
    /// </remarks>
    internal static Result Secure(string dir, SecurityIdentifier owner, IReadOnlyList<SecurityIdentifier> full, SecurityIdentifier readers)
    {
        var state = new State(owner, full, readers);
        dir = Path.GetFullPath(dir);
        SafeFileHandle root;
        try
        {
            root = SecureRoot(dir, state);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            state.Fail(e.Message);
            return new Result(state.Secured, state.LinksRemoved, state.FirstError, LogTrusted: false, ReportsTrusted: false, state.Aside);
        }
        using (root)
        {
            bool logTrusted = SecureKnownFiles(root, dir, LogFiles, keepTaintedContent: true, state);
            bool reportsTrusted = false;
            SafeFileHandle? reports = null;
            string reportsPath = Path.Combine(dir, "reports");
            try
            {
                reports = SecureSubfolder(root, dir, "reports", state);
                reportsTrusted = SecureKnownFiles(reports, reportsPath, ReportFiles, keepTaintedContent: false, state);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
            {
                state.Fail(e.Message.StartsWith(reportsPath, StringComparison.Ordinal) ? e.Message : $"{reportsPath}: {e.Message}");
            }
            using (reports)
            {
                Walk(root, dir, 1, state, skip: reports is null ? null : "reports");
                if (reports is not null) Walk(reports, reportsPath, 2, state, skip: null);
            }
            return new Result(state.Secured, state.LinksRemoved, state.FirstError, logTrusted, reportsTrusted, state.Aside);
        }
    }

    // ---- Kök ve reports ----

    /// <summary>Kökü güvenceye alır ve tanıtıcısını (listeleme + üst veri) döndürür. Hata istisnayla.</summary>
    private static SafeFileHandle SecureRoot(string dir, State state)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (attempt >= 4) throw new IOException(WinText.T("DataDir.StillLink", dir));
            var h = OpenPath(dir, DirAccess);
            if (h is null)
            {
                if (!CreateDirectoryWithSecurity(dir, state)) continue; // bu arada biri açtı: yeniden bak
                state.Secured++;
                return OpenPath(dir, DirAccess) ?? throw new IOException(WinText.T("DataDir.StillLink", dir));
            }
            var info = Info(h);
            if (IsLink(info) || !IsDirectory(info))
            {
                h.Dispose();
                DeletePath(dir, i => IsLink(i) || !IsDirectory(i));
                state.LinksRemoved++;
                continue;
            }
            if (IsTainted(h, state.Trusted))
            {
                h.Dispose();
                state.Aside.Add(MovePathAside(dir));
                continue;
            }
            SetSecurity(h, isContainer: true, state);
            state.Secured++;
            return h;
        }
    }

    /// <summary>Kökün altındaki klasörü (<c>reports</c>) kökle aynı kurallarla güvenceye alır; tanıtıcısını döndürür.</summary>
    private static SafeFileHandle SecureSubfolder(SafeFileHandle parent, string parentPath, string name, State state)
    {
        string path = Path.Combine(parentPath, name);
        for (int attempt = 0; ; attempt++)
        {
            if (attempt >= 4) throw new IOException(WinText.T("DataDir.StillLink", path));
            var h = OpenRelative(parent, name, DirAccess, FileDirectoryFile, out uint status);
            if (h is null)
            {
                if (IsNotFound(status))
                {
                    var created = CreateRelativeDirectory(parent, name, state, out uint createStatus);
                    if (created is not null)
                    {
                        state.Secured++;
                        return created;
                    }
                    if (createStatus == StatusObjectNameCollision) continue;
                    throw NtError(createStatus, path);
                }
                if (status != StatusNotADirectory) throw NtError(status, path);
                // klasör adında dosya: aşağıdaki silme yolu
                h = OpenRelative(parent, name, MetaAccess, 0, out status) ?? throw NtError(status, path);
            }
            var info = Info(h);
            if (IsLink(info) || !IsDirectory(info))
            {
                h.Dispose();
                DeleteRelative(parent, name, path, i => IsLink(i) || !IsDirectory(i));
                state.LinksRemoved++;
                continue;
            }
            if (IsTainted(h, state.Trusted))
            {
                h.Dispose();
                state.Aside.Add(MoveRelativeAsideOutside(parent, parentPath, name));
                continue;
            }
            SetSecurity(h, isContainer: true, state);
            state.Secured++;
            return h;
        }
    }

    /// <summary>
    /// Klasördeki bilinen dosyalar: bağ ya da sabit bağsa silinir; sıradan dosyaysa yeni kopyasıyla değiştirilir (dosya kirliyse ve
    /// <paramref name="keepTaintedContent"/> false ise boş). Yerinde klasör varsa silinmeye çalışılır (boşsa). true: hepsi güvenilir.
    /// </summary>
    private static bool SecureKnownFiles(SafeFileHandle dir, string dirPath, string[] names, bool keepTaintedContent, State state)
    {
        bool trusted = true;
        foreach (string name in names)
        {
            string path = Path.Combine(dirPath, name);
            try
            {
                FileInfoByHandle info;
                bool tainted;
                using (var h = OpenRelative(dir, name, MetaAccess, 0, out uint status))
                {
                    if (h is null)
                    {
                        if (IsNotFound(status)) continue;
                        throw NtError(status, path);
                    }
                    info = Info(h);
                    tainted = IsTainted(h, state.Trusted);
                }
                if (IsLink(info) || IsDirectory(info) || info.Links > 1)
                {
                    // bağ, sabit bağ ya da bilinen adın yerindeki klasör (boş değilse silinemez → alan güvenilmez)
                    DeleteRelative(dir, name, path, i => IsLink(i) || IsDirectory(i) || i.Links > 1);
                    if (!IsDirectory(info) || IsLink(info)) state.LinksRemoved++;
                    continue;
                }
                byte[] content = tainted && !keepTaintedContent ? [] : ReadRelative(dir, name, path);
                try { ReplaceRelative(dir, name, path, content); }
                catch (Win32Exception e) when (!tainted && e.NativeErrorCode == ErrorSharingViolation)
                {
                    // Kirli olmayan dosyayı yönetici olmayan biri yalnız OKUYABİLİR: silme paylaşımı vermeden okuyan tanıtıcı (her
                    // kullanıcı açabilir) değiştirmeyi engeller ama yönlendirme değildir; dosya yerinde güvenceye alınır (genel yürüyüş).
                    // Yalnız kirliyken açılmış yazma tanıtıcısı tehlikeliydi: kirli dosyada bu yol yok.
                    state.Fail(e.Message);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
            {
                state.Fail(e.Message.StartsWith(path, StringComparison.Ordinal) ? e.Message : $"{path}: {e.Message}");
                trusted = false;
            }
        }
        return trusted;
    }

    /// <summary>Bilinen dosyanın içeriği (tanıtıcıyla; çok büyükse boş). Okunamazsa istisna.</summary>
    private static byte[] ReadRelative(SafeFileHandle dir, string name, string path)
    {
        using var h = OpenRelative(dir, name, FileReadData | FileReadAttributes | Synchronize, FileNonDirectoryFile, out uint status)
            ?? throw NtError(status, path);
        long length = RandomAccess.GetLength(h);
        if (length > MaxCopyBytes) return [];
        var buffer = new byte[length];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = RandomAccess.Read(h, buffer.AsSpan(read), read);
            if (n == 0) break;
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>
    /// <paramref name="content"/>'i aynı klasörde yeni bir dosyaya yazar (izinleri güvenceye alınmış klasörden miras alır) ve POSIX
    /// anlamlı yeniden adlandırmayla bilinen adın yerine koyar: hedefte silme paylaşımı veren açık tanıtıcı varken de atomik (klasik
    /// yeniden adlandırma açık hedefi reddeder); salt okunur öznitelik engel olmaz; silme paylaşımı vermeyen tanıtıcı paylaşım ihlali
    /// verir → istisna (geçici dosya silinir).
    /// </summary>
    private static void ReplaceRelative(SafeFileHandle dir, string name, string path, byte[] content)
    {
        string fresh = name + "." + Guid.NewGuid().ToString("N")[..8] + ".new";
        using var h = CreateRelative(dir, fresh, FileWriteData | DeleteAccess | FileReadAttributes | Synchronize,
            FileNonDirectoryFile | FileSynchronousIoNonAlert, null, out uint status) ?? throw NtError(status, Path.Combine(Path.GetDirectoryName(path)!, fresh));
        try
        {
            if (content.Length > 0) RandomAccess.Write(h, content, 0);
            Rename(h, dir, name, RenameReplaceIfExists | RenamePosixSemantics | RenameIgnoreReadOnly, path);
        }
        catch
        {
            try { PosixDelete(h); } catch (Win32Exception) { }
            throw;
        }
    }

    // ---- Genel yürüyüş ----

    /// <summary>Test kancası: genel yürüyüş bir klasörün içine inmeden hemen önce (klasör bu arada bağa çevrilebilir).</summary>
    internal static Action<string>? BeforeDescend { get; set; }

    private static void Walk(SafeFileHandle dir, string dirPath, int depth, State state, string? skip)
    {
        if (depth > MaxDepth) { state.Fail(WinText.T("DataDir.DepthLimit", dirPath)); return; }
        List<(string Name, FileAttributes Attributes)> entries;
        try { entries = List(dir); }
        catch (Win32Exception e) { state.Fail($"{dirPath}: {e.Message}"); return; }
        foreach (var (name, _) in entries)
        {
            if (name is "." or ".." || string.Equals(name, skip, StringComparison.Ordinal)) continue;
            if (state.Secured + state.LinksRemoved >= MaxItems) { state.Fail(WinText.T("DataDir.ItemLimit", dirPath)); return; }
            string path = Path.Combine(dirPath, name);
            try
            {
                using var h = OpenRelative(dir, name, MetaAccess, 0, out uint status) ?? throw NtError(status, path);
                var info = Info(h);
                bool isDir = IsDirectory(info);
                if (IsLink(info) || (!isDir && info.Links > 1))
                {
                    h.Dispose();
                    DeleteRelative(dir, name, path, i => IsLink(i) || (!IsDirectory(i) && i.Links > 1));
                    state.LinksRemoved++;
                    continue;
                }
                SetSecurity(h, isDir, state);
                state.Secured++;
                if (!isDir) continue;
                // İçine YOLLA değil, AYNI nesneyi yeniden açan tanıtıcıyla inilir: arada bağa çevrilse de izlenmez
                BeforeDescend?.Invoke(path);
                using var list = OpenRelative(h, "", DirAccess, FileDirectoryFile, out status) ?? throw NtError(status, path);
                Walk(list, path, depth + 1, state, skip: null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
            {
                state.Fail(e.Message.StartsWith(path, StringComparison.Ordinal) ? e.Message : $"{path}: {e.Message}");
            }
        }
    }

    /// <summary>Klasör tanıtıcısından içerik listesi (ad + öznitelik; FILE_FULL_DIR_INFO).</summary>
    private static List<(string, FileAttributes)> List(SafeFileHandle dir)
    {
        var result = new List<(string, FileAttributes)>();
        var buffer = new byte[64 * 1024];
        bool first = true;
        while (true)
        {
            if (!GetFileInformationByHandleEx(dir, first ? FileFullDirectoryRestartInfo : FileFullDirectoryInfo, buffer, buffer.Length))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreFiles) return result;
                throw new Win32Exception(error);
            }
            first = false;
            int offset = 0;
            while (true)
            {
                var attributes = (FileAttributes)BitConverter.ToUInt32(buffer, offset + 56);
                int nameBytes = BitConverter.ToInt32(buffer, offset + 60);
                result.Add((System.Text.Encoding.Unicode.GetString(buffer, offset + 68, nameBytes), attributes));
                int next = BitConverter.ToInt32(buffer, offset);
                if (next == 0) break;
                offset += next;
            }
        }
    }

    // ---- Kirli mi ----

    private static HashSet<SecurityIdentifier> TrustedSet(IEnumerable<SecurityIdentifier> also) =>
        [Sid(WellKnownSidType.LocalSystemSid), Sid(WellKnownSidType.BuiltinAdministratorsSid), TrustedInstaller, .. also];

    /// <summary>
    /// Sahibi güvenilmeyen biri, izin listesi yok ya da güvenilmeyen birine yazma/silme/izin/sahiplik hakkı veriyor: o hak bir
    /// zaman verilmişse önceden açılmış tanıtıcı onu korur.
    /// </summary>
    private static bool IsTainted(SafeFileHandle h, HashSet<SecurityIdentifier> trusted)
    {
        int error = GetSecurityInfo(h, SeFileObject, OwnerSecurityInformation | DaclSecurityInformation, out _, out _, out _, out _, out IntPtr sd);
        if (error != 0) throw new Win32Exception(error);
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(sd)];
            Marshal.Copy(sd, bytes, 0, bytes.Length);
            var raw = new RawSecurityDescriptor(bytes, 0);
            if (raw.Owner is null || !trusted.Contains(raw.Owner)) return true;
            if ((raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || raw.DiscretionaryAcl is null) return true;
            foreach (var ace in raw.DiscretionaryAcl)
            {
                if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
                if (ace.AceType is AceType.AccessDenied or AceType.AccessDeniedObject or AceType.AccessDeniedCallback) continue;
                if (ace is not KnownAce known) return true; // tanınmayan izin girdisi: kirli say
                if (!trusted.Contains(known.SecurityIdentifier) && (known.AccessMask & WriteRights) != 0) return true;
            }
            return false;
        }
        finally { LocalFree(sd); }
    }

    // ---- İzin ----

    private static byte[] Descriptor(bool isContainer, State state)
    {
        var inherit = isContainer ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var dacl = new DiscretionaryAcl(isContainer, false, state.Full.Count + 1);
        foreach (var sid in state.Full)
            dacl.AddAccess(AccessControlType.Allow, sid, (int)FileSystemRights.FullControl, inherit, PropagationFlags.None);
        // SYNCHRONIZE da: okuyan programlar (.NET, Not Defteri) onu da ister; yalnız ReadAndExecute ile "Günlüğü aç" reddedilir
        dacl.AddAccess(AccessControlType.Allow, state.Readers, (int)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize),
            inherit, PropagationFlags.None);
        var sd = new CommonSecurityDescriptor(isContainer, false,
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected | ControlFlags.SelfRelative,
            state.Owner, null, null, dacl);
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static void SetSecurity(SafeFileHandle h, bool isContainer, State state)
    {
        // Sahip ve korumalı izin listesi tek çağrıda; SetKernelObjectSecurity alt öğelere YAYMAZ: her öğe kendisi gezilir
        if (!SetKernelObjectSecurity(h, OwnerSecurityInformation | DaclSecurityInformation, Descriptor(isContainer, state)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Klasörü SON izinleriyle açar (atomik). false: o adda bir şey var (bu arada açılmış).</summary>
    private static bool CreateDirectoryWithSecurity(string path, State state)
    {
        var sd = GCHandle.Alloc(Descriptor(isContainer: true, state), GCHandleType.Pinned);
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd.AddrOfPinnedObject() };
            if (CreateDirectoryW(path, ref sa)) return true;
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorAlreadyExists) return false;
            throw new Win32Exception(error, $"{path}: {new Win32Exception(error).Message}");
        }
        finally { sd.Free(); }
    }

    private static SafeFileHandle? CreateRelativeDirectory(SafeFileHandle parent, string name, State state, out uint status)
    {
        var sd = GCHandle.Alloc(Descriptor(isContainer: true, state), GCHandleType.Pinned);
        try { return CreateRelative(parent, name, DirAccess, FileDirectoryFile | FileSynchronousIoNonAlert, sd.AddrOfPinnedObject(), out status); }
        finally { sd.Free(); }
    }

    // ---- Silme, taşıma ----

    /// <summary>Yoldaki öğeyi (bağ ise bağın kendisini) DELETE ile açıp koşul hâlâ doğruysa POSIX anlamlı siler ve gittiğini denetler.</summary>
    private static void DeletePath(string path, Func<FileInfoByHandle, bool> condition)
    {
        using (var h = OpenPath(path, DeleteAccess | FileReadAttributes | Synchronize))
        {
            if (h is null) return;
            if (!condition(Info(h))) throw new IOException(WinText.T("DataDir.StillLink", path));
            PosixDelete(h);
        }
        using var again = OpenPath(path, FileReadAttributes | Synchronize);
        if (again is not null) throw new IOException(WinText.T("DataDir.StillLink", path));
    }

    private static void DeleteRelative(SafeFileHandle dir, string name, string path, Func<FileInfoByHandle, bool> condition)
    {
        using (var h = OpenRelative(dir, name, DeleteAccess | FileReadAttributes | Synchronize, 0, out uint status))
        {
            if (h is null)
            {
                if (IsNotFound(status)) return;
                throw NtError(status, path);
            }
            if (!condition(Info(h))) throw new IOException(WinText.T("DataDir.StillLink", path));
            PosixDelete(h);
        }
        using var again = OpenRelative(dir, name, FileReadAttributes | Synchronize, 0, out uint againStatus);
        if (again is not null || !IsNotFound(againStatus)) throw new IOException(WinText.T("DataDir.StillLink", path));
    }

    /// <summary>Kirli kökü yanına <c>&lt;ad&gt;.old-xxxxxxxx</c> adıyla taşır; yeni adı döndürür.</summary>
    private static string MovePathAside(string path)
    {
        string aside = path + ".old-" + Guid.NewGuid().ToString("N")[..8];
        using var h = OpenPath(path, DeleteAccess | FileReadAttributes | Synchronize) ?? throw new IOException(WinText.T("DataDir.StillLink", path));
        Rename(h, null, @"\??\" + aside, 0, path); // kökü olmayan NT çağrısı tam NT yolu ister
        return aside;
    }

    /// <summary>
    /// Kirli alt klasörü kökün DIŞINA, köke kardeş <c>&lt;kök&gt;.&lt;ad&gt;.old-xxxxxxxx</c> adıyla taşır: eski tanıtıcılar güvenli
    /// alanın içinde yazılabilir bir klasör tutmasın (yürüyüş ona hiç girmez).
    /// </summary>
    private static string MoveRelativeAsideOutside(SafeFileHandle parent, string parentPath, string name)
    {
        string aside = parentPath + "." + name + ".old-" + Guid.NewGuid().ToString("N")[..8];
        string path = Path.Combine(parentPath, name);
        using var h = OpenRelative(parent, name, DeleteAccess | FileReadAttributes | Synchronize, 0, out uint status) ?? throw NtError(status, path);
        Rename(h, null, @"\??\" + aside, 0, path);
        return aside;
    }

    /// <summary>POSIX anlamlı silme: ad hemen kalkar (başka tanıtıcı geri alamaz); salt okunur öznitelik engel olmaz.</summary>
    private static void PosixDelete(SafeFileHandle h)
    {
        var flags = BitConverter.GetBytes(DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly);
        if (!SetFileInformationByHandle(h, FileDispositionInfoExClass, flags, (uint)flags.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// FILE_RENAME_INFO (x64): Flags 4 + dolgu 4, RootDirectory 8, FileNameLength 4, FileName. <paramref name="root"/> verilirse ad
    /// ona görelidir (yol çözülmez), verilmezse tam yol.
    /// </summary>
    private static void Rename(SafeFileHandle h, SafeFileHandle? root, string target, uint flags, string pathForError)
    {
        bool added = false;
        try
        {
            root?.DangerousAddRef(ref added);
            var info = new byte[Math.Max(24, 20 + (target.Length + 1) * 2)];
            BitConverter.TryWriteBytes(info.AsSpan(0), flags);
            BitConverter.TryWriteBytes(info.AsSpan(8), root is null ? 0L : root.DangerousGetHandle().ToInt64());
            BitConverter.TryWriteBytes(info.AsSpan(16), target.Length * 2);
            System.Text.Encoding.Unicode.GetBytes(target, info.AsSpan(20));
            // Doğrudan NT çağrısı: kernel32'nin yolu göreli RootDirectory'yi "parametre hatalı" ile reddediyor (ölçüldü)
            uint status = NtSetInformationFile(h, out _, info, (uint)info.Length, FileRenameInformationEx);
            if (status != 0) throw NtError(status, pathForError);
        }
        finally { if (added) root!.DangerousRelease(); }
    }

    // ---- Açma ----

    /// <summary>Yolu bağı İZLEMEDEN açar; yoksa null. Başka hata istisnayla.</summary>
    private static SafeFileHandle? OpenPath(string path, uint access)
    {
        var h = CreateFileW(path, access, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        int error = Marshal.GetLastWin32Error();
        h.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound) return null;
        throw new Win32Exception(error, $"{path}: {new Win32Exception(error).Message}");
    }

    /// <summary>
    /// <paramref name="dir"/>'e göreli açar (boş ad = aynı nesne): bağ İZLENMEZ, yol üzerindeki hiçbir bağdan geçilmez
    /// (<c>OBJ_DONT_REPARSE</c>). Başarısızsa null ve NTSTATUS.
    /// </summary>
    private static SafeFileHandle? OpenRelative(SafeFileHandle dir, string name, uint access, uint options, out uint status) =>
        NtOpenOrCreate(dir, name, access, FileOpen, options | FileSynchronousIoNonAlert, IntPtr.Zero, out status);

    private static SafeFileHandle? CreateRelative(SafeFileHandle dir, string name, uint access, uint options, IntPtr? descriptor, out uint status) =>
        NtOpenOrCreate(dir, name, access, FileCreate, options, descriptor ?? IntPtr.Zero, out status);

    private static SafeFileHandle? NtOpenOrCreate(SafeFileHandle dir, string name, uint access, uint disposition, uint options,
        IntPtr descriptor, out uint status)
    {
        bool added = false;
        IntPtr nameBuffer = Marshal.StringToHGlobalUni(name);
        IntPtr us = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            dir.DangerousAddRef(ref added);
            Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = nameBuffer }, us, false);
            var oa = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = dir.DangerousGetHandle(), ObjectName = us,
                Attributes = ObjCaseInsensitive | ObjDontReparse, SecurityDescriptor = descriptor,
            };
            status = NtCreateFile(out IntPtr raw, access, ref oa, out _, IntPtr.Zero, 0, (uint)(FileShare.ReadWrite | FileShare.Delete),
                disposition, options | FileOpenReparsePoint | FileOpenForBackupIntent, IntPtr.Zero, 0);
            return status == 0 ? new SafeFileHandle(raw, ownsHandle: true) : null;
        }
        finally
        {
            if (added) dir.DangerousRelease();
            Marshal.FreeHGlobal(us);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static bool IsNotFound(uint status) => status is StatusObjectNameNotFound or StatusObjectPathNotFound;

    private static Win32Exception NtError(uint status, string path)
    {
        int error = RtlNtStatusToDosError(status);
        return new Win32Exception(error, $"{path}: {new Win32Exception(error).Message}");
    }

    private static bool IsLink(FileInfoByHandle info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsDirectory(FileInfoByHandle info) => (info.Attributes & FileAttributes.Directory) != 0;

    private static FileInfoByHandle Info(SafeFileHandle h)
    {
        if (!GetFileInformationByHandle(h, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new FileInfoByHandle((FileAttributes)info.FileAttributes, info.NumberOfLinks);
    }

    private readonly record struct FileInfoByHandle(FileAttributes Attributes, uint Links);

    private sealed class State(SecurityIdentifier owner, IReadOnlyList<SecurityIdentifier> full, SecurityIdentifier readers)
    {
        public SecurityIdentifier Owner { get; } = owner;
        public IReadOnlyList<SecurityIdentifier> Full { get; } = full;
        public SecurityIdentifier Readers { get; } = readers;
        public HashSet<SecurityIdentifier> Trusted { get; } = TrustedSet([owner, .. full]);
        public List<string> Aside { get; } = [];
        public int Secured, LinksRemoved;
        public string? FirstError;
        public void Fail(string message) => FirstError ??= message;
    }

    /// <summary>Süreç belirtecinde yetkileri açar; Dispose önceki hâline döndürür. Yetki belirteçte yoksa sessizce geçilir.</summary>
    private sealed class Privileges : IDisposable
    {
        private readonly IntPtr _token;
        private readonly byte[]? _previous;

        private Privileges(IntPtr token, byte[]? previous) { _token = token; _previous = previous; }

        public static Privileges Enable(params string[] names)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return new Privileges(IntPtr.Zero, null);
            var luids = new List<long>();
            foreach (var name in names)
                if (LookupPrivilegeValueW(null, name, out long luid)) luids.Add(luid);
            // TOKEN_PRIVILEGES: DWORD sayı + her biri için LUID (8) + DWORD öznitelik
            var state = new byte[4 + luids.Count * 12];
            BitConverter.TryWriteBytes(state.AsSpan(0), luids.Count);
            for (int i = 0; i < luids.Count; i++)
            {
                BitConverter.TryWriteBytes(state.AsSpan(4 + i * 12), luids[i]);
                BitConverter.TryWriteBytes(state.AsSpan(12 + i * 12), SePrivilegeEnabled);
            }
            var previous = new byte[state.Length + 16];
            if (!AdjustTokenPrivileges(token, false, state, previous.Length, previous, out _)) previous = null;
            return new Privileges(token, previous);
        }

        public void Dispose()
        {
            if (_token == IntPtr.Zero) return;
            if (_previous is not null) AdjustTokenPrivileges(_token, false, _previous, 0, null, out _);
            CloseHandle(_token);
        }
    }

    // ---- Win32 / NT ----
    private const uint FileListDirectory = 0x1, FileReadData = 0x1, FileWriteData = 0x2, FileReadAttributes = 0x80;
    private const uint DeleteAccess = 0x10000, ReadControl = 0x20000, WriteDac = 0x40000, WriteOwner = 0x80000, Synchronize = 0x100000;
    private const uint MetaAccess = FileReadAttributes | ReadControl | WriteDac | WriteOwner | Synchronize;
    private const uint DirAccess = MetaAccess | FileListDirectory;

    /// <summary>Yazma sayılan haklar: veri/ekleme, EA, alt öğe silme, öznitelik, silme, izin, sahiplik, genel yazma/tam.</summary>
    private const int WriteRights = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;

    private const uint FileFlagBackupSemantics = 0x02000000, FileFlagOpenReparsePoint = 0x00200000;
    private const uint OwnerSecurityInformation = 0x1, DaclSecurityInformation = 0x4;
    private const int SeFileObject = 1;
    private const int FileRenameInformationEx = 65, FileDispositionInfoExClass = 21;
    private const int FileFullDirectoryInfo = 14, FileFullDirectoryRestartInfo = 15;
    private const uint RenameReplaceIfExists = 0x1, RenamePosixSemantics = 0x2, RenameIgnoreReadOnly = 0x40;
    private const uint DispositionDelete = 0x1, DispositionPosixSemantics = 0x2, DispositionIgnoreReadOnly = 0x10;
    private const uint FileOpen = 1, FileCreate = 2;
    private const uint FileDirectoryFile = 0x1, FileSynchronousIoNonAlert = 0x20, FileNonDirectoryFile = 0x40;
    private const uint FileOpenForBackupIntent = 0x4000, FileOpenReparsePoint = 0x200000;
    private const uint ObjCaseInsensitive = 0x40, ObjDontReparse = 0x1000;
    private const uint StatusObjectNameNotFound = 0xC0000034, StatusObjectPathNotFound = 0xC000003A;
    private const uint StatusObjectNameCollision = 0xC0000035, StatusNotADirectory = 0xC0000103;
    private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorNoMoreFiles = 18, ErrorAlreadyExists = 183, ErrorSharingViolation = 32;
    private const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x8, SePrivilegeEnabled = 0x2;

    // FILETIME 4 bayt hizalı: Pack = 4 olmadan long alanlar 8'e hizalanır ve NumberOfLinks kayar
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory, ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status, Information; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes security);

    [DllImport("ntdll.dll")]
    private static extern uint NtCreateFile(out IntPtr handle, uint access, ref ObjectAttributes oa, out IoStatusBlock iosb,
        IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(uint status);

    [DllImport("ntdll.dll")]
    private static extern uint NtSetInformationFile(SafeFileHandle h, out IoStatusBlock iosb, byte[] info, uint length, int infoClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, byte[] buffer, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, byte[] info, uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle h, uint securityInformation, byte[] securityDescriptor);

    [DllImport("advapi32.dll")]
    private static extern int GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo, out IntPtr owner, out IntPtr group,
        out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("advapi32.dll")]
    private static extern int GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, byte[] newState,
        int bufferLength, byte[]? previousState, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);
}

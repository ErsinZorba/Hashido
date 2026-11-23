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
/// dosyaların izinlerini değiştiriyor (<c>/L</c> de durdurmuyor; yönetici setup'ı kullanıcının yerleştirdiği bağla System32'ye
/// dokunabilirdi) ve aynı komut var olan dosyalarda izin listesini boş bırakıyordu. Burada her öğe bağı İZLEMEDEN açılır
/// (<c>FILE_FLAG_OPEN_REPARSE_POINT</c>), türü tanıtıcıdan okunur, izin tanıtıcıya yazılır (<c>SetKernelObjectSecurity</c>: alt
/// öğelere yayılmaz). Bir klasörün içine ancak kendisi güvenceye alındıktan SONRA inilir: içerik artık kullanıcıca değiştirilemez.
/// </remarks>
public static class DataDirSecurity
{
    private const int MaxDepth = 16;
    private const int MaxItems = 10_000;

    /// <summary>Sonuç: silinen bağ/sabit bağ sayısı, güvenceye alınan öğe sayısı, ilk hata (yoksa null).</summary>
    public sealed record Result(int Secured, int LinksRemoved, string? FirstError);

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
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var result = Secure(dir, admins, [new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), admins],
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
            // Rapor klasörü açılışta var olsun (önceden ilk onayda açılıyordu); kökten miras alır
            Directory.CreateDirectory(Path.Combine(dir, "reports"));
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            return new Result(0, 0, e.Message);
        }
    }

    /// <summary>
    /// <paramref name="dir"/>'i güvenceye alır (yoksa açar; kendisi bağsa bağı silip gerçek klasör açar). Testler kendi kullanıcısını
    /// sahip ve tam yetkili verir (standart kullanıcı sahibi Yöneticiler yapamaz).
    /// </summary>
    internal static Result Secure(string dir, SecurityIdentifier owner, IReadOnlyList<SecurityIdentifier> full, SecurityIdentifier readers)
    {
        var state = new State(owner, full, readers);
        dir = Path.GetFullPath(dir);
        for (int attempt = 0; ; attempt++)
        {
            if (attempt >= 3) throw new IOException(WinText.T("DataDir.StillLink", dir));
            if (!Path.Exists(dir) && !IsDanglingLink(dir)) Directory.CreateDirectory(dir);
            using var h = Open(dir);
            var info = Info(h);
            if (IsLink(info) || (info.Attributes & FileAttributes.Directory) == 0)
            {
                Delete(h); // bağ ya da klasörün yerindeki dosya
                state.LinksRemoved++;
                continue;
            }
            SetSecurity(h, isContainer: true, state);
            state.Secured++;
            break;
        }
        SecureChildren(dir, 1, state);
        return new Result(state.Secured, state.LinksRemoved, state.FirstError);
    }

    private static void SecureChildren(string dir, int depth, State state)
    {
        if (depth > MaxDepth) { state.Fail(WinText.T("DataDir.DepthLimit", dir)); return; }
        foreach (string path in Directory.EnumerateFileSystemEntries(dir))
        {
            if (state.Secured + state.LinksRemoved >= MaxItems) { state.Fail(WinText.T("DataDir.ItemLimit", dir)); return; }
            try
            {
                bool isDir;
                using (var h = Open(path))
                {
                    var info = Info(h);
                    isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    if (IsLink(info) || (!isDir && info.Links > 1))
                    {
                        Delete(h);
                        state.LinksRemoved++;
                        continue;
                    }
                    SetSecurity(h, isDir, state);
                    state.Secured++;
                }
                if (isDir) SecureChildren(path, depth + 1, state);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
            {
                state.Fail($"{path}: {e.Message}");
            }
        }
    }

    private static bool IsLink(FileInfoByHandle info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsDanglingLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void SetSecurity(SafeFileHandle h, bool isContainer, State state)
    {
        var inherit = isContainer ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var dacl = new DiscretionaryAcl(isContainer, false, state.Full.Count + 1);
        foreach (var sid in state.Full)
            dacl.AddAccess(AccessControlType.Allow, sid, (int)FileSystemRights.FullControl, inherit, PropagationFlags.None);
        dacl.AddAccess(AccessControlType.Allow, state.Readers, (int)FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None);
        var sd = new CommonSecurityDescriptor(isContainer, false,
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected | ControlFlags.SelfRelative,
            state.Owner, null, null, dacl);
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        // Sahip ve korumalı izin listesi tek çağrıda; tanıtıcı WRITE_OWNER + WRITE_DAC ile açıldı (SYSTEM'de geri yükleme yetkisi
        // izin listesinden bağımsız verir). SetKernelObjectSecurity alt öğelere YAYMAZ: her öğe kendisi gezilir.
        if (!SetKernelObjectSecurity(h, OwnerSecurityInformation | DaclSecurityInformation, bytes))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static SafeFileHandle Open(string path)
    {
        var h = CreateFileW(path, FileReadAttributes | WriteDac | WriteOwner | DeleteAccess,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new Win32Exception(error, $"{path}: {new Win32Exception(error).Message}");
        }
        return h;
    }

    private static FileInfoByHandle Info(SafeFileHandle h)
    {
        if (!GetFileInformationByHandle(h, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new FileInfoByHandle((FileAttributes)info.FileAttributes, info.NumberOfLinks);
    }

    /// <summary>Tanıtıcının açtığı adı siler: bağın kendisini (hedefe dokunmaz) ya da sabit bağlı dosyanın bu adını.</summary>
    private static void Delete(SafeFileHandle h)
    {
        byte deleteFile = 1;
        if (!SetFileInformationByHandle(h, FileDispositionInfoClass, ref deleteFile, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private readonly record struct FileInfoByHandle(FileAttributes Attributes, uint Links);

    private sealed class State(SecurityIdentifier owner, IReadOnlyList<SecurityIdentifier> full, SecurityIdentifier readers)
    {
        public SecurityIdentifier Owner { get; } = owner;
        public IReadOnlyList<SecurityIdentifier> Full { get; } = full;
        public SecurityIdentifier Readers { get; } = readers;
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

    // ---- Win32 ----
    private const uint FileReadAttributes = 0x80, WriteDac = 0x40000, WriteOwner = 0x80000, DeleteAccess = 0x10000;
    private const uint FileFlagBackupSemantics = 0x02000000, FileFlagOpenReparsePoint = 0x00200000;
    private const uint OwnerSecurityInformation = 0x1, DaclSecurityInformation = 0x4;
    private const int FileDispositionInfoClass = 4;
    private const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x8, SePrivilegeEnabled = 0x2;

    // FILETIME 4 bayt hizalı: Pack = 4 olmadan long alanlar 8'e hizalanır ve NumberOfLinks kayar (sıradan dosya "sabit bağ" sanılıp
    // silinirdi; ExistingContentKeepsWorkingAndLosesUserWrite yakaladı)
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref byte info, uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle h, uint securityInformation, byte[] securityDescriptor);

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

using System.Runtime.InteropServices;

namespace Apfs.Mount;

/// <summary>
/// Açılışta uygulamanın İSTEDİĞİ erişim (WinFsp .NET katmanı Open'a yalnız erişim denetiminden çıkan VERİLEN erişimi iletir:
/// MAXIMUM_ALLOWED isteyen açılış, ACL herkese tam erişim verdiği için yazma hakları "verilmiş" görünür). Değer, WinFsp'nin
/// yerel <c>FspFileSystemGetOperationContext()</c> işlevinin döndürdüğü isteğin (FSP_FSCTL_TRANSACT_REQ, Create) DesiredAccess
/// alanından okunur; DLL zaten WinFsp tarafından yüklüdür (yeni yükleme yok).
/// <para>
/// Yerleşim (winfsp/fsctl.h, ölçüldü 2026-11-08, WinFsp 2.1): Version u16 @0, Size u16 @2, Kind u32 @4 (1 = Create), Hint u64 @8,
/// Create.CreateOptions u32 @16, FileAttributes @20, SecurityDescriptor @24, AllocationSize u64 @32, AccessToken u64 @40,
/// DesiredAccess u32 @48, GrantedAccess u32 @52 (dosya sistemi güvenlik tanımlayıcısı kabul etmiyorsa 0), ShareAccess u32 @56.
/// </para>
/// Yerleşim kendini doğrular: tür Create, boyut ≥ 64, CreateOptions Open'a gelenle aynı, yapıdaki GrantedAccess 0 ya da gelenle
/// aynı, istenen özel haklar verilenin alt kümesi. Biri tutmazsa (başka WinFsp sürümü, beklenmedik yapı) false döner ve çağıran
/// eski, güvenli davranışa düşer (yazma hakkı verilmişse açılış reddedilir).
/// </summary>
internal static unsafe class RequestedAccess
{
    private const uint KindCreate = 1;
    private const uint MaximumAllowed = 0x02000000, AccessSystemSecurity = 0x01000000;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, GenericExecute = 0x20000000, GenericAll = 0x10000000;
    private const uint FileGenericRead = 0x120089, FileGenericWrite = 0x120116, FileGenericExecute = 0x1200A0, FileAllAccess = 0x1F01FF;

    private static readonly delegate* unmanaged<IntPtr> GetOperationContext = Resolve();

    private static delegate* unmanaged<IntPtr> Resolve()
    {
        string dll = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "winfsp-x64.dll",
            Architecture.Arm64 => "winfsp-a64.dll",
            Architecture.X86 => "winfsp-x86.dll",
            _ => "",
        };
        IntPtr module = dll.Length == 0 ? IntPtr.Zero : GetModuleHandleW(dll);
        IntPtr fn = module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, "FspFileSystemGetOperationContext");
        return (delegate* unmanaged<IntPtr>)fn;
    }

    /// <summary>GENERIC_* haklarını dosya haklarına çevirir (Windows'un dosya eşlemesi).</summary>
    internal static uint MapGeneric(uint access)
    {
        uint r = access & ~(GenericRead | GenericWrite | GenericExecute | GenericAll);
        if ((access & GenericRead) != 0) r |= FileGenericRead;
        if ((access & GenericWrite) != 0) r |= FileGenericWrite;
        if ((access & GenericExecute) != 0) r |= FileGenericExecute;
        if ((access & GenericAll) != 0) r |= FileAllAccess;
        return r;
    }

    /// <summary>Şu anki Open isteğinin DesiredAccess'i (yalnız Open geri çağrısının içinden; aynı iş parçacığı).</summary>
    public static bool TryGet(uint createOptions, uint grantedAccess, out uint desired)
    {
        desired = 0;
        if (GetOperationContext == null) return false;
        IntPtr ctx = GetOperationContext();
        if (ctx == IntPtr.Zero) return false;
        byte* req = *(byte**)ctx; // FSP_FILE_SYSTEM_OPERATION_CONTEXT.Request
        if (req == null) return false;
        ushort size = *(ushort*)(req + 2);
        uint kind = *(uint*)(req + 4);
        if (kind != KindCreate || size < 64) return false;
        if (*(uint*)(req + 16) != createOptions) return false;
        uint structGranted = *(uint*)(req + 52);
        if (structGranted != 0 && structGranted != grantedAccess) return false;
        uint d = *(uint*)(req + 48);
        if ((MapGeneric(d) & ~(MaximumAllowed | AccessSystemSecurity) & ~grantedAccess) != 0) return false; // denetim istenen her hakkı vermiş olmalı
        desired = d;
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
}

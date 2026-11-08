using System.Text.RegularExpressions;
using Apfs.Container;
using Apfs.Util;

namespace Apfs.Diagnostics;

/// <summary>
/// Bir birimin kişiyi ya da diski tanımlamayan yapı bilgileri (1.4.0 raporlaması). Ad, UUID, tam boyut YOKTUR; sayılar ham
/// tutulur, rapora yalnız aralık olarak geçer (Apfs.Windows/Reporting). Salt okunur: yalnız birim süper bloğunu okur.
/// </summary>
/// <param name="Role">Birim rolü (sabit İngilizce simge: "none", "user", "system"…; bilinmeyen "other").</param>
/// <param name="Encryption">"none" (şifresiz), "onekey" (yazılımla, tek anahtar), "perfile" (dosya başına anahtar), "other".</param>
/// <param name="LastWriter">Birime en son yazan yazılımın kimliği, yalnız bilinen kalıba uyarsa (macOS "apfs_kext (2313.41.1)"
/// ya da bu programın kendi kimliği); kalıba uymayan her şey atılır (null).</param>
public sealed record VolumeFacts(string Role, string Encryption, bool CaseSensitive, bool HasSnapshots, ulong FileCount,
    string? LastWriter, ulong Features, ulong Incompatible, ulong ReadOnlyCompatible);

/// <summary>Kapsayıcının raporlanabilir yapı bilgileri. Okunamayan birim listede yer almaz (<see cref="UnreadableVolumes"/>).</summary>
public sealed partial record ContainerFacts(uint BlockSize, ulong ByteSize, ulong Features, ulong Incompatible, ulong ReadOnlyCompatible,
    bool SoftwareCrypto, IReadOnlyList<VolumeFacts> Volumes, int UnreadableVolumes)
{
    public static ContainerFacts Of(ApfsContainer c)
    {
        var sb = c.Superblock;
        var volumes = new List<VolumeFacts>();
        int unreadable = 0;
        foreach (ulong oid in sb.FsOids)
        {
            byte[] v;
            try { v = c.Reader.ReadObject(c.Omap.Resolve(oid, c.Xid).Paddr, ApfsConstants.TypeFs, oid); }
            catch (ApfsException) { unreadable++; continue; }
            ulong fsFlags = Bin.U64(v, 264);
            ulong incompat = Bin.U64(v, 56);
            string encryption = (fsFlags & 3) == ApfsConstants.ApfsFsUnencrypted ? "none"
                : fsFlags == ApfsConstants.ApfsFsOneKey ? "onekey"
                : (fsFlags & 3) == 0 ? "perfile" : "other";
            volumes.Add(new VolumeFacts(RoleToken(Bin.U16(v, 964)), encryption,
                CaseSensitive: (incompat & ApfsConstants.ApfsIncompatCaseInsensitive) == 0,
                HasSnapshots: Bin.U64(v, 216) != 0,
                FileCount: Bin.U64(v, 184),
                LastWriter: LastWriterOf(v),
                Features: Bin.U64(v, 40), Incompatible: incompat, ReadOnlyCompatible: Bin.U64(v, 48)));
        }
        return new ContainerFacts(sb.BlockSize, sb.BlockCount * sb.BlockSize, sb.Features, sb.IncompatibleFeatures,
            sb.ReadOnlyCompatibleFeatures, (sb.Flags & ApfsConstants.NxCryptoSw) != 0, volumes, unreadable);
    }

    /// <summary>apfs_modified_by[0] (yoksa apfs_formatted_by) kimliği; yalnız <see cref="WriterPattern"/>'e uyarsa.</summary>
    private static string? LastWriterOf(byte[] apsb)
    {
        const int FormattedBy = 272, ModifiedBy = 320;
        string id = Bin.Utf8Z(Bin.Slice(apsb, ModifiedBy, 32));
        if (id.Length == 0) id = Bin.Utf8Z(Bin.Slice(apsb, FormattedBy, 32));
        return WriterPattern().IsMatch(id) ? id : null;
    }

    /// <summary>
    /// macOS: "apfs_kext (2313.41.1)", "newfs_apfs (…)", "hfs_convert (…)"; bu program: "apfs-for-windows 1.2.0". Başka biçim
    /// (ör. serbest metin taşıyabilen üçüncü taraf kimlikleri) rapora girmez. Worker şeması aynı kalıbı uygular.
    /// </summary>
    [GeneratedRegex(@"^([a-z_]{1,24} \([0-9]{1,5}(\.[0-9]{1,5}){0,4}\)|apfs-for-windows [0-9]{1,3}(\.[0-9]{1,3}){1,3})$")]
    public static partial Regex WriterPattern();

    private static string RoleToken(ushort role) => role switch
    {
        ApfsConstants.RoleNone => "none",
        ApfsConstants.RoleSystem => "system",
        ApfsConstants.RoleUser => "user",
        ApfsConstants.RoleRecovery => "recovery",
        ApfsConstants.RoleVm => "vm",
        ApfsConstants.RolePreboot => "preboot",
        ApfsConstants.RoleInstaller => "installer",
        ApfsConstants.RoleData => "data",
        ApfsConstants.RoleBaseband => "baseband",
        ApfsConstants.RoleUpdate => "update",
        ApfsConstants.RoleXart => "xart",
        ApfsConstants.RoleHardware => "hardware",
        ApfsConstants.RoleBackup => "backup",
        0x01C0 => "sidecar",
        0x0240 => "enterprise",
        0x02C0 => "prelogin",
        _ => "other",
    };
}

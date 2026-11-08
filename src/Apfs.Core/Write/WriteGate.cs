using Apfs.Container;
using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Util;

namespace Apfs.Write;

/// <summary>Yazma kapısının kararı. İzin verilmediyse <see cref="Reasons"/> kullanıcıya gösterilecek sade nedenlerdir.</summary>
public sealed record GateResult(bool Allowed, IReadOnlyList<string> Reasons, WriteGrant? Grant, CheckReport? Report)
{
    /// <summary>
    /// <see cref="Reasons"/> ile AYNI sırada ve uzunlukta, her nedenin metin kaynağı anahtarı (ör. "Gate.Snapshots"). Raporlama
    /// metin yerine bunu kullanır: anahtar dile bağlı değildir ve birim adı gibi yer tutucu değerleri taşımaz.
    /// </summary>
    public IReadOnlyList<string> ReasonKeys { get; init; } = [];
}

/// <summary>
/// Yazma kapısı: bir kapsayıcıya yazmaya başlamadan önce yapılan denetim. Tek bir kural bile tutmazsa disk salt okunur kalır.
/// Kurallar:
/// <list type="bullet">
/// <item>kapsayıcıdaki BÜTÜN birimler anlık görüntüsüz, mühürsüz ve rolsüz/Kullanıcı rollü olmalı (alan yöneticisi ortak
/// olduğu için kardeş birimler de önemlidir);</item>
/// <item>şifreli birim (1.2.0) yalnızca: yazılım şifrelemesi tek anahtarla (fs bayrakları tam ONEKEY), kapsayıcıda NX_CRYPTO_SW,
/// kilidi açılmış anahtar verilmiş, şifreleme işlemi yok/bitmiş (er_state 0, ENC_ROLLED yok, omap'te şifreleme/anahtar değiştirme
/// bayrağı yok), dosya başına anahtar (crypto_state) yok ve tam denetim anahtarla temiz;</item>
/// <item>Fusion yok, checkpoint halkaları bitişik, blok boyutu 4096, tanınmayan özellik biti yok;</item>
/// <item>en yeni checkpoint kullanılıyor (daha eskisine düşülmemiş), reaper boşta;</item>
/// <item>tam denetleyici (<see cref="FullCheck"/>) 0 hata veriyor.</item>
/// </list>
/// </summary>
public static class WriteGate
{
    // Bilinen ve yazıcının desteklediği özellik bitleri
    private const ulong NxFeatureDefrag = 0x1;
    private const ulong NxIncompatAllowed = ApfsConstants.NxIncompatVersion2;
    private const ulong ApfsFeaturesAllowed = 0x2 /* HARDLINK_MAP_RECORDS */ | 0x4 /* DEFRAG */ | 0x8 /* STRICTATIME */;
    private const ulong ApfsIncompatAllowed = ApfsConstants.ApfsIncompatCaseInsensitive | ApfsConstants.ApfsIncompatNormalizationInsensitive;
    private const ulong ApfsFsFlagsAllowed = ApfsConstants.ApfsFsUnencrypted;
    private const uint OmapCryptoBusy = ApfsConstants.OmapEncrypting | ApfsConstants.OmapDecrypting | ApfsConstants.OmapKeyrolling | ApfsConstants.OmapCryptoGeneration;

    /// <param name="keys">Kilidi açılmış birim anahtarları; şifreli birim ancak kendi anahtarıyla yazmaya açılır.</param>
    public static GateResult Evaluate(ApfsContainer c, IEnumerable<Crypto.VolumeKey>? keys = null)
    {
        var keyList = keys?.ToList() ?? [];
        var reasons = new List<(string Key, string Text)>();
        void Add(string key, string text) => reasons.Add((key, text)); // metin satırda CoreText.U ile (ResourceTests anahtarı orada arar)
        var sb = c.Superblock;

        if ((sb.IncompatibleFeatures & ApfsConstants.NxIncompatFusion) != 0) Add("Gate.Fusion", CoreText.U("Gate.Fusion"));
        if ((sb.IncompatibleFeatures & ~NxIncompatAllowed & ~ApfsConstants.NxIncompatFusion) != 0
            || (sb.Features & ~NxFeatureDefrag) != 0 || sb.ReadOnlyCompatibleFeatures != 0)
            Add("Gate.UnknownContainerFeature", CoreText.U("Gate.UnknownContainerFeature"));
        if (sb.BlockSize != 4096) Add("Gate.BlockSize", CoreText.U("Gate.BlockSize", sb.BlockSize));
        if ((sb.XpDescBlocks & ApfsConstants.XpDescBlocksNonContiguous) != 0 || (sb.XpDataBlocks & ApfsConstants.XpDescBlocksNonContiguous) != 0)
            Add("Gate.CheckpointArea", CoreText.U("Gate.CheckpointArea"));
        if (c.Warnings.Count > 0) Add("Gate.OpenWarnings", CoreText.U("Gate.OpenWarnings"));

        foreach (ulong oid in sb.FsOids)
        {
            byte[] v;
            try { v = c.Reader.ReadObject(c.Omap.Resolve(oid, c.Xid).Paddr, ApfsConstants.TypeFs, oid); }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { Add("Gate.VolumeUnreadable", CoreText.U("Gate.VolumeUnreadable", e.Detail)); continue; }
            string name = Bin.Utf8Z(Bin.Slice(v, 704, 256));
            ulong fsFlags = Bin.U64(v, 264);
            ushort role = Bin.U16(v, 964);
            if ((fsFlags & 3) != ApfsConstants.ApfsFsUnencrypted)
            {
                if (fsFlags != ApfsConstants.ApfsFsOneKey || (sb.Flags & ApfsConstants.NxCryptoSw) == 0)
                    Add("Gate.UnsupportedEncryption", CoreText.U("Gate.UnsupportedEncryption", name));
                else if (!keyList.Any(k => k.VolumeUuid == Bin.Uuid(v, 240)))
                    Add("Gate.Locked", CoreText.U("Gate.Locked", name));
                if ((Bin.U64(v, 56) & ApfsConstants.ApfsIncompatEncRolled) != 0)
                    Add("Gate.EncRolled", CoreText.U("Gate.EncRolled", name));
                try
                {
                    uint omFlags = Bin.U32(c.Reader.ReadObject(Bin.U64(v, 128), ApfsConstants.TypeOmap), 32);
                    if ((omFlags & OmapCryptoBusy) != 0)
                        Add("Gate.EncryptionInProgress", CoreText.U("Gate.EncryptionInProgress", name));
                }
                catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { Add("Gate.OmapUnreadable", CoreText.U("Gate.OmapUnreadable", name)); }
            }
            else if ((fsFlags & ~ApfsFsFlagsAllowed) != 0)
                Add("Gate.UnsupportedVolumeSetting", CoreText.U("Gate.UnsupportedVolumeSetting", name));
            if (Bin.U64(v, 216) != 0) Add("Gate.Snapshots", CoreText.U("Gate.Snapshots", name));
            if (role is not (ApfsConstants.RoleNone or ApfsConstants.RoleUser))
                Add("Gate.SystemVolume", CoreText.U("Gate.SystemVolume", name));
            if (Bin.U64(v, 976) != 0)
                Add("Gate.EncryptionInProgress", CoreText.U("Gate.EncryptionInProgress", name));
            ulong incompat = Bin.U64(v, 56);
            if ((incompat & ApfsConstants.ApfsIncompatSealedVolume) != 0) Add("Gate.Sealed", CoreText.U("Gate.Sealed", name));
            else if ((incompat & ~ApfsIncompatAllowed) != 0 || (Bin.U64(v, 40) & ~ApfsFeaturesAllowed) != 0 || Bin.U64(v, 48) != 0)
                Add("Gate.UnknownVolumeFeature", CoreText.U("Gate.UnknownVolumeFeature", name));
        }

        if (reasons.Count > 0) return Refused(reasons, null);

        // Yapısal denetim en sonda: pahalıdır ve yukarıdaki kurallar geçmeden anlamsızdır
        var report = FullCheck.Run(c, keys: keyList);
        if (!report.IsClean)
            return Refused([("Gate.Inconsistent", CoreText.U("Gate.Inconsistent"))], report);
        if (report.WriteBlockers.Count > 0)
            return Refused([.. report.WriteBlockers.Select(b => ("Gate.WriteBlocker", CoreText.U("Gate.WriteBlocker", b)))], report);
        if (report.OwnershipIncomplete)
            return Refused([("Gate.Unresolvable", CoreText.U("Gate.Unresolvable"))], report);
        return new GateResult(true, [], new WriteGrant(sb.Uuid, sb.Xid), report);
    }

    /// <summary>Aynı metin bir kez (ör. iki birimde aynı neden); anahtar listesi metinle aynı sırada kalır.</summary>
    private static GateResult Refused(List<(string Key, string Text)> reasons, CheckReport? report)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = reasons.Where(r => seen.Add(r.Text)).ToList();
        return new GateResult(false, [.. kept.Select(r => r.Text)], null, report) { ReasonKeys = [.. kept.Select(r => r.Key)] };
    }
}

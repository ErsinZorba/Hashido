using Apfs.Container;
using Apfs.Util;

namespace Apfs.Volume;

/// <summary>
/// Birimin süren şifreleme dönüşümü (er_state_phys_t; apfs_er_state_oid sıfır değilse). macOS bir birimi şifrelerken dosyaları
/// sırayla dönüştürür ve ilerlemeyi burada tutar: <see cref="FileId"/> şu an dönüştürülen veri akışı, <see cref="FileOffset"/>
/// o akışta dönüştürülmüş baytların sonu. O akışın extent'i crypto_id taşısa da ofsetin ötesi diskte hâlâ düzdür
/// (Mac'te yazılmış mac-sifreli-yarim ölçümü, tamDuzeltmePlan P1). Yalnız Mac görüntüsüyle doğrulanan durum
/// "anlaşılmış" sayılır: sürüm 2, yalnız şifreleme (çözme/anahtar değiştirme değil), crypto_id = tweak, hata yok. Anlaşılmayan
/// durumda birimin şifreli extent'leri okunmaz (sessiz yanlış veri yerine okunamaz dosya).
/// </summary>
public sealed class EncryptionRolling
{
    private const uint Magic = 0x464C4142;          // 'FLAB'
    private const uint SupportedVersion = 2;
    private const ulong FlagEncrypting = 0x1, FlagDecrypting = 0x2, FlagKeyrolling = 0x4, FlagPaused = 0x8, FlagFailed = 0x10,
        FlagCidIsTweak = 0x20, FlagCmBlockSizeMask = 0xF00, FlagPhaseMask = 0x3000, FlagFromOnekey = 0x4000;
    private const ulong KnownFlags = FlagEncrypting | FlagDecrypting | FlagKeyrolling | FlagPaused | FlagFailed | FlagCidIsTweak
        | FlagCmBlockSizeMask | FlagPhaseMask | FlagFromOnekey;

    private EncryptionRolling(ulong fileId, ulong fileOffset, string? problem)
    {
        FileId = fileId;
        FileOffset = fileOffset;
        Problem = problem;
    }

    /// <summary>Şu an dönüştürülen veri akışının kimliği (j_file_extent anahtarındaki nesne kimliği).</summary>
    public ulong FileId { get; }

    /// <summary>O akışta şifrelenmiş baytların sonu (mantıksal ofset, bayt).</summary>
    public ulong FileOffset { get; }

    /// <summary>Durum anlaşılamadıysa nedeni (teknik, yalnız tr/en); anlaşıldıysa null.</summary>
    public string? Problem { get; }

    public bool Understood => Problem is null;

    /// <summary>Birimin er_state nesnesini okur; birimde dönüşüm yoksa null. Okunamayan/bilinmeyen durum "anlaşılmamış" döner.</summary>
    internal static EncryptionRolling? Read(ObjectReader reader, ulong oid)
    {
        if (oid == 0) return null;
        byte[] b;
        try
        {
            if (oid >= reader.BlockCount) return Unknown(CoreText.T("Er.Unreadable", oid));
            b = reader.ReadObject(oid, ApfsConstants.TypeErState);
        }
        catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError)
        {
            return Unknown(CoreText.T("Er.Unreadable", oid));
        }
        uint magic = Bin.U32(b, 32), version = Bin.U32(b, 36);
        ulong flags = Bin.U64(b, 40);
        if (magic != Magic || version != SupportedVersion) return Unknown(CoreText.T("Er.UnknownVersion", magic, version));
        // Hiçbir dönüşüm (şifreleme/çözme/anahtar değiştirme) işaretli değilse süren iş yoktur: birim eskisi gibi okunur (bitmiş
        // işlemin bırakılmış nesnesi tamamen şifreli birimi okunamaz yapmasın; bağımsız inceleme, tamDuzeltmePlan P1)
        if ((flags & (FlagEncrypting | FlagDecrypting | FlagKeyrolling)) == 0) return null;
        if ((flags & ~KnownFlags) != 0 || (flags & (FlagDecrypting | FlagKeyrolling | FlagFailed)) != 0
            || (flags & FlagEncrypting) == 0 || (flags & FlagCidIsTweak) == 0)
            return Unknown(CoreText.T("Er.UnknownFlags", flags));
        ulong fileId = Bin.U64(b, 56), fileOffset = Bin.U64(b, 64);
        if (fileOffset % reader.BlockSize != 0) return Unknown(CoreText.T("Er.UnalignedOffset", fileOffset));
        return new EncryptionRolling(fileId, fileOffset, null);
    }

    private static EncryptionRolling Unknown(string problem) => new(0, 0, problem);
}

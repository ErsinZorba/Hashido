namespace Apfs.Volume;

/// <summary>
/// Bir veri akışını extent listesi üzerinden okur. İş parçacığı güvenlidir (durum tutmaz).
/// Extent'ler arasındaki boşluklar ve PhysBlock = 0 olan extent'ler seyrek alandır ve sıfır döner. Son extent'in ötesi okunamaz
/// (Corrupt), seyrek akışta da: macOS ve yazıcımız seyrek dosyanın deliklerini (sondakini de) extent olarak yazar (Mac görüntülerinde
/// seyrek boy = kaplanan); boyu ayrılandan büyük dosya bozuktur (önceden sonsuz sıfır okunuyordu; INODE_IS_SPARSE + dev boy da).
/// </summary>
public sealed class FileDataReader : IDataReader
{
    private readonly ApfsVolume _volume;
    private readonly FileExtent[] _extents;
    private readonly ulong _streamId;

    /// <param name="extents">Mantıksal ofsete göre sıralı extent'ler. Dizi paylaşılabilir (önbellek); değiştirilmez.</param>
    /// <param name="streamId">Veri akışının kimliği (extent anahtarlarındaki nesne kimliği); şifreleme dönüşümü sürerken yarıda
    /// kalan akışı tanımak için (<see cref="ApfsVolume.Rolling"/>).</param>
    public FileDataReader(ApfsVolume volume, FileExtent[] extents, ulong size, ulong streamId = 0)
    {
        _volume = volume;
        _extents = extents;
        _streamId = streamId;
        Length = size;
        ulong prevEnd = 0;
        foreach (var e in _extents)
        {
            if (e.LogicalOffset < prevEnd)
                throw ApfsException.Corrupt(CoreText.T("Volume.ExtentsOverlap"));
            prevEnd = e.LogicalOffset + e.Length;
            if (prevEnd < e.LogicalOffset)
                throw ApfsException.Corrupt(CoreText.T("Volume.ExtentSizeOverflow"));
        }
        CoveredLength = prevEnd;
    }

    public ulong Length { get; }

    /// <summary>Extent'lerin kapladığı uzunluk (son extent'in sonu); okunabilen en çok bayt.</summary>
    public ulong CoveredLength { get; }

    /// <summary><paramref name="offset"/>'ten başlayarak okur; dosya sonunda kısalır. Okunan bayt sayısını döndürür.</summary>
    public int Read(ulong offset, Span<byte> buffer)
    {
        if (offset >= Length) return 0;
        int toRead = (int)Math.Min((ulong)buffer.Length, Length - offset);
        if (offset + (ulong)toRead > CoveredLength)
            throw ApfsException.Corrupt(CoreText.T("Volume.DataBeyondAllocation", Length, CoveredLength));
        buffer = buffer[..toRead];
        uint bs = _volume.Reader.BlockSize;

        int done = 0;
        int idx = FindExtent(offset);
        while (done < toRead)
        {
            ulong pos = offset + (ulong)done;
            // idx: pos'u içeren veya pos'tan sonraki ilk extent
            while (idx < _extents.Length && _extents[idx].LogicalOffset + _extents[idx].Length <= pos) idx++;

            if (idx >= _extents.Length || _extents[idx].LogicalOffset > pos)
            {
                // Seyrek boşluk: bir sonraki extent'e kadar sıfır
                ulong holeEnd = idx < _extents.Length ? _extents[idx].LogicalOffset : ulong.MaxValue;
                int n = (int)Math.Min((ulong)(toRead - done), holeEnd - pos);
                buffer.Slice(done, n).Clear();
                done += n;
                continue;
            }

            var ext = _extents[idx];
            ulong within = pos - ext.LogicalOffset;
            int count = (int)Math.Min((ulong)(toRead - done), ext.Length - within);
            if (ext.PhysBlock == 0)
            {
                buffer.Slice(done, count).Clear();
            }
            else
            {
                ulong lastBlock = ext.PhysBlock + (ext.Length - 1) / bs;
                if (lastBlock >= _volume.Reader.BlockCount || lastBlock < ext.PhysBlock)
                    throw ApfsException.Corrupt(CoreText.T("Volume.ExtentOutsideDisk"));
                if (_volume.Crypto is { } crypto && ext.CryptoId != 0)
                    ReadCrypted(crypto, ext, within, buffer.Slice(done, count));
                else
                    _volume.Reader.ReadBlocks(ext.PhysBlock + within / bs, (long)(within % bs), buffer.Slice(done, count));
            }
            done += count;
        }
        return toRead;
    }

    /// <summary>
    /// crypto_id taşıyan extent. Birimde şifreleme dönüşümü sürüyorsa (<see cref="ApfsVolume.Rolling"/>): dönüştürülen akışta
    /// ilerleme ofsetini içeren extent'in ofset ötesi diskte hâlâ düzdür (çözülmeden okunur); o akışta ofsetten sonra başlayan
    /// crypto_id'li extent'in durumu bilinemez, durum anlaşılamadıysa hiçbir şifreli extent okunmaz (yanlış içerik yerine hata).
    /// </summary>
    private void ReadCrypted(Crypto.VolumeCrypto crypto, FileExtent ext, ulong within, Span<byte> dest)
    {
        if (_volume.Rolling is not { } er)
        {
            ReadEncrypted(crypto, ext, within, dest);
            return;
        }
        if (!er.Understood)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Volume.EncryptionChangeUnreadable", er.Problem));
        if (_streamId != er.FileId || ext.LogicalOffset + ext.Length <= er.FileOffset)
        {
            ReadEncrypted(crypto, ext, within, dest);
            return;
        }
        if (ext.LogicalOffset >= er.FileOffset)
            throw new ApfsException(ApfsErrorCode.Unsupported,
                CoreText.T("Volume.EncryptionChangeUnreadable", CoreText.T("Er.ExtentAhead", ext.LogicalOffset, er.FileOffset)));
        // İlerleme ofsetini içeren extent: [0, split) şifreli, [split, ...) henüz düz
        ulong split = er.FileOffset - ext.LogicalOffset;
        if (within < split)
        {
            int enc = (int)Math.Min((ulong)dest.Length, split - within);
            ReadEncrypted(crypto, ext, within, dest[..enc]);
            within += (ulong)enc;
            dest = dest[enc..];
        }
        if (!dest.IsEmpty)
        {
            uint bs = _volume.Reader.BlockSize;
            _volume.Reader.ReadBlocks(ext.PhysBlock + within / bs, (long)(within % bs), dest);
        }
    }

    /// <summary>
    /// Şifreli extent: okuma 512 baytlık XTS birimlerine genişletilir, her birim tweak = crypto_id × (blok/512) + birim sırası ile
    /// çözülür ve istenen bölüm kopyalanır.
    /// </summary>
    private void ReadEncrypted(Crypto.VolumeCrypto crypto, FileExtent ext, ulong within, Span<byte> dest)
    {
        const int unit = Crypto.AesXts.UnitSize;
        ulong firstUnit = within / unit;
        ulong end = within + (ulong)dest.Length;
        ulong lastUnitEnd = (end + unit - 1) / unit * unit;
        const int chunkUnits = 2048; // 1 MiB'lık parçalarla
        var buf = new byte[(int)Math.Min(lastUnitEnd - firstUnit * unit, (ulong)chunkUnits * unit)];
        uint bs = _volume.Reader.BlockSize;
        for (ulong u = firstUnit; u * unit < end; u += chunkUnits)
        {
            ulong start = u * unit;
            int len = (int)Math.Min((ulong)buf.Length, lastUnitEnd - start);
            var span = buf.AsSpan(0, len);
            _volume.Reader.ReadBlocks(ext.PhysBlock + start / bs, (long)(start % bs), span);
            crypto.DecryptData(span, ext.CryptoId, u);
            ulong copyFrom = Math.Max(start, within);
            ulong copyTo = Math.Min(start + (ulong)len, end);
            span.Slice((int)(copyFrom - start), (int)(copyTo - copyFrom)).CopyTo(dest[(int)(copyFrom - within)..]);
        }
    }

    /// <summary>İkili arama: offset'i içeren veya ondan sonraki ilk extent'in sırası.</summary>
    private int FindExtent(ulong offset)
    {
        int lo = 0, hi = _extents.Length - 1, ans = _extents.Length;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_extents[mid].LogicalOffset + _extents[mid].Length > offset) { ans = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return ans;
    }
}

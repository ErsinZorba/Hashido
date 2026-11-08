using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Apfs.Compression;
using Apfs.Container;
using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Mount;
using Apfs.Util;
using Apfs.Volume;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>
/// Klon ve macOS'un sıkıştırdığı dosyalar bu sürümde içerik olarak salt okunurdur. Yazılabilir sürücüde yazma erişimiyle
/// (FILE_WRITE_DATA / FILE_APPEND_DATA) açılışları Windows'un READONLY dosyasındaki gibi "erişim engellendi" ile reddedilmeli
/// (önceden açılış başarılı oluyor, ilk yazma "Bad file descriptor" ile düşüyordu); okuma, ad değiştirme, silme, zaman ve
/// gizli bayrağı çalışmaya devam eder.
/// </summary>
[Collection("WinFsp")]
public class ReadOnlyContentMountTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    private const int Bs = 4096;
    private const ulong RootDir = 2;
    private static readonly TreeShape PlainFsShape = new(ApfsConstants.TypeFsTree, ApfsConstants.ObjVirtual, 0, 0, KeyOrder.Fs, Bs);

    private static readonly byte[] CloneData = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("klon içeriği ğüşıöç ", 700)));
    private static readonly byte[] CompressedText = "macOS sıkıştırdı: öznitelik içi veri\r\n"u8.ToArray();

    /// <summary>
    /// mk-ci-64m'de üç dosya oluşturur, sonra kök yaprağı yerinde yamalar: klon-b klon-a ile aynı veri akışını paylaşır
    /// (private_id + dstream alanı, dstream_id refcnt 2 — motorun klon tanımı), sıkistirilmis.txt UF_COMPRESSED + öznitelik içi
    /// decmpfs (tür 1) taşır. Yamalı görüntü tam denetimden ve yazma kapısından geçer.
    /// </summary>
    private (string Path, WriteGrant Grant) PrepareImage()
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, "mk-ci-64m");
        ulong a = 0, b = 0, z = 0;
        FsOpsTests.Run(path, grant, (_, v) =>
        {
            a = v.CreateFile(RootDir, "klon-a.bin");
            v.Write(a, 0, CloneData);
            b = v.CreateFile(RootDir, "klon-b.bin");
            z = v.CreateFile(RootDir, "sikistirilmis.txt");
        });

        using (var c = TestImages.Open(path))
        {
            var vol = c.OpenVolumes()[0].Volume!;
            Assert.Equal(0, vol.FsTree.Height); // tek yapraklı kök
            PatchRootLeaf(path, vol.Omap.Resolve(vol.RootTreeOid, c.Xid).Paddr, n =>
            {
                var ia = InodeRecord.Parse(n.Vals[IndexOf(n, Records.Key(a, JType.Inode))]!);
                int ib = IndexOf(n, Records.Key(b, JType.Inode));
                var inb = InodeRecord.Parse(n.Vals[ib]!);
                inb.PrivateId = ia.PrivateId;
                var ds = ia.Find(Records.XtDstream)!;
                inb.Set(ds.Type, ds.Flags, ds.Data);
                n.Vals[ib] = inb.Encode();
                n.Vals[IndexOf(n, Records.Key(ia.PrivateId, JType.DstreamId))] = Records.DstreamIdVal(2);

                int iz = IndexOf(n, Records.Key(z, JType.Inode));
                var inz = InodeRecord.Parse(n.Vals[iz]!);
                inz.BsdFlags |= Inode.BsdFlagCompressed;
                n.Vals[iz] = inz.Encode();
                var attr = new byte[Decmpfs.HeaderSize + CompressedText.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(attr, Decmpfs.Magic);
                BinaryPrimitives.WriteUInt32LittleEndian(attr.AsSpan(4), 1);                    // tür 1: sıkıştırılmamış, öznitelik içi
                BinaryPrimitives.WriteUInt64LittleEndian(attr.AsSpan(8), (ulong)CompressedText.Length);
                CompressedText.CopyTo(attr, Decmpfs.HeaderSize);
                var val = new byte[4 + attr.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(val, ApfsVolume.XattrDataEmbedded);
                BinaryPrimitives.WriteUInt16LittleEndian(val.AsSpan(2), (ushort)attr.Length);
                attr.CopyTo(val, 4);
                Insert(n, Records.XattrKey(z, "com.apple.decmpfs"), val);
            });
        }
        FsOpsTests.AssertCleanFull(path);
        using var c2 = TestImages.Open(path);
        var g = WriteGate.Evaluate(c2, null);
        Assert.True(g.Allowed, string.Join("; ", g.Reasons));
        return (path, g.Grant!);
    }

    [Fact]
    public void CloneAndCompressedFilesRefuseWriteAccessButStayUsable()
    {
        var (path, grant) = PrepareImage();
        var errors = new List<string>();
        using (var dev = new FileImageWriter(path, grant))
        {
            var s = WriteSession.Open(dev, grant);
            using var m = ApfsWritableMount.Mount(s, 0, null, e => { lock (errors) errors.Add(e); });
            string root = m.MountPoint + "\\";
            foreach (string name in new[] { "klon-a.bin", "klon-b.bin", "sikistirilmis.txt" })
            {
                string f = root + name;
                // Okuma açılışı ve okuma çalışır
                using (var r = new FileStream(f, FileMode.Open, FileAccess.Read)) Assert.True(r.Length > 0);
                // Yazma erişimi açılışta reddedilir (önceden açılış geçiyor, yazma "Bad file descriptor" veriyordu)
                Assert.Throws<UnauthorizedAccessException>(() => new FileStream(f, FileMode.Open, FileAccess.ReadWrite).Dispose());
                Assert.Throws<UnauthorizedAccessException>(() => new FileStream(f, FileMode.Open, FileAccess.Write).Dispose());
                Assert.Throws<UnauthorizedAccessException>(() => File.AppendAllText(f, "ek"));
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(f, "üzerine"));       // CREATE_ALWAYS → Overwrite
            }
            Assert.Equal(CloneData, File.ReadAllBytes(root + "klon-a.bin"));
            Assert.Equal(CloneData, File.ReadAllBytes(root + "klon-b.bin"));
            Assert.Equal(CompressedText, File.ReadAllBytes(root + "sikistirilmis.txt"));
            File.Copy(root + "klon-a.bin", root + "kopya.bin");                                       // kaynak olarak kopyalanır

            // İçerik dışı işlemler serbest
            File.Move(root + "klon-a.bin", root + "klon-a-yeni.bin");
            File.SetLastWriteTimeUtc(root + "sikistirilmis.txt", new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc));
            File.SetAttributes(root + "klon-a-yeni.bin", FileAttributes.Hidden);
            File.Delete(root + "klon-b.bin");
            // Sıradan dosya hâlâ yazılabilir
            using (var w = new FileStream(root + "kopya.bin", FileMode.Open, FileAccess.ReadWrite)) w.Write("yazılabilir"u8);
        }
        Assert.Empty(errors);

        FsOpsTests.AssertCleanFull(path);
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        Assert.Equal(CloneData, ReaderTests.ReadAll(v, v.Lookup("klon-a-yeni.bin")));
        Assert.True(v.Lookup("klon-a-yeni.bin").IsHidden);
        Assert.Throws<ApfsException>(() => v.Lookup("klon-b.bin"));
        Assert.Equal(CompressedText, ReaderTests.ReadAll(v, v.Lookup("sikistirilmis.txt")));
        Assert.Equal(new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc), Inode.ToDateTime(v.Lookup("sikistirilmis.txt").ModTimeNs));
        Assert.Equal("yazılabilir"u8.ToArray(), ReaderTests.ReadAll(v, v.Lookup("kopya.bin"))[.."yazılabilir"u8.Length]);
    }

    /// <summary>
    /// 1.4.0 P2.7: MAXIMUM_ALLOWED ile açan uygulama (ör. bazı düzenleyiciler, eşitleme araçları) NTFS'teki READONLY dosyada
    /// olduğu gibi açabilmeli; açılış reddedilmez, yalnız o tanıtıcıdan içerik değişikliği (yazma, boyut) erişim engellendi
    /// alır. Yazma erişimini AÇIKÇA isteyen açılışın reddi (yukarıdaki test) değişmez.
    /// </summary>
    [Fact]
    public void MaximumAllowedOpensReadOnlyContentButContentChangesAreDenied()
    {
        var (path, grant) = PrepareImage();
        var errors = new List<string>();
        using (var dev = new FileImageWriter(path, grant))
        {
            var s = WriteSession.Open(dev, grant);
            using var m = ApfsWritableMount.Mount(s, 0, null, e => { lock (errors) errors.Add(e); });
            string root = m.MountPoint + "\\";
            foreach (var (name, data) in new[] { ("klon-a.bin", CloneData), ("klon-b.bin", CloneData), ("sikistirilmis.txt", CompressedText) })
            {
                using var h = CreateFileW(root + name, MaximumAllowed, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                Assert.False(h.IsInvalid, $"{name}: MAXIMUM_ALLOWED açılışı reddedildi (hata {Marshal.GetLastPInvokeError()})");
                var buf = new byte[data.Length + 16];
                Assert.True(ReadFile(h, buf, buf.Length, out int read, IntPtr.Zero), $"{name}: okunamadı");
                Assert.Equal(data, buf[..read]);
                Assert.False(WriteFile(h, "x"u8.ToArray(), 1, out _, IntPtr.Zero), $"{name}: yazma geçti");
                Assert.Equal(5, Marshal.GetLastPInvokeError()); // ERROR_ACCESS_DENIED
                Assert.False(SetEndOfFile(h), $"{name}: boyut değişti");
                Assert.Equal(5, Marshal.GetLastPInvokeError());
            }
            // Sıradan dosyada MAXIMUM_ALLOWED yazma hakkı verir (yani yukarıdaki ret dosyaya özgü)
            File.WriteAllText(root + "duz.txt", "düz");
            using (var h = CreateFileW(root + "duz.txt", MaximumAllowed, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                Assert.False(h.IsInvalid);
                Assert.True(WriteFile(h, "D"u8.ToArray(), 1, out int w, IntPtr.Zero) && w == 1);
            }
            // Açıkça yazma isteyen açılışlar hâlâ açılışta reddedilir
            Assert.Throws<UnauthorizedAccessException>(() => new FileStream(root + "klon-a.bin", FileMode.Open, FileAccess.ReadWrite).Dispose());
            using (var h = CreateFileW(root + "sikistirilmis.txt", GenericWrite, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
                Assert.True(h.IsInvalid);
            using (var h = CreateFileW(root + "klon-b.bin", MaximumAllowed | FileAppendData, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
                Assert.True(h.IsInvalid);
        }
        Assert.Empty(errors);
        FsOpsTests.AssertCleanFull(path);
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        Assert.Equal(CloneData, ReaderTests.ReadAll(v, v.Lookup("klon-a.bin")));
        Assert.Equal(CloneData, ReaderTests.ReadAll(v, v.Lookup("klon-b.bin")));
        Assert.Equal(CompressedText, ReaderTests.ReadAll(v, v.Lookup("sikistirilmis.txt")));
        Assert.Equal((byte)'D', ReaderTests.ReadAll(v, v.Lookup("duz.txt"))[0]); // sıradan dosyaya MAXIMUM_ALLOWED tanıtıcısıyla yazıldı
    }

    private const uint MaximumAllowed = 0x02000000, GenericWrite = 0x40000000, FileAppendData = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, byte[] buffer, int count, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEndOfFile(SafeFileHandle h);

    private static int IndexOf(MNode n, byte[] key)
    {
        for (int i = 0; i < n.Count; i++)
            if (KeyOrder.Fs(n.Keys[i], key) == 0) return i;
        throw new InvalidOperationException("kayıt yok");
    }

    private static void Insert(MNode n, byte[] key, byte[] val)
    {
        int at = 0;
        while (at < n.Count && KeyOrder.Fs(n.Keys[at], key) < 0) at++;
        n.Keys.Insert(at, key);
        n.Vals.Insert(at, val);
        BitConverter.TryWriteBytes(n.Footer!.AsSpan(24), Bin.U64(n.Footer!, 24) + 1);                      // kayıt sayısı
        BitConverter.TryWriteBytes(n.Footer!.AsSpan(16), Math.Max(Bin.U32(n.Footer!, 16), (uint)key.Length)); // en uzun anahtar
        BitConverter.TryWriteBytes(n.Footer!.AsSpan(20), Math.Max(Bin.U32(n.Footer!, 20), (uint)val.Length)); // en uzun değer
    }

    private static void PatchRootLeaf(string path, ulong paddr, Action<MNode> change)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var b = new byte[Bs];
        fs.Position = (long)paddr * Bs;
        fs.ReadExactly(b);
        Assert.True(Fletcher64.Verify(b));
        var n = NodeCodec.Parse(b, PlainFsShape, isRoot: true);
        change(n);
        var o = NodeCodec.Serialize(n, PlainFsShape, isRoot: true, Bin.U64(b, 8), Bin.U64(b, 16));
        fs.Position = (long)paddr * Bs;
        fs.Write(o);
    }
}

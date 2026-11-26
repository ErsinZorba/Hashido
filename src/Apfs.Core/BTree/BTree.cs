using Apfs.Container;
using Apfs.Util;

namespace Apfs.BTree;

/// <summary>B-ağacı yaprağındaki bir kayıt (anahtar ve değer kopyaları).</summary>
public readonly record struct BTreeEntry(byte[] Key, byte[] Value);

/// <summary>
/// Salt okunur APFS B-ağacı (btree_node_phys_t). Hem fiziksel (omap) hem sanal (fs-tree) ağaçları okur.
/// Güvenlik: düğüm seviyesi her inişte tam olarak bir azalmalıdır (döngü imkânsız), derinlik en fazla 16'dır. Bir tarama aynı
/// düğüme (fiziksel adres) ikinci kez inmez: paylaşılan çocuk (DAG) bozuk veridir — yoksa her işaret çocuğu yeniden gezdirir ve
/// düzey başına ~1000 işaretle tek çözümleme üstel büyür (1.8.1). Aynı oid'in farklı xid'li anahtarlarının birden
/// çok çocuğa yayılması meşrudur; yasak olan aynı DÜĞÜME iki kez inmek.
/// </summary>
public sealed class BTree
{
    public const int MaxDepth = 16;

    private readonly ObjectReader _reader;
    private readonly Func<ulong, (ulong Paddr, bool Encrypted)>? _resolveVirtual;
    private readonly Crypto.VolumeCrypto? _crypto;
    private readonly ulong _rootPaddr;
    private readonly bool _rootEncrypted;
    private readonly uint _subtype;
    private readonly int _fixedKeySize;
    private readonly int _fixedValSize;

    /// <param name="rootPaddr">Kök düğümün fiziksel adresi.</param>
    /// <param name="subtype">Ağacın türü (ör. OMAP, FSTREE); düğümlerin o_subtype alanı ile eşleşmelidir.</param>
    /// <param name="resolveVirtual">Sanal ağaçlarda çocuk düğüm oid'lerini fiziksel adrese (ve şifreli olup olmadığına) çevirir; fiziksel ağaçlarda null.</param>
    /// <param name="crypto">Şifreli birimde düğümleri çözen nesne; omap'te şifreli işaretli düğümler için kullanılır.</param>
    public BTree(ObjectReader reader, ulong rootPaddr, uint subtype, Func<ulong, (ulong Paddr, bool Encrypted)>? resolveVirtual,
        bool rootEncrypted = false, Crypto.VolumeCrypto? crypto = null)
    {
        _reader = reader;
        _rootPaddr = rootPaddr;
        _subtype = subtype;
        _resolveVirtual = resolveVirtual;
        _crypto = crypto;
        _rootEncrypted = rootEncrypted;
        if (rootEncrypted && crypto is null)
            throw new ApfsException(ApfsErrorCode.Encrypted, CoreText.T("BTree.NoKeyForEncryptedNode"));

        var root = _reader.ReadObject(rootPaddr, ApfsConstants.TypeBTree, decrypt: rootEncrypted ? crypto : null);
        CheckSubtype(root, rootPaddr);
        if ((Bin.U16(root, 32) & ApfsConstants.BtNodeRoot) == 0)
            throw ApfsException.Corrupt(CoreText.T("BTree.RootNotMarked", rootPaddr));
        int info = root.Length - ApfsConstants.BTreeInfoSize;
        uint nodeSize = Bin.U32(root, info + 4);
        if (nodeSize != reader.BlockSize)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("BTree.NodeSizeMismatch", nodeSize));
        _fixedKeySize = (int)Bin.U32(root, info + 8);
        _fixedValSize = (int)Bin.U32(root, info + 12);
        Height = Bin.U16(root, 34);
    }

    /// <summary>Kök düğümün seviyesi (0 = tek yapraklı ağaç).</summary>
    public int Height { get; }

    /// <summary>
    /// Sıralı tarama. <paramref name="compare"/> bir anahtar için: aralıktan küçükse &lt;0, aralıktaysa 0, büyükse &gt;0 döndürür.
    /// Karşılaştırma yalnızca anahtarın sıralama önekine bakmalıdır.
    /// </summary>
    /// <param name="includeGhosts">Değeri olmayan (hayalet) kayıtlar da boş değerle döndürülsün mü (ör. alan yöneticisinin boş
    /// kuyruğunda tek bloklu kayıtlar böyle saklanır).</param>
    public IEnumerable<BTreeEntry> Scan(Func<ReadOnlySpan<byte>, int> compare, bool includeGhosts = false) =>
        ScanNode(_rootPaddr, _rootEncrypted, ApfsConstants.TypeBTree, expectedLevel: -1, compare, depth: 0, includeGhosts, visited: null);

    /// <summary>
    /// Çocuğa inmeden önce: bu taramada aynı düğüme ikinci kez inilmez (<see cref="BTree"/> özeti). Küme kökün iç düğüm olduğu
    /// taramada kurulur (tek yapraklı ağaçta ve yaprakta hiç ayrılmaz); kök de içindedir.
    /// </summary>
    private static void Visit(ref HashSet<ulong>? visited, ulong parentPaddr, ulong childPaddr)
    {
        visited ??= [parentPaddr];
        if (!visited.Add(childPaddr)) throw ApfsException.Corrupt(CoreText.T("BTree.SharedChild", childPaddr));
    }

    // visited: yineleyici gövdesinde (her numaralandırmada yeniden) kökte null başlar; aynı Scan sonucunu iki kez gezmek ayrı küme kullanır
    private IEnumerable<BTreeEntry> ScanNode(ulong paddr, bool encrypted, uint objType, int expectedLevel, Func<ReadOnlySpan<byte>, int> compare,
        int depth, bool includeGhosts, HashSet<ulong>? visited)
    {
        if (depth > MaxDepth)
            throw ApfsException.Corrupt(CoreText.T("BTree.TooDeep"));

        if (encrypted && _crypto is null)
            throw new ApfsException(ApfsErrorCode.Encrypted, CoreText.T("BTree.NoKeyForEncryptedNode"));
        var data = _reader.ReadObject(paddr, objType, decrypt: encrypted ? _crypto : null);
        CheckSubtype(data, paddr);
        var node = Node.Parse(data, paddr, _fixedKeySize, _fixedValSize);
        if (expectedLevel >= 0 && node.Level != expectedLevel)
            throw ApfsException.Corrupt(CoreText.T("BTree.LevelInconsistent", paddr));
        bool isLeaf = (node.Flags & ApfsConstants.BtNodeLeaf) != 0;
        if (isLeaf != (node.Level == 0))
            throw ApfsException.Corrupt(CoreText.T("BTree.LeafFlagLevelMismatch", paddr));

        if (isLeaf)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var e = node.Entry(i, leaf: true);
                int c = compare(data.AsSpan(e.KeyOff, e.KeyLen));
                if (c < 0) continue;
                if (c > 0) yield break;
                if (e.ValLen < 0)
                {
                    // değeri olmayan (hayalet) kayıt
                    if (includeGhosts) yield return new BTreeEntry(data.AsSpan(e.KeyOff, e.KeyLen).ToArray(), []);
                    continue;
                }
                yield return new BTreeEntry(data.AsSpan(e.KeyOff, e.KeyLen).ToArray(), data.AsSpan(e.ValOff, e.ValLen).ToArray());
            }
            yield break;
        }

        for (int i = 0; i < node.Count; i++)
        {
            var e = node.Entry(i, leaf: false);
            if (compare(data.AsSpan(e.KeyOff, e.KeyLen)) > 0) yield break; // bu çocuk ve sonrakiler aralığın ötesinde
            if (i + 1 < node.Count)
            {
                var next = node.Entry(i + 1, leaf: false);
                if (compare(data.AsSpan(next.KeyOff, next.KeyLen)) < 0) continue; // bu çocuğun tamamı aralıktan küçük
            }
            if (e.ValLen < 8) throw ApfsException.Corrupt(CoreText.T("BTree.InvalidIndexValue", paddr));
            ulong childOid = Bin.U64(data, e.ValOff);
            var (childPaddr, childEncrypted) = _resolveVirtual is null ? (childOid, false) : _resolveVirtual(childOid);
            Visit(ref visited, paddr, childPaddr);
            foreach (var r in ScanNode(childPaddr, childEncrypted, ApfsConstants.TypeBTreeNode, node.Level - 1, compare, depth + 1, includeGhosts, visited))
                yield return r;
        }
    }

    /// <summary>Ağaçtaki bir düğüm (tam denetleyici için). <paramref name="ObjType"/>: o_type alanının tamamı (bayraklar dahil, çözülmüş).</summary>
    internal readonly record struct NodeVisit(ulong Paddr, ulong Oid, int Level, int Count, uint ObjType);

    /// <summary>
    /// Ağacın bütün düğümlerini (kök dahil) derinlik öncelikli gezer. Okuma yolunun kullandığı denetimlerin aynısı uygulanır
    /// (seviye, yaprak bayrağı, alt tür, derinlik, paylaşılan çocuk).
    /// </summary>
    /// <param name="order">Verilirse anahtar sırası da denetlenir (bkz. <see cref="KeyOrderCheck"/>); gezinti tamamlanınca sonuç orada.</param>
    internal IEnumerable<NodeVisit> EnumerateNodes(KeyOrderCheck? order = null) =>
        EnumerateNode(_rootPaddr, _rootEncrypted, ApfsConstants.TypeBTree, expectedLevel: -1, depth: 0, visited: null, order);

    /// <summary>
    /// Anahtar sırası (1.9.1; apfsck btree.c parse_subtree): derinlik öncelikli gezintide görülen anahtarlar hiç azalmaz,
    /// yaprakta kesin artar (tekrar yok) ve kök dışı her düğümün ilk anahtarı üstteki ayraçla (iç düğüm anahtarı) aynıdır. Okuyucunun
    /// ikili/sıralı araması ve yazıcının ekleme yeri bu sıraya dayanır: bozuk sıralı yaprakta kayıt sessizce "yok" görünür.
    /// </summary>
    internal sealed class KeyOrderCheck(KeyComparison compare)
    {
        private const int MaxProblems = 5;
        private byte[]? _lastData;
        private int _lastOff, _lastLen;

        public List<(KeyOrderProblem Kind, ulong Paddr)> Problems { get; } = [];
        public int ProblemCount { get; private set; }

        internal void See(byte[] data, int off, int len, ulong paddr, bool firstInNonRoot, bool leaf, bool firstInNode)
        {
            if (_lastData is not null)
            {
                int c = compare(_lastData.AsSpan(_lastOff, _lastLen), data.AsSpan(off, len));
                if (c > 0) Add(KeyOrderProblem.OutOfOrder, paddr);
                else if (firstInNonRoot && c != 0) Add(KeyOrderProblem.IndexKeyNotFirst, paddr);
                else if (leaf && !firstInNode && c == 0) Add(KeyOrderProblem.Repeated, paddr);
            }
            _lastData = data; _lastOff = off; _lastLen = len;
        }

        private void Add(KeyOrderProblem kind, ulong paddr)
        {
            if (ProblemCount++ < MaxProblems) Problems.Add((kind, paddr));
        }
    }

    internal enum KeyOrderProblem { OutOfOrder, IndexKeyNotFirst, Repeated }

    /// <summary>İki anahtarın ağaç sırasıyla karşılaştırması (&lt;0, 0, &gt;0).</summary>
    internal delegate int KeyComparison(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);

    private IEnumerable<NodeVisit> EnumerateNode(ulong paddr, bool encrypted, uint objType, int expectedLevel, int depth, HashSet<ulong>? visited,
        KeyOrderCheck? order)
    {
        if (depth > MaxDepth)
            throw ApfsException.Corrupt(CoreText.T("BTree.TooDeep"));
        if (encrypted && _crypto is null)
            throw new ApfsException(ApfsErrorCode.Encrypted, CoreText.T("BTree.NoKeyForEncryptedNode"));
        var data = _reader.ReadObject(paddr, objType, decrypt: encrypted ? _crypto : null);
        CheckSubtype(data, paddr);
        var node = Node.Parse(data, paddr, _fixedKeySize, _fixedValSize);
        if (expectedLevel >= 0 && node.Level != expectedLevel)
            throw ApfsException.Corrupt(CoreText.T("BTree.LevelInconsistent", paddr));
        bool isLeaf = (node.Flags & ApfsConstants.BtNodeLeaf) != 0;
        if (isLeaf != (node.Level == 0))
            throw ApfsException.Corrupt(CoreText.T("BTree.LeafFlagLevelMismatch", paddr));
        yield return new NodeVisit(paddr, Bin.U64(data, 8), node.Level, node.Count, Bin.U32(data, 24));
        bool nonRoot = (node.Flags & ApfsConstants.BtNodeRoot) == 0;
        if (isLeaf)
        {
            if (order is not null)
                for (int i = 0; i < node.Count; i++)
                {
                    var k = node.Entry(i, leaf: true);
                    order.See(data, k.KeyOff, k.KeyLen, paddr, firstInNonRoot: i == 0 && nonRoot, leaf: true, firstInNode: i == 0);
                }
            yield break;
        }
        for (int i = 0; i < node.Count; i++)
        {
            var e = node.Entry(i, leaf: false);
            order?.See(data, e.KeyOff, e.KeyLen, paddr, firstInNonRoot: i == 0 && nonRoot, leaf: false, firstInNode: i == 0);
            if (e.ValLen < 8) throw ApfsException.Corrupt(CoreText.T("BTree.InvalidIndexValue", paddr));
            ulong childOid = Bin.U64(data, e.ValOff);
            var (childPaddr, childEncrypted) = _resolveVirtual is null ? (childOid, false) : _resolveVirtual(childOid);
            Visit(ref visited, paddr, childPaddr);
            foreach (var v in EnumerateNode(childPaddr, childEncrypted, ApfsConstants.TypeBTreeNode, node.Level - 1, depth + 1, visited, order))
                yield return v;
        }
    }

    private void CheckSubtype(byte[] data, ulong paddr)
    {
        uint subtype = Bin.U32(data, 28) & ApfsConstants.ObjectTypeMask;
        if (_subtype != 0 && subtype != _subtype)
            throw ApfsException.Corrupt(CoreText.T("BTree.UnexpectedSubtype", paddr));
    }

    private readonly record struct EntryLoc(int KeyOff, int KeyLen, int ValOff, int ValLen);

    /// <summary>Tek bir düğümün ayrıştırılmış başlığı.</summary>
    private readonly struct Node
    {
        private readonly byte[] _data;
        private readonly int _tocStart, _keyStart, _valEnd, _keySize, _valSize;
        private readonly bool _fixed;

        public ushort Flags { get; }
        public int Level { get; }
        public int Count { get; }

        private Node(byte[] data, ushort flags, int level, int count, int tocStart, int keyStart, int valEnd, int keySize, int valSize, bool isFixed)
        {
            _data = data; Flags = flags; Level = level; Count = count;
            _tocStart = tocStart; _keyStart = keyStart; _valEnd = valEnd;
            _keySize = keySize; _valSize = valSize; _fixed = isFixed;
        }

        public static Node Parse(byte[] data, ulong paddr, int keySize, int valSize)
        {
            ushort flags = Bin.U16(data, 32);
            ushort level = Bin.U16(data, 34);
            uint nkeys = Bin.U32(data, 36);
            ushort tocOff = Bin.U16(data, 40);
            ushort tocLen = Bin.U16(data, 42);
            bool isRoot = (flags & ApfsConstants.BtNodeRoot) != 0;
            bool isFixed = (flags & ApfsConstants.BtNodeFixedKvSize) != 0;

            if (level >= MaxDepth)
                throw ApfsException.Corrupt(CoreText.T("BTree.InvalidLevel", paddr));
            int tocStart = ApfsConstants.BTreeNodeHeaderSize + tocOff;
            int keyStart = tocStart + tocLen;
            int valEnd = data.Length - (isRoot ? ApfsConstants.BTreeInfoSize : 0);
            int entrySize = isFixed ? 4 : 8;
            if (keyStart > valEnd || (long)nkeys * entrySize > tocLen)
                throw ApfsException.Corrupt(CoreText.T("BTree.InvalidToc", paddr));
            if (isFixed && (keySize <= 0 || keySize > data.Length || valSize < 0 || valSize > data.Length))
                throw ApfsException.Corrupt(CoreText.T("BTree.InvalidFixedKvSize", paddr));

            return new Node(data, flags, level, (int)nkeys, tocStart, keyStart, valEnd, keySize, valSize, isFixed);
        }

        /// <summary>i. kaydın konumu. Değeri olmayan kayıtta ValLen = -1.</summary>
        public EntryLoc Entry(int i, bool leaf)
        {
            int kOff, kLen, vOff, vLen;
            if (_fixed)
            {
                int t = _tocStart + i * 4;
                kOff = Bin.U16(_data, t);
                vOff = Bin.U16(_data, t + 2);
                kLen = _keySize;
                vLen = leaf ? _valSize : 8;
            }
            else
            {
                int t = _tocStart + i * 8;
                kOff = Bin.U16(_data, t);
                kLen = Bin.U16(_data, t + 2);
                vOff = Bin.U16(_data, t + 4);
                vLen = Bin.U16(_data, t + 6);
            }
            int kStart = _keyStart + kOff;
            if (kStart + kLen > _valEnd)
                throw ApfsException.Corrupt(CoreText.T("BTree.KeyOutOfNode"));
            if (vOff == 0xFFFF)
                return new EntryLoc(kStart, kLen, 0, -1);
            int vStart = _valEnd - vOff;
            if (vStart < _keyStart || vStart + vLen > _valEnd)
                throw ApfsException.Corrupt(CoreText.T("BTree.ValueOutOfNode"));
            return new EntryLoc(kStart, kLen, vStart, vLen);
        }
    }
}

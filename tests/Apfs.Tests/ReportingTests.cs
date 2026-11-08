using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Apfs.Container;
using Apfs.Diagnostics;
using Apfs.Volume;
using Apfs.Windows.Reporting;

namespace Apfs.Tests;

/// <summary>
/// 1.4.0 P2.2–P2.3: rapor olayları fikstürlerden üretilir; hepsi ortak şemaya (worker/schema.json) uyar ve kişiyi/diski tanıtan
/// hiçbir şey taşımaz (Türkçe adlı fikstürlerin birim/dosya/klasör adları, UUID'ler, yollar, makine ve kullanıcı adı).
/// </summary>
public partial class ReportingTests(LinuxFixtures fixtures, TestImages images) : IClassFixture<LinuxFixtures>, IClassFixture<TestImages>
{
    // ---------------- Gizlilik denetimi (uçtan uca testler de kullanır) ----------------

    /// <summary>Bir görüntüdeki tanıtıcı her şey: birim adları, bütün dosya/klasör adları, kapsayıcı ve birim UUID'leri.</summary>
    internal static List<string> Identifying(ApfsContainer c, IEnumerable<Crypto.VolumeKey>? keys = null, string? password = null)
    {
        var list = new List<string> { c.Superblock.Uuid.ToString() };
        foreach (var r in c.OpenVolumes(password is null ? null : _ => password, keys))
        {
            list.Add(c.VolumeUuid(c.Superblock.FsOids[r.Index]).ToString());
            if (r.Volume is not { } v) continue;
            list.Add(v.Name);
            var stack = new Stack<ulong>([Inode.RootDirId]);
            while (stack.Count > 0)
                foreach (var e in v.ListDirectory(stack.Pop()))
                {
                    list.Add(e.Name);
                    if (v.GetInode(e.FileId).IsDirectory) stack.Push(e.FileId);
                }
        }
        return list;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])")]
    private static partial Regex UuidLike();

    /// <summary>
    /// Olay şemaya uyar ve şunları içermez: verilen tanıtıcı metinler (4+ karakter; UUID'ler her biçimde), UUID biçimli her şey,
    /// yol (\ ya da /), makine/kullanıcı adı, geçici klasör.
    /// </summary>
    internal static void AssertPrivate(JsonNode e, IEnumerable<string> identifying)
    {
        Assert.Empty(ReportSchema.ValidateEvent(e));
        string json = e.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var forbidden = new List<string> { Environment.MachineName, Environment.UserName, Path.GetTempPath().TrimEnd('\\') };
        foreach (var s in identifying)
        {
            forbidden.Add(s);
            if (Guid.TryParse(s, out var g)) forbidden.AddRange([g.ToString("N"), g.ToString("D").ToUpperInvariant(), g.ToString("N").ToUpperInvariant()]);
        }
        // Şemanın sabit simgeleri (ör. rol "preboot") tanıtıcı değildir: "Preboot" adlı birim onlarla karışmasın
        var tokens = Strings(ReportSchema.Definitions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in forbidden.Where(f => f.Length >= 4 && !tokens.Contains(f)).Distinct())
            Assert.False(json.Contains(f, StringComparison.OrdinalIgnoreCase), $"olayda tanıtıcı metin var: {f}\n{json}");
        Assert.DoesNotMatch(UuidLike(), json);
        foreach (var v in Strings(e))
        {
            Assert.DoesNotContain('\\', v);
            Assert.DoesNotContain('/', v);
        }
    }

    private static IEnumerable<string> Strings(JsonNode? n) => n switch
    {
        JsonObject o => o.SelectMany(p => Strings(p.Value)),
        JsonArray a => a.SelectMany(Strings),
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => [],
    };

    /// <summary>Hizmetin üretebileceği her olay türü, verilen kapsayıcının gerçek bilgileriyle.</summary>
    internal static List<JsonObject> AllEventKinds(ContainerFacts facts)
    {
        var disk = new DiskFacts("usb", 512, "gpt", 1);
        var t = TimeSpan.FromMilliseconds(1234);
        return
        [
            ReportEvents.Create("mount", "ok", "service").WithDisk(disk, facts).WithVolumeCounts(1, 0, 0, 0).WithDuration(t),
            ReportEvents.Create("mount", "fail", "service").WithDisk(disk, facts).WithVolumeCounts(0, 0, 0, 1).WithCode("Corrupt").WithDuration(t),
            ReportEvents.Create("unlock", "fail", "service").WithDisk(disk, facts).WithCode("WrongPassword").WithDuration(t),
            ReportEvents.Create("write-enable", "refused", "service").WithDisk(disk, facts).WithGate(["Gate.Snapshots", "Write.MultipleVolumes"]).WithDuration(t),
            ReportEvents.Create("write-session", "ok", "service").WithDisk(disk, facts).WithWrite("read-only", new WriteCounters(3, 1_000_000, 42)).WithDuration(t),
            ReportEvents.Create("eject", "busy", "service").WithDisk(disk, null),
            ReportEvents.Create("disk-changed", "abandoned", "service").WithDisk(disk, null),
        ];
    }

    public static TheoryData<string> PrivacyFixtures() => ["pop-ci-64m", "mk-cs-64m", "enc-pop-ci-64m", "unicode", "unicode-cs", "multivolume", "encrypted"];

    [Theory]
    [MemberData(nameof(PrivacyFixtures))]
    public void EventsFromTurkishNamedFixturesCarryNoNamesUuidsOrPaths(string name)
    {
        string path = LinuxFixtures.Names.Contains(name) || name.StartsWith("enc-", StringComparison.Ordinal) ? fixtures.Get(name) : images.Get(name).Path;
        using var c = TestImages.Open(path);
        var keys = name.StartsWith("enc-", StringComparison.Ordinal) ? TestKeys.Of(c) : null;
        var identifying = Identifying(c, keys, name == "encrypted" ? Apfs.ImageBuilder.Scenarios.EncryptedPassword : null);
        identifying.Add(path);
        Assert.Contains(identifying, s => s.IndexOfAny("çğıöşüÇĞİÖŞÜ".ToCharArray()) >= 0 || name is "mk-cs-64m" or "multivolume");
        var facts = ContainerFacts.Of(c);
        Assert.Equal(c.Superblock.FsOids.Count, facts.Volumes.Count);
        foreach (var e in AllEventKinds(facts)) AssertPrivate(e, identifying);
    }

    [Fact]
    public void CrashEventHasTypeAndOwnFramesButNoMessageOrPath()
    {
        Exception caught;
        try { ThrowWithPersonalMessage(); throw new InvalidOperationException(); }
        catch (Exception ex) { caught = ex; }
        var e = ReportEvents.Crash(caught, "tray");
        AssertPrivate(e, ["Ayşe'nin Diski", FakeProfile + @"\Belgeler\gizli.txt", "gizli.txt"]);
        var crash = e["crash"]!.AsObject();
        Assert.Equal(typeof(IOException).FullName, crash["exType"]!.GetValue<string>());
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, crash["innerType"]!.GetValue<string>());
        // test derlemesi "Apfs.Tests": kendi çerçevemiz görünür, System/xunit çerçeveleri görünmez
        var frames = crash["frames"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        Assert.Contains(frames, f => f.StartsWith("Apfs.Tests.ReportingTests.ThrowWithPersonalMessage+0x", StringComparison.Ordinal));
        Assert.All(frames, f => Assert.StartsWith("Apfs.", f));
    }

    /// <summary>
    /// Sahte kullanıcı profili yolu (C sürücüsünde Users\ayşe). Parçalı yazılır: açık depoya aktarma betiği (publish-public.ps1)
    /// kaynakta kullanıcı profili yolu geçen her dosyayı gerçek yol sızıntısı sayıp durdurur.
    /// </summary>
    internal const string FakeProfile = @"C:\" + @"Users\ayşe";

    private static void ThrowWithPersonalMessage() =>
        throw new IOException("Ayşe'nin Diski: " + FakeProfile + @"\Belgeler\gizli.txt okunamadı", new UnauthorizedAccessException("gizli.txt"));

    // ---------------- Şema ----------------

    [Fact]
    public void SchemaRejectsUnknownFieldsBadPatternsAndOutOfRangeNumbers()
    {
        JsonObject ok;
        using (var c = OpenFixture("mk-ci-64m")) ok = AllEventKinds(ContainerFacts.Of(c))[0];
        Assert.Empty(ReportSchema.ValidateEvent(ok));

        JsonObject With(Action<JsonObject> change) { var x = ok.DeepClone().AsObject(); change(x); return x; }
        Assert.Contains("event.volumeName: unknown", ReportSchema.ValidateEvent(With(x => x["volumeName"] = "Belgeler")));
        Assert.Contains("event.disk.volumes[0].name: unknown", ReportSchema.ValidateEvent(With(x => x["disk"]!["volumes"]![0]!["name"] = "Belgeler")));
        Assert.Contains("event.code: pattern", ReportSchema.ValidateEvent(With(x => x["code"] = FakeProfile)));
        Assert.Contains("event.code: pattern", ReportSchema.ValidateEvent(With(x => x["code"] = "Corrupt\n")));
        Assert.Contains("event.type: enum", ReportSchema.ValidateEvent(With(x => x["type"] = "keylogger")));
        Assert.Contains("event.disk.sizeLog2: maximum", ReportSchema.ValidateEvent(With(x => x["disk"]!["sizeLog2"] = 65)));
        Assert.Contains("event.disk.sizeLog2: type", ReportSchema.ValidateEvent(With(x => x["disk"]!["sizeLog2"] = 1.5)));
        Assert.Contains("event.disk.volumes[0].lastWriter: pattern",
            ReportSchema.ValidateEvent(With(x => x["disk"]!["volumes"]![0]!["lastWriter"] = "Ayşe's MacBook (1.0)")));
        Assert.Contains("event.v: const", ReportSchema.ValidateEvent(With(x => x["v"] = 2)));
        Assert.Contains("event.day: required", ReportSchema.ValidateEvent(With(x => x.Remove("day"))));

        var envelope = new JsonObject { ["events"] = new JsonArray(ok.DeepClone()) };
        Assert.Empty(ReportSchema.ValidateEnvelope(envelope));
        envelope["machine"] = "x";
        Assert.Contains("envelope.machine: unknown", ReportSchema.ValidateEnvelope(envelope));
    }

    [Fact]
    public void SchemaFileIsTheEmbeddedOne()
    {
        // Worker ile ortak dosya: derlemeye gömülen kopya depodakiyle birebir
        string onDisk = File.ReadAllText(Path.Combine(RepoRoot(), "worker", "schema.json"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(onDisk), ReportSchema.Definitions));
    }

    internal static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "APFS-for-Windows.sln"))) return d.FullName;
        throw new InvalidOperationException("depo kökü bulunamadı");
    }

    // ---------------- Yapı bilgileri ----------------

    private Apfs.Container.ApfsContainer OpenFixture(string name) => TestImages.Open(fixtures.Get(name));

    [Fact]
    public void FactsDescribeStructureOfFixtures()
    {
        using (var c = OpenFixture("mk-ci-64m"))
        {
            var f = ContainerFacts.Of(c);
            Assert.Equal(4096u, f.BlockSize);
            Assert.Equal(c.Superblock.BlockCount * 4096, f.ByteSize);
            Assert.Equal(27, ReportEvents.Log2(f.ByteSize)); // 64 MiB = 2^26 → 27 basamak
            var v = Assert.Single(f.Volumes);
            Assert.False(v.CaseSensitive);
            Assert.Equal("none", v.Encryption);
            Assert.False(v.HasSnapshots);
            Assert.Null(v.LastWriter); // mkapfs'in kimliği ("mkapfs by …") kalıba uymuyor → atılır
        }
        using (var c = OpenFixture("mk-cs-64m")) Assert.True(Assert.Single(ContainerFacts.Of(c).Volumes).CaseSensitive);
        using (var c = OpenFixture("enc-pop-ci-64m"))
        {
            var f = ContainerFacts.Of(c);
            Assert.True(f.SoftwareCrypto);
            Assert.Equal("onekey", Assert.Single(f.Volumes).Encryption);
        }
        using (var c = TestImages.Open(images.Get("multivolume").Path))
        {
            var f = ContainerFacts.Of(c);
            Assert.Equal(4, f.Volumes.Count);
            Assert.Contains(f.Volumes, v => v.Role != "none");
        }
    }

    [Fact]
    public void FactsSeeSnapshotsAndOurOwnWriterId()
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, "mk-ci-64m");
        FsOpsTests.Run(path, grant, (s, v) => v.CreateFile(Inode.RootDirId, "rapor.txt"));
        using (var c = TestImages.Open(path))
            Assert.Matches(@"^apfs-for-windows \d+\.\d+\.\d+$", Assert.Single(ContainerFacts.Of(c).Volumes).LastWriter!);

        string snap = fixtures.Copy("mk-ci-64m");
        PatchVolume(snap, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(216), 1));
        using (var c = TestImages.Open(snap)) Assert.True(Assert.Single(ContainerFacts.Of(c).Volumes).HasSnapshots);
    }

    private static void PatchVolume(string path, Action<byte[]> change)
    {
        ulong paddr;
        using (var c = TestImages.Open(path)) paddr = c.Omap.Resolve(c.Superblock.FsOids[0], c.Xid).Paddr;
        FullCheckTests.PatchObject(path, paddr, change);
    }

    [Fact]
    public void Log2RangesAreBitLengths()
    {
        Assert.Equal(0, ReportEvents.Log2(0UL));
        Assert.Equal(1, ReportEvents.Log2(1UL));
        Assert.Equal(2, ReportEvents.Log2(3UL));
        Assert.Equal(3, ReportEvents.Log2(4UL));
        Assert.Equal(64, ReportEvents.Log2(ulong.MaxValue));
        Assert.Equal(0, ReportEvents.Log2(-5L));
        Assert.Equal(11, ReportEvents.DurationLog2(TimeSpan.FromMilliseconds(1234)));
        Assert.Equal("2026-11-07", ReportEvents.Day(new DateTime(2026, 11, 7, 23, 59, 0, DateTimeKind.Utc)));
    }

    // ---------------- Kuyruk ve kayıt ----------------

    private static string TempDir() => Directory.CreateTempSubdirectory("apfs-reports-").FullName;

    private static JsonObject Sample(int i) => ReportEvents.Create("eject", "ok", "service").WithCode("N" + i);

    [Fact]
    public void QueueDropsOldestBeyondLimitsAndPagesAndAcks()
    {
        var q = new ReportQueue(Path.Combine(TempDir(), "queue.jsonl"));
        for (int i = 1; i <= ReportQueue.MaxEvents + 5; i++) q.Enqueue(Sample(i));
        Assert.Equal(ReportQueue.MaxEvents, q.Count);
        var (all, more) = q.Pull(0, int.MaxValue);
        Assert.False(more);
        Assert.Equal("N6", all[0].Event.GetProperty("code").GetString()); // en eski 5 olay düştü
        Assert.Equal("N205", all[^1].Event.GetProperty("code").GetString());

        // Sayfalama: küçük sınırla her çekişte birkaç olay, hepsi sırayla ve eksiksiz gelir
        long after = 0;
        var seen = new List<string>();
        int pages = 0;
        while (true)
        {
            var (page, m) = q.Pull(after, 2_000);
            Assert.NotEmpty(page);
            seen.AddRange(page.Select(p => p.Event.GetProperty("code").GetString()!));
            after = page[^1].Id;
            pages++;
            if (!m) break;
        }
        Assert.True(pages > 10, $"{pages} sayfa");
        Assert.Equal(all.Select(a => a.Event.GetProperty("code").GetString()), seen);

        q.Ack(all[99].Id);
        Assert.Equal(ReportQueue.MaxEvents - 100, q.Count);
        q.Ack(after);
        Assert.Equal(0, q.Count);
        q.Enqueue(Sample(1));
        Assert.True(q.Pull(0, int.MaxValue).Reports[0].Id > after, "sıra numarası boşalan kuyrukta geri gitmemeli");
    }

    [Fact]
    public void QueueRespectsByteLimit()
    {
        var q = new ReportQueue(Path.Combine(TempDir(), "queue.jsonl"));
        var big = ReportEvents.Crash(new IOException(), "service");
        big["crash"]!["frames"] = new JsonArray([.. Enumerable.Range(0, 20).Select(i => JsonValue.Create($"Apfs.{new string('X', 150)}.M{i}+0x1"))]);
        for (int i = 0; i < ReportQueue.MaxEvents; i++) q.Enqueue(big);
        Assert.True(new FileInfo(q.PathName).Length <= ReportQueue.MaxBytes + 4096, new FileInfo(q.PathName).Length.ToString());
        Assert.InRange(q.Count, 1, ReportQueue.MaxEvents - 1);
    }

    [Fact]
    public void RecorderIsOffByDefaultValidatesAndClearsWhenTurnedOff()
    {
        string dir = TempDir();
        var r = new ReportRecorder(dir);
        Assert.False(r.Enabled);
        r.Record(() => Sample(1));
        Assert.Equal(0, r.Queue.Count); // yapılandırma yok → hiçbir şey toplanmaz

        r.SetEnabled(true);
        r.Record(() => Sample(2));
        r.Record(() => Sample(3).WithCode("bad code with spaces")); // şemaya uymuyor → düşer
        r.Record(() => throw new InvalidOperationException("olay kurulamadı")); // raporlama asıl işi engellemez
        Assert.Equal(1, r.Queue.Count);
        Assert.True(new ReportRecorder(dir).Enabled); // seçim kalıcı

        r.SetEnabled(false);
        Assert.False(File.Exists(r.Queue.PathName));
        r.Record(() => Sample(4));
        Assert.Equal(0, r.Queue.Count);
        Assert.False(new ReportRecorder(dir).Enabled);
    }
}

using System.Text.Json;
using S2Port.Extraction;
using S2Port.Extraction.Smb3;
using SonicBros3.Audio;
using SonicBros3.Logic;

namespace SonicBros3.Tests;

public class ConversionTests
{
    [Fact]
    public void Catalog_ContainsAllWorldsAndBothPlayableRecipes()
    {
        if (Smb3Rom.Image is not { } rom) return;
        var catalog = Smb3Catalog.Read(rom);
        Assert.Equal(Enumerable.Range(1, 8), catalog.Worlds.Select(w => w.Number));
        Assert.Equal(257, catalog.Areas.Length);
        Assert.Equal(catalog.Areas.Length, catalog.Areas.Select(a => a.Id).Distinct().Count());
        var ids = catalog.Areas.Select(a => a.Id).ToHashSet();
        foreach (var world in catalog.Worlds)
        {
            Assert.Equal(0, world.Map.Length % 144);
            Assert.All(world.Entries.Where(e => e.AreaId is not null), e => Assert.Contains(e.AreaId!, ids));
        }
        foreach (var spec in Smb3Levels.All)
        {
            var area = Assert.Single(catalog.Areas, a => a.Tileset == spec.Tileset &&
                a.LayoutBank == spec.Layout.Bank && a.LayoutAddress == spec.Layout.Addr &&
                a.ObjectAddress == spec.Objects.Addr);
            Assert.Contains(catalog.Worlds[0].Entries, e => e.AreaId == area.Id);
        }
    }

    [Theory]
    [InlineData("1-1")]
    [InlineData("1-2")]
    public void OriginalLoader_DispatchMatchesPortedLoader(string name)
    {
        if (Smb3Rom.Image is not { } rom) return;
        var spec = Smb3Levels.All.Single(l => l.Name == name);
        var area = new Smb3Area(name, spec.Tileset, spec.Layout.Bank, spec.Layout.Addr,
            spec.Objects.Bank, spec.Objects.Addr);
        var original = new Smb3DecodedArea(rom, area);
        var ported = new Smb3LevelDecoder(rom, spec.Layout, spec.Tileset);
        Convert.FromHexString(Smb3DecodedArea.Seed).CopyTo(ported.RandomPool, 0);
        ported.Load();
        int cells = (ported.Width + 1) * Smb3Map.ScreenSize;
        Assert.Equal(ported.TileMem[..cells].ToArray(), original.TileMemory[..cells]);
        Assert.Equal(ported.Width, original.Width);
        Assert.False(original.Vertical);
        Assert.Equal(ported.Tile(25, 8), original.Tile(25, 8));
    }

    [Fact]
    public void ObjectMappings_PreserveSubtypesOffsetsAndUnknownIds()
    {
        var redKoopa = Smb3ObjectConverter.Convert(new(0x6D, 3, 4));
        Assert.NotNull(redKoopa);
        Assert.Equal((56, 64, 0xE3, 1), (redKoopa.X, redKoopa.Y, redKoopa.Id, redKoopa.Subtype));
        var goal = Smb3ObjectConverter.Convert(new(0x41, 2, 3));
        Assert.NotNull(goal);
        Assert.Equal(64, goal.Y);
        Assert.Null(Smb3ObjectConverter.Convert(new(0xFF, 0, 0)));
    }

    [Fact]
    public void UnsupportedAndTruncatedImagesAreRejected()
    {
        byte[] header = new byte[16];
        "NES\u001A"u8.CopyTo(header);
        header[4] = 1;
        Assert.Throws<InvalidDataException>(() => new NesRom(header));
        var rom = SyntheticRom();
        Assert.Throws<InvalidDataException>(() => Smb3Catalog.Read(rom));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x11)]
    [InlineData(0xD0)]
    public void Music_RejectsInvalidSet2BSelectors(int selector)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Smb3Music(SyntheticRom()).Start(selector));
    }

    [Fact]
    public void Report_NeverCallsIntermediateDataComplete()
    {
        var report = new Smb3ConversionReport(Smb3Conversion.Version, "test", Extractor.Version, 8,
            [new("test", "decoded", [], [])], [], []);
        Assert.False(report.Complete);
        var roundTrip = JsonSerializer.Deserialize<Smb3ConversionReport>(
            JsonSerializer.Serialize(report, Manifest.JsonOptions), Manifest.JsonOptions);
        Assert.Equal("decoded", roundTrip!.Areas[0].Status);
        Assert.False(roundTrip.Complete);
    }

    [Fact]
    public void ObjectReader_RejectsUnterminatedBankInsteadOfWrapping()
    {
        Assert.Throws<InvalidDataException>(() => Smb3ObjectConverter.Read(SyntheticRom(), 0, 0xC000));
        Assert.Throws<InvalidDataException>(() => Smb3ObjectConverter.Read(SyntheticRom(), 0, 0x8000));
    }

    [Fact]
    public void BatchReport_EverySuccessfulAreaHasItsDeclaredOutputs()
    {
        if (TestData.Dir is not { } data) return;
        var report = JsonSerializer.Deserialize<Smb3ConversionReport>(
            DataFile.ReadAllText(Path.Combine(data, Smb3Conversion.ReportPath)), Manifest.JsonOptions)!;
        Assert.Equal(Smb3Conversion.Version, report.Version);
        Assert.Equal(8, report.WorldCount);
        Assert.False(report.Complete);
        Assert.Equal(2, report.Areas.Count(a => a.Status == "engine-ready"));
        var partial = Assert.Single(report.Areas, a => a.Status == "native-partial");
        Assert.Equal("ts01-15-AE09-C2EE", partial.Id);
        Assert.Equal([0x07], partial.UnmappedObjects);
        Assert.Contains(partial.Blockers, b => b.Contains("warp-whistle"));
        Assert.All(report.Areas, area =>
        {
            Assert.True(DataFile.Exists(Path.Combine(data, "conversion", "areas", area.Id, "objects.json")));
            if (area.Status == "failed") { Assert.NotEmpty(area.Blockers); return; }
            string root = Path.Combine(data, "conversion", "areas", area.Id);
            Assert.Equal(Smb3Map.TileMemSize, DataFile.ReadAllBytes(Path.Combine(root, "tiles.bin")).Length);
            using var metadata = JsonDocument.Parse(DataFile.ReadAllText(Path.Combine(root, "area.json")));
            string layout = metadata.RootElement.GetProperty("tileLayout").GetString()!;
            Assert.True(DataFile.Exists(Path.GetFullPath(Path.Combine(root, layout))));
            using var graphics = JsonDocument.Parse(DataFile.ReadAllText(Path.Combine(root, "graphics.json")));
            int bank = graphics.RootElement.GetProperty("backgroundBanks")[0].GetInt32();
            Assert.Equal(64 * 32, DataFile.ReadAllBytes(Path.Combine(data, "conversion", "graphics", $"chr_{bank:X2}_p0.bin")).Length);
        });
    }

    [Fact]
    public void ConvertedLibrary_All27SongsLoadAndRunOnNativeDriver()
    {
        if (TestData.Dir is not { } data) return;
        string reportPath = Path.Combine(data, Smb3Conversion.ReportPath);
        Assert.True(DataFile.Exists(reportPath), "Run tools/ConvertSmb3 before integration tests.");
        var report = JsonSerializer.Deserialize<Smb3ConversionReport>(DataFile.ReadAllText(reportPath), Manifest.JsonOptions)!;
        Assert.Equal(27, report.Songs.Length);
        Assert.All(report.Songs, song =>
        {
            Assert.Equal("converted", song.Status);
            Assert.True(song.IntroFrames + song.LoopFrames > 0);
            string manifest = Path.Combine(data, "conversion", "music", $"set{song.Set}_{song.Selector:X2}.json");
            var ram = new Ram();
            var chips = new CountingChips();
            var driver = new Smps68kDriver(ram, new Smps68kData(data, manifest), chips);
            driver.Update();
            int initialWrites = chips.Writes;
            ram.W8(Vars.SmpsQueue1, Smb3ZoneBuilder.MusicId);
            for (int i = 0; i < song.IntroFrames + song.LoopFrames + 120; i++) driver.Update();
            Assert.True(chips.Writes - initialWrites > 100, $"Song {song.Set}/${song.Selector:X2} did not play.");
        });
    }

    private static NesRom SyntheticRom()
    {
        byte[] data = new byte[16 + 0x4000];
        "NES\u001A"u8.CopyTo(data);
        data[4] = 1;
        return new(data);
    }

    private sealed class CountingChips : ISoundChips
    {
        public int Writes { get; private set; }
        public void WriteYm(int part, int register, int value) => Writes++;
        public void WritePsg(int value) => Writes++;
        public void PlayDac(int sample) => Writes++;
        public void SetDacPitch(int pitch) { }
        public void PlaySegaPcm() { }
    }
}

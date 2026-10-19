using S2Port.Extraction;
using S2Port.Extraction.Levels;
using S2Port.Extraction.Smb3;
using SonicBros3.Audio;
using SonicBros3.Frontend;
using SonicBros3.Logic;
using SonicBros3.Runtime;

namespace SonicBros3.Tests;

public class CampaignTests
{
    internal static string? Archive
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string archive = Path.Combine(dir.FullName, "web-data", "campaign.bin");
                if (File.Exists(archive)) return archive;
            }
            return null;
        }
    }

    [Fact]
    public void Batch_PreservesAllSourceIdentitiesAndReportsUnavailableAreas()
    {
        if (Archive is not { } archive) return;
        var campaign = Smb3Campaign.Load(archive)!;
        Assert.Equal(8, campaign.Worlds.Length);
        Assert.Equal(19, campaign.Worlds.Sum(w => w.Map.Length / 144));
        Assert.Equal(257, campaign.Areas.Length);
        Assert.Equal(257, campaign.Areas.Select(a => a.Act).Distinct().Count());
        Assert.Equal(257, campaign.Areas.Select(a => a.Source.Id).Distinct().Count());
        Assert.Equal(255, campaign.Areas.Count(a => a.Status == "native-partial"));
        Assert.Contains(campaign.Areas, a => a.Source.Id == "ts01-15-A4F9-C166" && a.Status == "failed");
        Assert.Contains(campaign.Areas, a => a.Height > 2048 && a.Status == "blocked");
        Assert.False(campaign.Complete);
        Assert.NotEmpty(campaign.Blockers);
        foreach (var area in campaign.Areas)
        {
            if (area.Status is "failed" or "blocked")
                Assert.Throws<InvalidDataException>(() => LevelLoader.Load(archive, Smb3Campaign.Zone, area.Act));
            else
                Assert.True(DataFile.Exists(Path.Combine(LevelLoader.ActDir(archive, Smb3Campaign.Zone, area.Act), "level.json")));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelLoader.ActDir(archive, Smb3Campaign.Zone, 258));
    }

    [Fact]
    public void EveryNativeArea_LoadsStepsAndRendersWithoutZoneOrActTruncation()
    {
        if (Archive is not { } archive) return;
        var campaign = Smb3Campaign.Load(archive)!;
        var acts = new HashSet<int>();
        foreach (var area in campaign.Areas.Where(a => a.Status == "native-partial"))
        {
            var session = new LevelSession(archive, Smb3Campaign.Zone, area.Act);
            for (int frame = 0; frame < 180; frame++) session.Step(0);
            session.Render();
            Assert.Null(session.StopMessage);
            Assert.Equal((area.Act - 1) & 255, session.World.Ram.U16(Vars.CurrentZoneAndAct) & 255);
            Assert.Equal((Smb3Campaign.Zone, area.Act), session.CurrentAct);
            Assert.Equal(0, session.World.Ram.U16(Vars.LevelHasSignpost));
            Assert.InRange(session.World.CameraX, 0, session.Level.Bounds.MaxX);
            Assert.NotNull(session.Level.MusicFile);
            acts.Add(area.Act);
        }
        Assert.Equal(255, acts.Count);
        Assert.Contains(257, acts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EveryWorld_HasItsOwnMapsAndNativeAreaWithOriginalAudio(int number)
    {
        if (Archive is not { } archive) return;
        var campaign = Smb3Campaign.Load(archive)!;
        var world = campaign.Worlds.Single(w => w.Number == number);
        for (int screen = 0; screen < world.Map.Length / 144; screen++)
        {
            var map = new GameFlow(archive, null, GameFlow.Start.Map, startWorld: number, startMapScreen: screen);
            for (int f = 0; f < 180; f++) map.Step(0);
            map.Render();
            Assert.IsType<WorldMapScreen>(map.Screen);
            Assert.Equal(number, map.Progress.World);
            Assert.Equal(screen, map.Progress.MapScreen);
            var rgba = new uint[Vdp.Width * Vdp.Height];
            map.Vdp.ToRgba(rgba);
            Assert.True(rgba.Distinct().Count() > 8);
        }
        var ids = world.Entries.Select(e => e.AreaId).ToHashSet();
        var area = campaign.Areas.First(a => ids.Contains(a.Source.Id) && a.Status == "native-partial");
        var mixer = new SoundMixer(archive);
        var game = new GameFlow(archive, mixer, GameFlow.Start.Level,
            startZone: Smb3Campaign.Zone, startAct: area.Act);
        var samples = new short[1600];
        long energy = 0;
        for (int f = 0; f < 240; f++)
        {
            game.Step(0);
            mixer.Render(samples, 800);
            foreach (short sample in samples) energy += Math.Abs((int)sample);
        }
        game.Render();
        Assert.Null(game.StopMessage);
        Assert.True(energy > 1_000_000, $"World {number} music energy: {energy}.");
    }

    [Fact]
    public void CampaignGoal_ReturnsToItsSourceMapWithCardAndCompletion()
    {
        if (Archive is not { } archive) return;
        var campaign = Smb3Campaign.Load(archive)!;
        var area = campaign.Areas.Single(a => a.Source.Id == "ts01-15-AE09-C2EE");
        var game = new GameFlow(archive, null, GameFlow.Start.Level, (2350, 396),
            Smb3Campaign.Zone, area.Act);
        for (int f = 0; f < 400; f++) game.Step(0);
        for (int f = 0; f < 40; f++) game.Step(8);
        for (int f = 0; f < 8; f++) game.Step(0x48);
        bool courseClear = false, gotCard = false;
        for (int f = 0; f < 1200 && game.Session is { } session; f++)
        {
            game.Step(8);
            var ram = session.World.Ram;
            for (int slot = Objects.FirstDynamicSlot; slot < Vars.DynamicObjectRamEnd; slot += Objects.SlotSize)
            {
                var obj = new SonicObject(ram, slot);
                if (obj.Id != RouletteGoal.TextId) continue;
                courseClear |= obj.MappingFrame == 3;
                gotCard |= obj.MappingFrame >= 4;
            }
            Assert.Null(game.StopMessage);
        }
        Assert.True(courseClear);
        Assert.True(gotCard);
        Assert.IsType<WorldMapScreen>(game.Screen);
        Assert.Equal(1, game.Progress.World);
        Assert.Equal(0, game.Progress.MapScreen);
        Assert.True(game.Progress.IsCleared(WorldMapScreen.Level13Tile));
        Assert.Single(game.Progress.Cards);
    }

    [Fact]
    public void MapCompletion_IsIsolatedByWorldAndScreen()
    {
        var progress = new MapProgress();
        progress.Cleared.Add(progress.PanelKey(4));
        Assert.True(progress.IsCleared(4));
        progress.MapScreen = 1;
        Assert.False(progress.IsCleared(4));
        progress.World = 2;
        progress.MapScreen = 0;
        Assert.False(progress.IsCleared(4));
    }
}

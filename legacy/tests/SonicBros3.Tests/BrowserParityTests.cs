using System.Security.Cryptography;
using SonicBros3.Audio;
using SonicBros3.Frontend;
using SonicBros3.Logic;
using SonicBros3.Runtime;

namespace SonicBros3.Tests;

public class BrowserParityTests
{
    [Theory]
    [InlineData(GameFlow.Start.Title, 0x40, 600, "FC6738061F78DD45C32FA098B6652EE9329ACC6E531D5AB925471F5B7567F800")]
    [InlineData(GameFlow.Start.Map, 0x40, 180, "5264759C00B99D84716C475ED87B510AFC1484C7170E5FB564B0DD3C6DCA8110")]
    [InlineData(GameFlow.Start.Match, 0x40, 180, "2E30874243E25229DD6BFE0106F6F48B788D7796FFC616FE37D42EF6D9438516")]
    [InlineData(GameFlow.Start.Level, 0x40, 180, "61BED55F1E296CD3D529365D8F27CA0E5AAC3324A545B2DF76CFAD92D8954BD5")]
    [InlineData(GameFlow.Start.Level, 0x41, 180, "4DEBA907A7E47285AD2E5B955DEF8ED0779CE1A74A8FA126D37EE2963C686025")]
    [InlineData(GameFlow.Start.Level, 0x42, 180, "C8CAB832C6DB7255B2EF0D8864FFFE2BAF6E18F1D90DD8948CBE1548B13BB881")]
    public void OriginalEngine_RendersAndProducesDeterministicFrames(GameFlow.Start start, int zone, int frames, string expected)
    {
        if (TestData.Dir is not { } data) return;
        var flow = new GameFlow(data, null, start, startZone: zone);
        for (int i = 0; i < frames; i++) flow.Step(0);
        flow.Render();
        var rgba = new uint[Vdp.Width * Vdp.Height];
        flow.Vdp.ToRgba(rgba);
        Assert.True(rgba.Distinct().Count() > 8);
        Assert.Null(flow.StopMessage);
        string hash = Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(rgba.AsSpan())));
        Assert.Equal(expected, hash);
    }

    [Theory]
    [InlineData(0x40)]
    [InlineData(0x41)]
    [InlineData(0x42)]
    public void OriginalLevelMusic_ProducesNonSilentStereoSamples(int zone)
    {
        if (TestData.Dir is not { } data) return;
        var mixer = new SoundMixer(data);
        var flow = new GameFlow(data, mixer, GameFlow.Start.Level, startZone: zone);
        var samples = new short[1600];
        long energy = 0;
        for (int i = 0; i < 180; i++)
        {
            flow.Step(0);
            mixer.Render(samples, 800);
            foreach (short sample in samples) energy += Math.Abs((int)sample);
        }
        Assert.True(energy > 1_000_000, $"Music energy was only {energy}.");
        Assert.Null(flow.StopMessage);
    }

    [Fact]
    public void OriginalGoal_ShowsBothMessagesBeforeReturningWithACard()
    {
        if (TestData.Dir is not { } data) return;
        var flow = new GameFlow(data, null, GameFlow.Start.Level, (2350, 396), 0x42);
        for (int i = 0; i < 400; i++) flow.Step(0);
        for (int i = 0; i < 40; i++) flow.Step(8);
        for (int i = 0; i < 8; i++) flow.Step(0x48);
        bool courseClear = false, gotCard = false;
        for (int i = 0; i < 1200 && flow.Session is { } session; i++)
        {
            flow.Step(8);
            var ram = session.World.Ram;
            for (int slot = Objects.FirstDynamicSlot; slot < Vars.DynamicObjectRamEnd; slot += Objects.SlotSize)
            {
                var obj = new SonicObject(ram, slot);
                if (obj.Id != RouletteGoal.TextId) continue;
                courseClear |= obj.MappingFrame == 3;
                gotCard |= obj.MappingFrame >= 4;
            }
            Assert.Null(flow.StopMessage);
        }
        Assert.True(courseClear);
        Assert.True(gotCard);
        Assert.IsType<WorldMapScreen>(flow.Screen);
        Assert.Single(flow.Progress.Cards);
        Assert.Contains(WorldMapScreen.Level13Tile, flow.Progress.Cleared);
    }

    [Fact]
    public void MapSonic_IsTwoDistinctMiniatureFrames()
    {
        var tiles = MiniSonicArt.Tiles();
        Assert.Equal(2 * 4 * 32, tiles.Length);
        Assert.False(tiles.AsSpan(0, 128).SequenceEqual(tiles.AsSpan(128)));
    }
}

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using SonicBros3.Audio;
using SonicBros3.Frontend;
using SonicBros3.Runtime;

[assembly: SupportedOSPlatform("browser")]

return;

public static partial class BrowserGame
{
    private static GameFlow? _flow;
    private static SoundMixer? _mixer;
    private static readonly uint[] Pixels = new uint[Vdp.Width * Vdp.Height];
    private static readonly byte[] Frame = new byte[Vdp.Width * Vdp.Height * 4];
    private static readonly short[] Samples = new short[800 * 2];
    private static readonly byte[] Audio = new byte[800 * 4];

    [JSExport]
    public static void Load(byte[] archive, string screen, int zone, int debugX, int debugY)
        => LoadGame(archive, screen, zone, 1, 1, 0, debugX, debugY);

    [JSExport]
    public static void LoadCampaign(byte[] archive, string screen, int act, int world, int mapScreen)
        => LoadGame(archive, screen, S2Port.Extraction.Smb3.Smb3Campaign.Zone, act, world, mapScreen, -1, -1);

    [JSExport]
    public static string Campaign() => System.Text.Json.JsonSerializer.Serialize(
        S2Port.Extraction.Smb3.Smb3Campaign.Load("/game/assets.bin"), S2Port.Extraction.Manifest.JsonOptions);

    private static void LoadGame(byte[] archive, string screen, int zone, int act, int world, int mapScreen, int debugX, int debugY)
    {
        Directory.CreateDirectory("/game");
        const string path = "/game/assets.bin";
        S2Port.Extraction.DataFile.Forget(path);
        File.WriteAllBytes(path, archive);
        _mixer = new SoundMixer(path);
        var start = screen switch
        {
            "title" => GameFlow.Start.Title,
            "map" => GameFlow.Start.Map,
            "level" => GameFlow.Start.Level,
            "match" => GameFlow.Start.Match,
            _ => throw new ArgumentException($"Unknown start screen: {screen}")
        };
        if (zone is < 0x40 or > 0x43) throw new ArgumentOutOfRangeException(nameof(zone));
        if (debugX > 65535 || debugY > 65535) throw new ArgumentOutOfRangeException(nameof(debugX));
        (int, int)? debugStart = debugX >= 0 && debugY >= 0 ? (debugX, debugY) : null;
        _flow = new GameFlow(path, _mixer, start, debugStart, startZone: zone, startAct: act,
            startWorld: world, startMapScreen: mapScreen);
    }

    [JSExport]
    public static void Step(int buttons)
    {
        var flow = _flow ?? throw new InvalidOperationException("Game assets have not loaded.");
        flow.Step(buttons);
        _mixer!.Render(Samples, 800);
        Buffer.BlockCopy(Samples, 0, Audio, 0, Audio.Length);
        if (flow.StopMessage is { } error) throw new InvalidOperationException(error);
    }

    [JSExport]
    public static byte[] Render()
    {
        var flow = _flow ?? throw new InvalidOperationException("Game assets have not loaded.");
        flow.Render();
        flow.Vdp.ToRgba(Pixels);
        Buffer.BlockCopy(Pixels, 0, Frame, 0, Frame.Length);
        return Frame;
    }

    [JSExport]
    public static byte[] Sound() => Audio;

    [JSExport]
    public static string Describe() => _flow?.Describe() ?? "loading";
}

namespace SonicBros3.Logic;

/// <summary>Bits of an object's render flags (<see cref="SonicObject.RenderFlags"/>, $01).</summary>
public static class Render
{
    public const int XFlip = 0x01, YFlip = 0x02;
    /// <summary>Position is in level coordinates (else screen coordinates, e.g. the HUD and title cards).</summary>
    public const int LevelCoords = 0x04;
    /// <summary>The on-screen test uses y_radius as the half height (else a fixed $20).</summary>
    public const int UseYRadius = 0x10;
    /// <summary>The mappings pointer points straight at one frame.</summary>
    public const int StaticMappings = 0x20;
    /// <summary>The slot draws several sprites (child positions at $10 onward).</summary>
    public const int MultiSprite = 0x40;
    /// <summary>Set by BuildSprites when the object was drawn.</summary>
    public const int OnScreen = 0x80;
}

/// <summary>Bits of an object's status (<see cref="SonicObject.Status"/>, $22) for non-player objects.</summary>
public static class ObjStatus
{
    public const int XFlip = 0x01, YFlip = 0x02;
    /// <summary>Player 1 (Sonic) / player 2 (Tails) standing on it. As bit numbers: 3 and 4.</summary>
    public const int StandingP1 = 0x08, StandingP2 = 0x10;
    /// <summary>Player 1 / player 2 pushing it. As bit numbers: 5 and 6.</summary>
    public const int PushingP1 = 0x20, PushingP2 = 0x40;
    /// <summary>Destroyed (e.g. a broken monitor).</summary>
    public const int Destroyed = 0x80;
}

namespace S2Port.Extraction;

/// <summary>
/// Addresses of data tables in the supported ROM. Each one was located through the code that uses it
/// (see docs/memory-map.md for the referencing instruction). They are specific to this ROM build.
/// </summary>
public static class RomMap
{
    /// <summary>SHA-1 of the only ROM build these addresses are valid for.</summary>
    public const string SupportedSha1 = "6651a52a04caeec071b8e469308caa5b5c4cdd2f";

    /// <summary>PalPointers: 8-byte entries {u32 src, u16 RAM dest, u16 longs-1}. Used by PalLoad at 0x2E26/0x2E42.</summary>
    /// <summary>Uncompressed object art the game copies to VRAM itself: (name, ROM address, tiles).</summary>
    public static readonly (string Name, int Address, int Tiles)[] StreamedObjectArt =
    [
        ("giant-ring", 0x1129F6, 0xC40 / 32),        // streamed by 0x43EE0 while $F7BE counts down
        ("giant-ring-flash", 0x113636, 0x54),        // 0x43F50
    ];

    /// <summary>
    /// Title card (LoadTitleCard 0x18D7C): base art (Nemesis, to tile $580), the letter font (Nemesis, to RAM) and the
    /// per-zone letter lists ({font tile, tiles} pairs, $FF-terminated) copied in order to tile $5DE; Obj34's zone-name
    /// frame per zone (0x17238).
    /// </summary>
    public const int TitleCardBaseArt = 0x847D2, TitleCardFont = 0x84B30, TitleCardLetterOffsets = 0x18DD8,
        TitleCardLetterLists = 0x18E12, TitleCardNameFrames = 0x17238, TitleCardBaseTile = 0x580, TitleCardLetterTile = 0x5DE;

    /// <summary>Raw data tables that object code reads by ROM address: (file under objects/, address, length).</summary>
    public static readonly (string File, int Address, int Length)[] ObjectDataTables =
    [
        ("mz-grass-platform/heights.json", 0x15343C, 0xC4),   // Obj2F's three height profiles (0x1531E8 points at them)
        ("mz-caterkiller/wave.json", 0x1551E8, 0x80),          // Obj78's body rise while inching (0x1551E8)
        ("lava-line/colours.json", 0x164662, 0x12),            // the hack's below-the-line colours 18-20 (three sets, 0x164470)
        ("mz-breakable/speeds.json", 0x27888, 0x38),           // Obj51's fragment speeds (0x27888 / 0x278A0 / 0x278B0)
        ("mz-collapsing-floor/delays.json", 0x139A9, 0x20),    // Obj53's piece delays (0x139A9 / 0x139B1 ...)
        ("lava-line/ghz-art-0.json", 0x1140B6, 0xAA0),         // Green Hill art DMA'd to tile $500 off the lava stretch
        ("lava-line/ghz-art-1.json", 0x114B56, 0xAA0),         // ... and on it (0x164CEC)
        ("signpost/level-order.json", 0x176B4, 0x74),          // next level per zone $00-$1C and act (0x17592)
        ("object-table.json", 0x199CA, 256 * 8),
        ("spiral/heights.json", 0x256BC, 0x1A0),       // Obj06 (Sonic 2 zones): the path height per pixel
        ("spiral/flip-angles.json", 0x25688, 0x34),    // Obj06: the flip angle per 8 px
        ("spring/diagonal-slopes.json", 0x1CD26, 0x38),        // Obj41's diagonal springs' slopes (up 0x1CD26, down 0x1CD42)
        ("spring/diagonal-slopes-edge.json", 0x24D25, 0x1D),   // the bytes SlopedSolid reads $7FFF past them at a flipped edge                // RunObjects' table: Sonic 2 zones' and hack zones' routine per id
    ];

    /// <summary>Marble Zone's animated-art routine (custom magma part) and the magma art it shifts.</summary>
    public const int MarbleAnimatedArtRoutine = 0x4423A, MarbleMagmaArt = 0x1114F6;

    /// <summary>Music per zone, played at level load (0x4E9A; the 2-player list at 0x4C0C).</summary>
    public const int LevelMusicList = 0x4BEE, LevelMusicList2P = 0x4C0C;

    public const int PalPointers = 0x2E96;
    public const int PalPointerCount = 0x35;

    /// <summary>Palette RAM base the PalPointers destinations are relative to ($FFFFFB00).</summary>
    public const int PaletteRamBase = 0xFB00;

    /// <summary>Palette loaded into lines 0-1 before the zone palette (moveq #3,d0 at 0x4E3C / 0x4F1C).</summary>
    public const int LevelBasePalette = 0x03;

    /// <summary>LevelArtPointers: 12-byte entries indexed by Current_Zone. Referenced at 0x4CB6, 0x63D2, 0x105E4.</summary>
    public const int LevelArtPointers = 0x46B78;
    public const int ZoneCount = 0x1D;

    /// <summary>
    /// PLC list offset table, signed word offsets (LoadPLC at 0x17F2). Each list: s16 count-1 (negative = empty),
    /// then {u32 Nemesis source, u16 VRAM address} per entry.
    /// </summary>
    public const int PlcOffsets = 0x46CD4;
    public const int PlcCount = 0x5E;

    /// <summary>
    /// The title card (object at $B080, 0x171FC) loads PLC 2 (standard HUD/ring art) and the zone's animals PLC from
    /// this byte table when it leaves.
    /// </summary>
    public const int TitleCardStandardPlc = 0x02, AnimalPlcTable = 0x1721A;

    /// <summary>PLC loaded by Level after the zone's first PLC (moveq #1,d0 at 0x4CD4).</summary>
    public const int LevelCommonPlc = 0x01;

    /// <summary>
    /// LoadAnimatedBlocks table (0x44810): word offsets per zone to {u16 dest offset from $FFFF9000 (0 = none),
    /// u16 words-1, words...}. Fills blocks at the top of the 0x300-block buffer.
    /// </summary>
    public const int AnimatedBlocks = 0x4488C;

    /// <summary>LoadCollisionIndexes (0x5C12): u32 Kosinski pointers per zone. Primary -> $FFFFD600, secondary -> $FFFFD900.</summary>
    public const int CollisionIndexPrimary = 0x5C3E;
    public const int CollisionIndexSecondary = 0x5CB2;
    public const int CollisionIndexLength = 0x300;

    /// <summary>
    /// Collision arrays read by the floor/wall routines (0x22670-0x22958). The hack adds an alternate set,
    /// selected by $FFFFE6, which Level (0x4C56) sets to 1 for zones >= <see cref="AlternateCollisionFirstZone"/>.
    /// </summary>
    public static readonly CollisionSet NormalCollision = new("normal", AngleMap: 0x477EA, Heights: 0x478EA, Widths: 0x488EA);
    public static readonly CollisionSet AlternateCollision = new("alternate", AngleMap: 0x4A518, Heights: 0x4A618, Widths: 0x4B618);
    public const int AlternateCollisionFirstZone = 0x11;

    public sealed record CollisionSet(string Name, int AngleMap, int Heights, int Widths);

    /// <summary>
    /// Off_Objects (ObjectsManager_Init, 0x1B844): signed word offsets indexed by zone*2+act to 6-byte records
    /// {u16 x, u16 y|flags, u8 id, u8 subtype}, ended by x = $FFFF. Spawn code: 0x1BCCA.
    /// </summary>
    public const int ObjectLayouts = 0xFC756;

    /// <summary>
    /// Off_Rings (RingsManager setup, 0x1B046): signed word offsets indexed by zone*2+act to 4-byte groups
    /// {u16 x, u16 y|count|direction}, ended by a negative x. Expanded and x-sorted into $FFFFE806.
    /// </summary>
    public const int RingLayouts = 0xF56EC;

    /// <summary>
    /// Animated level tiles (0x43F98): per zone {s16 routine, s16 script list} relative to this address.
    /// Script list: u16 count-1, then scripts {u8 duration (bit 7 = per-frame durations), u24 uncompressed art,
    /// u16 VRAM dest, u8 frames, u8 tiles per frame, frame bytes (tile offset[, duration]) padded to even}.
    /// Per-script state at $FFFFF7F0 + 2n: {timer, next frame}.
    /// </summary>
    public const int AnimatedTiles = 0x43FB8;
    public const int AnimatedTilesGenericRoutine = 0x443EC;
    public const int AnimatedTilesNullRoutine = 0x4402C;

    /// <summary>LevelSizeLoad (0xD784): {u16 minX, maxX, minY, maxY} per zone/act, index zone*16 + act*8.</summary>
    public const int LevelSizes = 0xD7C8;

    /// <summary>Start locations (0xD9AE): {u16 x, y} per zone/act, index zone*8 + act*4.</summary>
    public const int StartLocations = 0xDA04;

    /// <summary>Level layout offset table indexed by zone/act. Used at 0x106A0.</summary>
    public const int LevelLayoutOffsets = 0x4CC1A;

    /// <summary>Kosinski-compressed Z80 driver for the 68k-SMPS path (uploaded at 0x1490).</summary>
    public const int Z80DacDriver = 0x131576;

    /// <summary>Supplementary level art overlaid after the main art (LoadZoneTiles, 0x63F2-0x6432).</summary>
    public static readonly IReadOnlyList<ArtOverlay> ArtOverlays =
    [
        new(Zone: 0x07, Source: 0xA00E8, DestOffset: 0x3F80, FinalLength: 0x6FC0),
        new(Zone: 0x06, Source: 0xCF4F8, DestOffset: 0x60E0, FinalLength: 0x6F20),
        new(Zone: 0x0E, Source: null,    DestOffset: 0,      FinalLength: 0x64C0),
    ];

    /// <summary>Supplementary 16x16 block mappings (LevelDataLoad, 0x10604).</summary>
    public static readonly IReadOnlyList<(int Zone, int Source, int DestOffset)> BlockOverlays =
    [
        (0x07, 0x9FBD8, 0x980),
    ];

    /// <summary>Per-act palette overrides in LevelDataLoad (0x10678-0x1068C), keyed by zone/act word.</summary>
    public static readonly IReadOnlyDictionary<int, int> PaletteOverrides = new Dictionary<int, int>
    {
        [0x1B01] = 0x2F,
        [0x1801] = 0x30,
    };

    /// <summary>
    /// Retail Sonic 2 zone names for IDs 0x00-0x10, for orientation only. The hack may have
    /// re-skinned these; IDs 0x11+ are hack additions with no retail equivalent.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> RetailZoneHints = new Dictionary<int, string>
    {
        [0x00] = "EHZ", [0x01] = "unused1", [0x02] = "unused2(WZ)", [0x03] = "unused3",
        [0x04] = "MTZ", [0x05] = "MTZ3", [0x06] = "WFZ", [0x07] = "HTZ", [0x08] = "HPZ",
        [0x09] = "unused9", [0x0A] = "OOZ", [0x0B] = "MCZ", [0x0C] = "CNZ", [0x0D] = "CPZ",
        [0x0E] = "DEZ", [0x0F] = "ARZ", [0x10] = "SCZ",
    };

    /// <summary>Per-act 128x128 chunk overrides in LevelDataLoad (0x10648), keyed by zone/act word.</summary>
    public static readonly IReadOnlyDictionary<int, int> ChunkOverrides = new Dictionary<int, int>
    {
        [0x1501] = 0xDFDEB,
    };

    public sealed record ArtOverlay(int Zone, int? Source, int DestOffset, int FinalLength);

    /// <summary>
    /// Object sprite data, by the name the game uses: mapping table (S2 format: word offsets, then per frame a piece
    /// count and 8-byte pieces) and optional AnimateSprite script table (word offsets to {duration, frames..., command}).
    /// Addresses come from each object's init code (the move.l #mappings,4(a0) / lea anims,a1 instructions).
    /// </summary>
    public static readonly (string Name, int Mappings, int Animations, int Frames)[] ObjectSprites =
    [
        ("monitor", 0x15C7A, 0x15C12, 0),     // Obj26 0x155B0 / Obj2E 0x15798
        ("explosion", 0x25174, 0, 0),          // Obj27 0x25096
        ("motobug", 0x151A58, 0x151AF4, 0),    // hack Obj40 0x1518E6
        ("points", 0x14DD2, 0, 0),             // Obj29 0x14CC8
        ("lost-rings", 0x152C4, 0x152BC, 0),   // Obj37 0x14F78 (ring mappings shared with Obj25)
        ("bridge", 0x12710, 0, 0),             // Obj11 0x120E0, hack zones
        ("bridge-s2", 0x126F8, 0, 0),          // Obj11 in Sonic 2's zones (Emerald Hill's logs); zone 8 uses 0x126B0
        ("spikes", 0x194D4, 0, 0),             // hack Obj36 0x19306 (Sonic 1 spikes)
        ("animal-14D1E", 0x14D1E, 0, 0),       // Obj28 0x14778: mappings per animal type (table 0x147F4)
        ("animal-14D42", 0x14D42, 0, 0),
        ("animal-14D66", 0x14D66, 0, 0),
        ("animal-14D8A", 0x14D8A, 0, 0),
        ("animal-14DAE", 0x14DAE, 0, 0),
        ("spring", 0x1CD98, 0x1CD5E, 11),   // Obj41 0x1C604 (red; the table is followed by another)
        ("spring-yellow", 0x1CDAE, 0, 0),    // Obj41, subtype bit 1
        ("platform", 0x134C8, 0, 0),         // Obj18 0x13064, hack zones
        ("platform-s2", 0x13418, 0, 0),      // Obj18 in Sonic 2's zones (Emerald Hill's floating platforms)
        ("waterfall-s2", 0x24C5E, 0, 0),     // Obj49 in Sonic 2's zones (Emerald Hill's waterfalls)
        ("spikes-s2", 0x191A6, 0, 0),        // Obj36 in Sonic 2's zones (0x18F3E)
        ("buzzer", 0x3154E, 0x31532, 0),     // Obj4B in Sonic 2's zones (0x312D0)
        ("masher", 0x316A6, 0x31694, 0),     // Obj5C in Sonic 2's zones (0x315F8)
        ("coconuts", 0x3BF94, 0x3BF86, 0),   // Obj9D (0x3BDFA) and its coconut (Obj98)
        ("ehz-boss-ship", 0x33D24, 0x33CF4, 7),       // Obj56 (0x33154): the flying ship (bottom and cockpit)
        ("ehz-boss-car", 0x33C84, 0x33C70, 8),        // Obj56: ground car, drill, wheels
        ("ehz-boss-propeller", 0x33B9C, 0x33B62, 7),  // Obj56: the propeller
        ("ledge", 0x139FE, 0, 0),            // Obj1A 0x13586 (collapsing ledge)
        ("rock", 0x19604, 0, 0),             // hack Obj3B 0x19574
        ("swing", 0x12E28, 0, 0),            // Obj15 0x12750, hack zones
        ("edge-wall", 0x15187A, 0, 0),       // hack Obj44 0x1517FA (Sonic 1 Green Hill edge walls)
        ("crabmeat", 0x152166, 0x152242, 0), // hack Obj1F 0x151F10
        ("buzz-bomber", 0x151C88, 0x151DC0, 0), // hack Obj22 0x151B12
        ("chopper", 0x152314, 0x15232C, 0),  // hack Obj2B 0x152278
        ("newtron", 0x1524FE, 0x15261A, 0),  // hack Obj42 0x15233E
        ("starpost", 0x23320, 0x23310, 0),   // Obj79 0x22FAC
        ("game-over", 0x1820E, 0, 0),        // Obj39 0x17256
        ("title-card", 0x17C3E, 0, 0),       // Obj34 0x16EC0
        ("mz-grass-platform", 0x153500, 0, 0), // hack Obj2F 0x1531D6 (Sonic 1 MZ large grassy platform)
        ("mz-fire", 0x1536E8, 0x153730, 0),  // hack Obj19 0x1535F4 (fire on the grass)
        ("mz-lava-ball", 0x1536E8, 0x153908, 0), // hack Obj14 0x153738 (same mappings, its own animations)
        ("mz-brick", 0x153B1E, 0, 0),        // hack Obj46 0x1539EE
        ("mz-glass-block", 0x153D7E, 0, 0),  // hack Obj30 0x153B2A
        ("mz-stomper", 0x154182, 0, 0),      // hack Obj31 0x153E4A
        ("button", 0x154514, 0, 0),          // hack Obj32 0x154376
        ("mz-push-block", 0x1549A8, 0, 0),   // hack Obj33 0x154564
        ("mz-moving-block", 0x154C3E, 0, 0), // hack Obj52 0x1549D8
        ("mz-basaran", 0x1553E8, 0x155458, 0), // hack Obj55 0x155268
        ("mz-caterkiller", 0x1550C8, 0, 0),  // hack Obj78 0x154CCE
        ("mz-geyser", 0x155746, 0x155BD6, 0), // hack Obj4C/4D 0x15546C / 0x15555C
        ("mz-lava-wall", 0x155D46, 0x155EBA, 0), // hack Obj4E 0x155C04
        ("mz-breakable", 0x27AC0, 0, 0),     // Obj51 0x276F4 (hack zones' mappings)
        ("lava-marker", 0x24E7A, 0, 0),      // Obj54 0x24DFA (drawn in debug mode, and redrawn while Sonic dies)
        ("lava-marker-debug", 0x24E82, 0, 0),
        ("mz-collapsing-floor", 0x13F58, 0, 0), // Obj53 0x136DE (hack zones' mappings)
        ("giant-ring", 0x15328, 0, 0),       // hack Obj4B 0x150FE
        ("giant-ring-flash", 0x15428, 0, 0), // hack ObjDE 0x151F4
        ("signpost", 0x1D354, 0x1D324, 0),   // Obj0D 0x1CF34 (1-player mappings)
        ("helix", 0x1300A, 0, 0),            // Obj17 0x12ECA (Green Hill spiked helix)
        ("ghz-boss", 0x152A10, 0x152E28, 0), // hack Obj3D 0x152688 (Sonic 1 Green Hill boss)
        ("title-intro", 0x16610, 0x165EE, 0), // Obj0E 0x15D5C (title screen: Sonic, Tails, emblem top, stars)
        ("ghz-boss-anchor", 0x152D98, 0, 0), // hack Obj48 0x152EE0 (wrecking ball anchor)
        ("ghz-boss-ball", 0x1530B2, 0, 0),   // hack Obj48 (the ball)
        ("boss-explosion", 0x3176E, 0, 0),   // Obj58 0x316F8
        ("egg-prison", 0x43658, 0x4364A, 0), // Obj3E 0x433F0
        ("shield", 0x21A68, 0x21A5A, 0),     // Obj38 0x2177A
        ("stars", 0x21B40, 0, 0),            // Obj35 0x21806 (invincibility stars)
        ("results", 0x1825E, 0, 0),          // Obj3A 0x17384 (screen-space text and tally lines)
        ("missile", 0x151ED4, 0x151F04, 0),  // hack Obj23 0x151DD2 (Buzz Bomber / Newtron projectile)
        ("wall", 0x19886, 0, 0),             // hack Obj3C 0x19618 (breakable wall)
    ];

    /// <summary>
    /// Obj11 bridge tables: depression per standing log (rows of 16 for 8..16 logs, 0x12520; the code indexes it
    /// from 0x124A0 with subtype*16) and the bend profile (16 rows of 16, 0x125B0).
    /// </summary>
    public const int BridgeDepression = 0x12520, BridgeDepressionSize = 0x90, BridgeBend = 0x125B0, BridgeBendSize = 0x100;

    /// <summary>Obj1A collapsing ledge: slope profile (0x139CE, 48 bytes) and per-fragment delays (0x13984, 74 bytes).</summary>
    public const int LedgeSlope = 0x139CE, LedgeSlopeSize = 0x30, LedgeDelays = 0x13984, LedgeDelaysSize = 0x4A;

    /// <summary>Obj3C breakable wall: fragment {x_vel, y_vel} per piece, smashed from the left (0x1977A) / right (0x1979A).</summary>
    public const int WallSpeedsRight = 0x1977A, WallSpeedsLeft = 0x1979A, WallSpeedsSize = 0x20;

    /// <summary>Obj35 invincibility stars: circle offsets (32 x/y byte pairs, 0x219C6) and frame sequences ($FF-ended, 0x21A06-0x21A5A).</summary>
    public const int StarsCircle = 0x219C6, StarsSequences = 0x21A06, StarsSequencesEnd = 0x21A5A;

    /// <summary>Obj28 animals: per-zone pair of animal types (2 bytes per zone, 0x147BA) and per type {x_vel, y_vel, mappings} (0x147F4).</summary>
    public const int AnimalZoneTypes = 0x147BA, AnimalZoneCount = 0x1D, AnimalTypes = 0x147F4, AnimalTypeCount = 12;
}

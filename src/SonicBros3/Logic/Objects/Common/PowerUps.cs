using System.Text.Json;

namespace SonicBros3.Logic;

/// <summary>
/// The shield, object $38 (ROM 0x2177A): follows its player, hidden while the player is invincible, deleted once
/// the shield is lost (status_secondary bit 0).
/// </summary>
public sealed class Shield(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x21A68;
    private readonly byte[] _anims = ctx.Art["shield"].Animations!;

    /// <summary>$3E: the player's slot.</summary>
    private static int PlayerSlot(SonicObject o) => o.Word(0x3E);

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.Mappings = Mappings;
            o.RenderFlags = Render.LevelCoords;
            o.Priority = 1;
            o.WidthPixels = 0x18;
            o.ArtTile = 0x4BE;
        }
        var player = new SonicObject(Ram, PlayerSlot(o));
        bool invincible = player.Invincible, hasShield = player.HasShield;
        if (invincible) return;
        if (!hasShield) { Delete(o); return; }
        o.X = player.X;
        o.Y = player.Y;
        o.Status = player.Status;
        o.ArtTile = (o.ArtTile & 0x7FFF) | (player.ArtTile & 0x8000);   // the player's priority
        ObjectKit.Animate(Ram, o, _anims);
        Display(o);
    }
}

/// <summary>
/// The invincibility stars, object $35 (ROM 0x21806). Four multi-sprite objects in consecutive slots, two stars each:
/// the first circles the player; the others follow the player's recorded position history, further back each.
/// The routine is in $0A, not $24, since the multi-sprite data occupies $10 onward.
/// </summary>
public sealed class InvincibilityStars : GameObject
{
    public const uint Mappings = 0x21B40;
    private const int Init = 0, Circling = 2, Trailing = 4;

    /// <summary>The circle: 32 (x, y) offsets.</summary>
    private readonly sbyte[] _circle;
    /// <summary>Frame sequences (ended by a negative byte), addressed by their ROM address.</summary>
    private readonly byte[] _sequences;
    private readonly int _sequencesAddress;
    /// <summary>Per trailing group: its sequence's offset and its parameter word.</summary>
    private readonly (int Sequence, int Parameter)[] _trailing;

    /// <summary>A group's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        public int Routine { get => o.Byte(0x0A); set => o.SetByte(0x0A, value); }
        /// <summary>$30: its frame sequence (as the original's ROM pointer).</summary>
        public uint Sequence { get => o.Long(0x30); set => o.SetLong(0x30, value); }
        /// <summary>$34: the angle round the circle; $35: the second star's offset into the sequence.</summary>
        public int Angle { get => o.Byte(0x34); set => o.SetByte(0x34, value); }
        public int SecondStarOffset => o.Byte(0x35);
        public int Parameter { set => o.SetWord(0x34, value); }
        /// <summary>$36: the group's number (0 circles; 1-3 trail 12, 24, 36 frames behind).</summary>
        public int Group { get => o.Byte(0x36); set => o.SetByte(0x36, value); }
        /// <summary>$38: position in the frame sequence.</summary>
        public int SequenceIndex { get => o.Word(0x38); set => o.SetWord(0x38, value); }
        /// <summary>$3E: the player's slot; $3F (its low byte) is nonzero for Tails.</summary>
        public int PlayerSlot { get => o.Word(0x3E); set => o.SetWord(0x3E, value); }
        public bool ForTails => o.Byte(0x3F) != 0;
    }

    public InvincibilityStars(ObjectContext ctx) : base(ctx)
    {
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(ctx.Art.DataDir, "objects", "stars", "stars.json")));
        var r = doc.RootElement;
        _circle = r.GetProperty("circle").EnumerateArray().Select(e => (sbyte)e.GetInt32()).ToArray();
        _sequences = r.GetProperty("sequences").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
        _sequencesAddress = r.GetProperty("sequencesAddress").GetInt32();
        _trailing = r.GetProperty("trailing").EnumerateArray().Select(e => (e[0].GetInt32(), e[1].GetInt32())).ToArray();
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (s.Routine)
        {
            case Init: CreateGroups(o, s); break;
            case Circling: Circle(o, s); break;
            case Trailing: Trail(o, s); break;
        }
    }

    private void CreateGroups(SonicObject o, State s)
    {
        for (int i = 0; i < 4; i++)
        {
            var group = new SonicObject(Ram, o.Address + i * Objects.SlotSize);
            var g = new State(group);
            group.Id = o.Id;
            g.Routine = Trailing;
            group.Mappings = Mappings;
            group.ArtTile = 0x4DE;
            group.RenderFlags = Render.MultiSprite | Render.LevelCoords;
            var multi = new MultiSprite(group);
            multi.Width = 0x10;
            multi.ChildCount = 2;
            g.PlayerSlot = s.PlayerSlot;
            g.Group = i;
            if (i > 0)
            {
                g.Sequence = (uint)(_sequencesAddress + _trailing[i - 1].Sequence);
                g.Parameter = _trailing[i - 1].Parameter;
            }
            else
            {
                // The parameter table starts 6 bytes early (0x21814): the first group reads the routine offsets.
                g.Sequence = 0x0018007C;
                g.Parameter = 0x00F0;
            }
        }
        s.Routine = Circling;
        s.Angle = 4;
        Circle(o, s);
    }

    private (int X, int Y) OnCircle(int angle, int x, int y)
    {
        angle &= 0x3E;
        return ((x + _circle[angle]) & 0xFFFF, (y + _circle[angle + 1]) & 0xFFFF);
    }

    /// <summary>0x21890: two stars opposite each other on the circle round the player.</summary>
    private void Circle(SonicObject o, State s)
    {
        var player = new SonicObject(Ram, s.PlayerSlot);
        if (!player.Invincible) { Delete(o); return; }   // invincibility over
        o.X = player.X;
        o.Y = player.Y;
        int frame = NextFrame(s, 0);
        var multi = new MultiSprite(o);
        Place(multi, 0, OnCircle(s.Angle, player.X, player.Y), frame);
        Place(multi, 1, OnCircle(s.Angle + 0x20, player.X, player.Y), frame);
        s.Angle += player.FacingLeft ? -0x12 : 0x12;
        SpriteBuilder.DisplaySpriteAt(Ram, o, 0x80);
    }

    /// <summary>0x21904: two stars at the player's position from 12 x group frames ago.</summary>
    private void Trail(SonicObject o, State s)
    {
        var player = new SonicObject(Ram, s.PlayerSlot);
        if (!player.Invincible) { Delete(o); return; }
        bool tails = Ram.U16(Vars.PlayerMode) == 2 || s.ForTails;
        int indexAddr = tails ? Vars.TailsPosRecordIndex : Vars.SonicPosRecordIndex;
        int history = tails ? Vars.TailsPosRecordBuf : Vars.SonicPosRecordBuf;
        int framesBack = ((s.Group << 2) & 0xFF) * 3;   // in 4-byte entries: 12 x group frames
        int index = Ram.U16(indexAddr);
        index = (index & 0xFF00) | ((index - framesBack) & 0xFF);
        o.X = Ram.U16(history + index);
        o.Y = Ram.U16(history + index + 2);
        int sequence = (int)s.Sequence - _sequencesAddress;
        int position = s.SequenceIndex;
        int firstFrame;
        while ((sbyte)(firstFrame = _sequences[sequence + position]) < 0) { s.SequenceIndex = 0; position = 0; }
        int secondFrame = _sequences[sequence + ((position + s.SecondStarOffset) & 0xFF)];
        s.SequenceIndex++;
        var multi = new MultiSprite(o);
        Place(multi, 0, OnCircle(s.Angle, o.X, o.Y), secondFrame);   // the second sequence frame comes first
        Place(multi, 1, OnCircle(s.Angle + 0x20, o.X, o.Y), firstFrame);
        s.Angle += player.FacingLeft ? -2 : 2;
        SpriteBuilder.DisplaySpriteAt(Ram, o, 0x80);
    }

    /// <summary>The circling stars' frame: the sequence at 0x21A06, restarted at its end.</summary>
    private int NextFrame(State s, int sequence)
    {
        int position = s.SequenceIndex;
        int frame;
        while ((sbyte)(frame = _sequences[sequence + position]) < 0) { s.SequenceIndex = 0; position = 0; }
        s.SequenceIndex++;
        return frame;
    }

    private static void Place(MultiSprite multi, int star, (int X, int Y) pos, int frame) =>
        multi.SetChild(star, pos.X, pos.Y, frame);
}

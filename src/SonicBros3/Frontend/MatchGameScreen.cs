using SonicBros3.Logic;
using SonicBros3.Smb3;

namespace SonicBros3.Frontend;

/// <summary>
/// The spade panel's card game: SMB3's N-Spade (the memory card matching game, prg022) with Tails as the host and
/// Sonic's monitors on the cards.
///
/// First the bonus room (tile memory from SMB3's own loader, <see cref="Smb3Assets.BonusRoom"/>, drawn with
/// Tile_Layout_TS15_TS16_TS17 on BG banks $24/$5E and PalSet_Bonus): the instruction box builds over five frames
/// (Video_Upd_Table $08-$0C, Bonus_Init), then the host types BonusGame_NSpade_Text ("Flip over any two cards / and
/// see if they match. / You can only miss twice!") a character every 4 frames with a blip every other one
/// (Bonus_DoHostText), and waits $A0 frames (Bonus_WaitA0). Tails stands where Toad does, behind the table, and
/// Sonic where the room has Mario's tiles.
///
/// Then the cards (NSpade_DoGame and its states, ported): the candy-stripe background (Video_NSpadeBG), 18 cards from
/// Card_InitialSet shuffled by Card_Shuffle (Randomize), each drawn with SMB3's graphics buffer templates
/// (CardBack_BufCmds / CardBase_BufCmds / CardBlackout_BufCmds patched as Card_RenderBack / Card_RenderFace /
/// Card_Blackout do), the 10-sprite cursor (Card_DrawCursor, a move every 8 frames), the 5-frame flip
/// (CardFlip_Draw), a match or a miss (Card_CheckForMatch), two misses allowed (Card_TurnsRemain), and the deck kept
/// between visits until all nine pairs are found (Map_NSpadeInProg). The faces are Sonic monitor icons in place of
/// SMB3's pictures: mushroom = speed shoes, flower = shield, star = invincibility, 1-up = Sonic, 10/20 coins = 10/20
/// rings (with SMB3's "1UP" / "10" / "20" lines). The card backs show a ring in place of the N-Spade. Prizes as
/// Card_MatchPairReward gives them: an extra life, rings one a frame (100 = a life, as coins), and the power-ups
/// come out of a monitor at the start of the next level (SMB3 puts them in the inventory).
/// </summary>
public sealed class MatchGameScreen : Screen
{
    // Bank 22 (smb3.fns; matches this image).
    private const int TileLayoutTs15 = 0xC000, NSpadeText = 0xCA9B, CardSelectPattern = 0xD6BB, CardSelectAttribute = 0xD6C5,
        CardCursorXs = 0xD6CF, CardCursorYs = 0xD6E1, CardFlipAttributes = 0xD6F3, CardFlipFrameDefs = 0xD6F8,
        CardFlipFrameDefOff = 0xD70C, CardInitialSet = 0xD711, CardVramHi = 0xD723, CardVramLo = 0xD735,
        CardFacePatterns = 0xD747, CardFaceAttribute = 0xD75F, CardVramAttrLo = 0xD765, CardBottomLeft = 0xD77C,
        CardBottomRight = 0xD77F, CardBaseBufCmds = 0xDD78, CardBlackoutBufCmds = 0xDD94, CardBackBufCmds = 0xDDB1,
        CardBufCmdsLength = 0x1C, CardBlackoutLength = 0x1D;
    private const int VideoUpdTable = 0x803E;                       // bank 30
    private const int PalSetBonus = 0xB712;                         // bank 27
    private const int CardMushroom = 0, CardFlower = 1, CardStar = 2, CardOneUp = 3, CardTenCoin = 4, CardTwentyCoin = 5,
        CardWild = 8, Flipped = 0x80;

    // VRAM tiles of the screen's own art.
    private const int SonicTile = 0x500, TailsTile = 0x540, MonitorTile = 0x580, LifeIconTile = 0x5C0;

    private readonly Smb3Assets _assets;
    private readonly NesPresenter _nes;
    private readonly NesPpu _ppu = new();
    private readonly SpriteKit _sprites;
    private readonly MapProgress _progress;
    private readonly SpriteData _sonicArt, _tailsArt, _monitor;
    private readonly byte[] _pool = new byte[10];

    private bool _room = true, _fading;
    private readonly List<(int X, int Y, int Size, int Pattern)> _extraSprites = [];

    public MatchGameScreen(string dataDir, MapProgress progress, Audio.ISoundChips? chips = null) : base(dataDir, chips)
    {
        _progress = progress;
        _assets = new Smb3Assets(dataDir);
        _nes = new NesPresenter(Vram, Ram, _assets);
        _sprites = new SpriteKit(Ram, Vram);
        _sonicArt = Art["sonic"];
        _tailsArt = Art["tails"];
        _monitor = Art["monitor"];
        _nes.SpriteBanks[0] = 0x28; _nes.SpriteBanks[1] = 0x29; _nes.SpriteBanks[2] = 0x5A; _nes.SpriteBanks[3] = 0x2B;
        for (int q = 0; q < 4; q++) _nes.TileOverrides[(0x5C, 0x2E + (q & 1) + (q >> 1) * 0x10)] = StatusBar.Glyph(RingBack, (q & 1) * 8, (q >> 1) * 8);
        LineScrollA = line => !_room || line + 8 < 193 ? 8 : 7;
    }

    private byte Prg22(int a) => _assets.Prg(22, a);

    protected override IEnumerable<bool> Mode()
    {
        ClearScreen();
        BackdropIndex = 0x20;
        LoadOwnArt();
        PaletteFade.LoadForFade(Ram, DataDir, 3);
        for (int i = 0; i < 32; i++) _ppu.Palette[i] = _assets.Prg(27, PalSetBonus + (i < 16 ? i : 0x80 + i - 16));
        foreach (var f in Room()) yield return f;
        foreach (var f in Cards()) yield return f;
        _fading = true;
        foreach (var f in FadeToBlack()) yield return f;
        Result = new ScreenResult.BackToMap();
    }

    // ------------------------------------------------------------------ the bonus room

    private IEnumerable<bool> Room()
    {
        _ppu.HorizontalMirroring = false;
        _ppu.Clear(0xFC);
        var room = _assets.BonusRoom;
        for (int r = 0; r < 12; r++)
        for (int c = 0; c < 16; c++)
        {
            int t = room[(15 + r) * 16 + c];
            if (t is 0x4A or 0x86) t = 0x40;   // the player's tiles (LoadLevel_BonusPlayer): Sonic is a sprite here
            if (t is >= 0xCE and <= 0xD3)
                for (int q = 0; q < 4; q++) _table.Add((c * 2 + (q & 1), r * 2 + (q >> 1)));
            int nt = 0x2800 + r * 64 + c * 2;
            _ppu.Write(nt, Prg22(TileLayoutTs15 + t));
            _ppu.Write(nt + 32, Prg22(TileLayoutTs15 + 256 + t));
            _ppu.Write(nt + 1, Prg22(TileLayoutTs15 + 512 + t));
            _ppu.Write(nt + 33, Prg22(TileLayoutTs15 + 768 + t));
            _ppu.SetCellPalette(2, c * 2, r * 2, t >> 6);
        }
        StatusBar.Draw(_ppu, _nes, _progress, StatusBar.Stream2B00);
        _fading = true;
        Present();
        foreach (var f in FadeFromBlack()) yield return f;
        _fading = false;

        // Bonus_Init: the instruction box, one part a frame.
        for (int item = 0x08; item <= 0x0C; item++)
        {
            RunVideoUpdate(item);
            Present();
            yield return true;
        }
        // Bonus_DoHostText from $28C5: a character every 4 frames, a blip every other one, $00 = next line.
        int v = 0x28C5, pos = 0, pause = 4;
        while (true)
        {
            if (--pause == 0)
            {
                pause = 4;
                byte ch;
                while ((ch = Prg22(NSpadeText + pos)) == 0)
                {
                    v = ((v + 32) & 0xFFE0) | 0x05;
                    pos++;
                }
                if (ch == 0xFF) break;
                if ((pos & 1) != 0) Sound.PlaySound(Sfx.Blip);
                _ppu.Write(v++, ch);
                pos++;
            }
            Present();
            yield return true;
        }
        // Bonus_WaitA0 (Start or A/B/C skips the wait).
        for (int i = 0; i < 0xA0 && (Ram.U8(Vars.Ctrl1Press) & 0xF0) == 0; i++)
        {
            Present();
            yield return true;
        }
        _fading = true;
        foreach (var f in FadeToBlack()) yield return f;
        _fading = false;
    }

    private void RunVideoUpdate(int item)
    {
        int addr = _assets.PrgWord(30, VideoUpdTable + item * 2);
        int bank = addr >= 0xC000 ? 22 : 30;
        _ppu.RunStream(a => _assets.Prg(bank, a), addr);
    }

    // ------------------------------------------------------------------ the cards

    /// <summary>NSpade_DoGame's state (Card_GameState) and variables.</summary>
    private int _state, _index, _firstIndex, _matchCard, _flipCount, _flipFrame = -1, _animTick, _moveDelay,
        _turnsRemain, _matches, _ringsToGive, _flipBack, _flipCard = -1;
    private int[] _deck = [];
    private bool _cursor;
    /// <summary>Cards whose face is drawn (their icon goes over it).</summary>
    private readonly bool[] _faceUp = new bool[18];

    private IEnumerable<bool> Cards()
    {
        _room = false;
        _ppu.HorizontalMirroring = true;
        _ppu.Clear(0xFF);                                   // ClearPattern_ByTileset[17]
        _nes.BgBanks[0] = 0x5C; _nes.BgBanks[1] = 0x5D; _nes.BgBanks[2] = 0x5E; _nes.BgBanks[3] = 0x5F;
        RunVideoUpdate(0x0D);                               // Video_NSpadeBG
        StatusBar.Draw(_ppu, _nes, _progress, StatusBar.Stream2300);

        // Card_InitDeck: a game in progress continues; otherwise Card_InitialSet, shuffled.
        if (_progress.Deck is { } deck) { _deck = deck; _matches = _progress.DeckMatches; }
        else
        {
            _deck = Enumerable.Range(0, 18).Select(i => (int)Prg22(CardInitialSet + i)).ToArray();
            // SMB3's Random_Pool turns over every frame, so the shuffle depends on when the game starts: here the frames
            // the screen has run, the score and the lives seed it (repeatable for the same play).
            int seed = (int)Ram.U32(Vars.VintRuncount) * 0x9E37 ^ _progress.Score ^ (_progress.Lives << 8);
            for (int i = 0; i < _pool.Length; i++) _pool[i] = (byte)((seed >> (i % 4 * 8)) ^ (0x88 + i * 37));
            Shuffle();
            _progress.Deck = _deck;
            _progress.DeckMatches = _matches = 0;
        }
        for (int i = 17; i >= 0; i--)
        {
            if ((_deck[i] & Flipped) != 0) RenderFace(i);
            else RenderBack(i);
        }
        _index = 0;
        _turnsRemain = 1;                                    // Card_InitVars
        _cursor = true;
        _fading = true;
        Present();
        foreach (var f in FadeFromBlack()) yield return f;
        _fading = false;

        while (true)
        {
            GiveRings();
            bool exit = Step();
            Present();
            yield return true;
            if (exit) break;
        }
    }

    /// <summary>One frame of NSpade_DoGame; true once the game is over (Card_WaitThenExit's delay ran out).</summary>
    private bool Step()
    {
        _extraSprites.Clear();
        switch (_state)
        {
            case 0: DoSelection(); _cursor = true; break;
            case 1: BeginFlip(); break;
            case 2: DoFlip(); break;
            case 3: _flipCount++; CheckForMatch(); break;
            case 4: HandleMatch(); break;
            case 5: HandleMismatch(); break;
            case 6:
                if (--_moveDelay < 0) return true;
                break;
            case 7: BeginFlipBack(); break;
            case 8: FlipBackCard(); break;
        }
        return false;
    }

    /// <summary>Card_DoSelection: A flips, directions move (one move per 8 frames), wrapping around.</summary>
    private void DoSelection()
    {
        if (_moveDelay != 0) { _moveDelay--; return; }
        int held = Ram.U8(Vars.Ctrl1Held);
        if ((held & 0x40) != 0 || (held & 0x30) != 0) { _state = 1; _cursor = false; return; }   // A (or B/C)
        if ((held & 0x0F) == 0) return;
        _moveDelay = 8;
        Sound.PlaySound(Sfx.Blip);
        if ((held & 0x01) != 0) { _index -= 6; if (_index < 0) _index += 18; }
        if ((held & 0x02) != 0) { _index += 6; if (_index >= 18) _index -= 18; }
        if ((held & 0x08) != 0) { _index++; if (_index >= 18) _index = 0; }
        if ((held & 0x04) != 0) { _index--; if (_index < 0) _index = 17; }
    }

    private void BeginFlip()
    {
        if ((_deck[_index] & Flipped) != 0) { _state = 0; return; }
        _flipFrame = 4;
        _flipCard = _index;
        Blackout(_index);
        _animTick = 0;
        _state = 2;
        Sound.PlaySound(Sfx.Signpost);
        DoFlip();
    }

    /// <summary>Card_DoFlip: the flip sprites frame 4 down to 0, 4 frames apart, then the face.</summary>
    private void DoFlip()
    {
        if (_animTick != 0) { _animTick--; DrawFlip(_flipCard); return; }
        DrawFlip(_flipCard);
        if (--_flipFrame < 0)
        {
            RenderFace(_index);
            _flipCard = -1;
            _state = 3;
        }
        _animTick = 4;
    }

    /// <summary>Card_CheckForMatch (the wild card included, though SMB3's deck has none).</summary>
    private void CheckForMatch()
    {
        if ((_flipCount & 1) != 0)
        {
            _firstIndex = _index;
            _matchCard = _deck[_index];
            _deck[_index] |= Flipped;
            _state = 0;
            return;
        }
        int card = _deck[_index];
        if (card == _matchCard) { _state = 4; return; }
        if (card == CardWild) { _deck[_index] = _matchCard; _state = 4; return; }
        if (_matchCard == CardWild) { _deck[_firstIndex] = card | Flipped; _state = 4; return; }
        _state = 5;
        _moveDelay = 0x30;
    }

    private void HandleMatch()
    {
        _deck[_index] |= Flipped;
        _state = 0;
        _matches++;
        _progress.DeckMatches = _matches;
        Reward(_deck[_index] & 0x0F);
        if (_matches == 9)
        {
            _state = 6;
            _moveDelay = 0x40;
            _progress.Deck = null;   // the game is complete: a new deck next time
        }
    }

    private void HandleMismatch()
    {
        _flipFrame = 0;
        _flipBack = 1;
        _state = 7;
        _moveDelay = 0x40;
        _deck[_index] &= 0x7F;
        _deck[_firstIndex] &= 0x7F;
        Sound.PlaySound(Sfx.Hurt);
        if (--_turnsRemain < 0) _state = 6;
    }

    private void BeginFlipBack()
    {
        if (_moveDelay != 0) { _moveDelay--; return; }
        _flipFrame = 0;
        _flipCard = _flipBack == 1 ? _index : _firstIndex;
        Blackout(_flipCard);
        _state = 8;
        _animTick = 0;
        FlipBackCard();
    }

    /// <summary>Card_FlipBackCard: frames 0 up to 4, then the back; then the other card.</summary>
    private void FlipBackCard()
    {
        if (_animTick != 0) { _animTick--; DrawFlip(_flipCard); return; }
        DrawFlip(_flipCard);
        if (++_flipFrame >= 5)
        {
            RenderBack(_flipCard);
            _flipCard = -1;
            if (--_flipBack >= 0) { _moveDelay = 2; _state = 7; }
            else _state = 0;
            return;
        }
        _animTick = 4;
    }

    /// <summary>Card_MatchPairReward, Sonic's way.</summary>
    private void Reward(int card)
    {
        switch (card)
        {
            case CardOneUp:
                _progress.Lives = Math.Min(_progress.Lives + 1, 99);
                Sound.PlayMusic(Music.ExtraLife);
                break;
            case CardTenCoin or CardTwentyCoin:
                _ringsToGive += card == CardTenCoin ? 10 : 20;
                break;
            default:
                // Mushroom, flower, star: speed shoes, shield, invincibility, out of a monitor at the next level.
                _progress.PowerUps.Add(card == CardMushroom ? 5 : card == CardFlower ? 6 : 7);
                Sound.PlaySound(Sfx.Shield);
                break;
        }
        StatusBar.Values(_ppu, _progress, 0x2300);
    }

    /// <summary>NSpade_DoGame's coin giving, one a frame: rings, 100 of them a life (as SMB3's coins).</summary>
    private void GiveRings()
    {
        if (_ringsToGive == 0) return;
        _ringsToGive--;
        _progress.Rings++;
        _progress.Score += 100;
        Sound.PlaySound(Sfx.Ring);
        if (_progress.Rings >= 100)
        {
            _progress.Rings -= 100;
            _progress.Lives = Math.Min(_progress.Lives + 1, 99);
            Sound.PlayMusic(Music.ExtraLife);
        }
        StatusBar.Values(_ppu, _progress, 0x2300);
    }

    /// <summary>Card_Shuffle: three times, rotate the first 15 cards 0-31 places (Randomize), then swap cards
    /// 0, 5 and 10 around.</summary>
    private void Shuffle()
    {
        for (int pass = 2; pass >= 0; pass--)
        {
            Randomize();
            int y = _pool[0] & 0x1F;
            do
            {
                int last = _deck[14];
                for (int x = 0x0D; x >= 0; x--) _deck[x + 1] = _deck[x];
                _deck[0] = last;
            } while (--y >= 0);
            (_deck[0], _deck[5], _deck[10]) = (_deck[5], _deck[10], _deck[0]);
        }
    }

    /// <summary>Randomize (prg030): the pool rotated right through a feedback bit; A = the pool's first byte.</summary>
    private void Randomize()
    {
        bool carry = ((_pool[0] & 2) ^ (_pool[1] & 2)) != 0;
        for (int i = 0; i < 9; i++)
        {
            bool next = (_pool[i] & 1) != 0;
            _pool[i] = (byte)((_pool[i] >> 1) | (carry ? 0x80 : 0));
            carry = next;
        }
    }

    // ------------------------------------------------------------------ card drawing (graphics buffer templates)

    private byte[] Template(int addr, int length) => Enumerable.Range(0, length).Select(i => Prg22(addr + i)).ToArray();

    /// <summary>Patches a template's four rows with the card's VRAM addresses (32 apart) and runs it.</summary>
    private void RunCard(byte[] buf, int card, bool attribute, int attr = 0xFF)
    {
        int v = (Prg22(CardVramHi + card) << 8) | Prg22(CardVramLo + card);
        for (int row = 0; row < 4; row++, v += 32)
        {
            buf[row * 7] = (byte)(v >> 8);
            buf[row * 7 + 1] = (byte)v;
        }
        var stream = buf.ToList();
        if (attribute) stream.AddRange([0x23, Prg22(CardVramAttrLo + card), 0x01, (byte)attr]);
        stream.Add(0);
        _ppu.RunStream(a => stream[a], 0);
    }

    /// <summary>Card_RenderBack.</summary>
    private void RenderBack(int card)
    {
        _faceUp[card] = false;
        RunCard(Template(CardBackBufCmds, CardBufCmdsLength), card, true);
    }

    /// <summary>Card_Blackout.</summary>
    private void Blackout(int card)
    {
        _faceUp[card] = false;
        RunCard(Template(CardBlackoutBufCmds, CardBlackoutLength - 1), card, false);
    }

    /// <summary>Card_RenderFace with the picture left blank (the Sonic icon is drawn over it), the 1UP/10/20 line
    /// kept, and the face's attribute (Card_FaceAttribute; the first two rows' cards use its low quadrant).</summary>
    private void RenderFace(int card)
    {
        var buf = Template(CardBaseBufCmds, CardBufCmdsLength);
        int face = _deck[card] & 0x7F;
        int attr = Prg22(CardFaceAttribute + face);
        if (card is < 6 or >= 12) attr = (attr << 4) | 0x0F;
        if (face >= CardOneUp)
        {
            buf[0x19] = Prg22(CardBottomLeft + face - CardOneUp);
            buf[0x1A] = Prg22(CardBottomRight + face - CardOneUp);
        }
        RunCard(buf, card, true, attr & 0xFF);
        _faceUp[card] = true;
    }

    /// <summary>CardFlip_Draw: four 8x16 sprites over the blacked-out card, the right ones flipped.</summary>
    private void DrawFlip(int card)
    {
        if (card < 0) return;
        int x0 = Prg22(CardCursorXs + card) + 8, y0 = Prg22(CardCursorYs + card) + 8;
        int frame = Math.Clamp(_flipFrame, 0, 4);
        int pal = Prg22(CardFlipAttributes + frame) & 3;
        for (int i = 0; i < 4; i++)
        {
            int pattern = Prg22(CardFlipFrameDefs + Prg22(CardFlipFrameDefOff + frame) + i);
            bool hflip = (i & 1) != 0;
            int t = _nes.SpriteTile(pattern, pal);
            _extraSprites.Add((x0 + (i & 1) * 8, y0 + (i >> 1) * 16, 1, 0xE000 | t | (hflip ? 0x800 : 0)));
        }
    }

    // ------------------------------------------------------------------ the picture

    private void Present()
    {
        if (_room)
        {
            _nes.BgBanks[0] = 0x24; _nes.BgBanks[1] = 0x25; _nes.BgBanks[2] = 0x5E; _nes.BgBanks[3] = 0x5F;
            for (int row = 0; row < 24; row++) _nes.Row(_ppu, 2, row, PlaneA, row, high: c => _table.Contains((c, row)));
            _nes.BgBanks[0] = 0x5C; _nes.BgBanks[1] = 0x5D;
            for (int row = 24; row < 30; row++) _nes.Row(_ppu, 2, row, PlaneA, row);
        }
        else
            for (int row = 0; row < 30; row++) _nes.Row(_ppu, 0, row, PlaneA, row);
        _nes.Palettes(_ppu, _fading ? Vars.TargetPalette : Vars.NormalPalette);

        _sprites.Begin();
        if (_room)
        {
            // Tails behind the table (it is drawn in front of low-priority sprites), Sonic where Mario stood.
            Character(_tailsArt, 1, TailsTile, 136, 114, flip: false);
            Character(_sonicArt, 1, SonicTile, 40, 124, flip: false);
        }
        else
        {
            foreach (var (x, y, size, pattern) in _extraSprites) _sprites.Piece(x + 32, y + 1 - 8, size, pattern);
            if (_cursor && _state == 0) DrawCursor();
            for (int i = 0; i < 18; i++)
                if (_faceUp[i]) Icon(i);
            DrawCards();
        }
        _sprites.End();
    }

    /// <summary>Nametable cells of the bonus room's table ($CE-$D3): high priority, in front of the host.</summary>
    private readonly HashSet<(int Col, int Row)> _table = [];

    private void Character(SpriteData art, int frame, int tile, int x, int y, bool flip)
    {
        _sprites.LoadDplc(art, frame, tile);
        _sprites.Frame(art, frame, x + 32, y - 8, tile, flip);
    }

    /// <summary>Card_DrawCursor: four 8x16 sprites across the top, one each side twice, four across the bottom.</summary>
    private void DrawCursor()
    {
        int x0 = Prg22(CardCursorXs + _index), y = Prg22(CardCursorYs + _index), x = x0, n = 0;
        void Sprite(int px, int py)
        {
            int pattern = Prg22(CardSelectPattern + n), attr = Prg22(CardSelectAttribute + n++);
            int t = _nes.SpriteTile(pattern, attr & 3);
            _sprites.Piece(px + 32, py + 1 - 8, 1, 0xE000 | t | ((attr & 0x40) != 0 ? 0x800 : 0) | ((attr & 0x80) != 0 ? 0x1000 : 0));
        }
        for (int i = 0; i < 4; i++, x += 8) Sprite(x, y);
        y += 16; Sprite(x0, y); Sprite(x0 + 24, y);
        y += 16; x = x0;
        for (int i = 0; i < 4; i++, x += 8) Sprite(x, y);
    }

    /// <summary>A face-up card's Sonic monitor icon over the blank picture (card tile column + 1, row + 1).</summary>
    private void Icon(int card)
    {
        int v = (Prg22(CardVramHi + card) << 8) | Prg22(CardVramLo + card);
        int col = (v & 0x1F) + 1, row = ((v - 0x2000) >> 5) + 1;
        int face = _deck[card] & 0x0F;
        int frame = face switch { CardMushroom => 6, CardFlower => 7, CardStar => 8, CardOneUp => 2, _ => 5 };
        var p = _monitor.Frames[frame][0];
        // The 1-up's icon is the life icon art (pattern $154 from the monitor art = VRAM $7D4 in levels).
        int pattern = frame == 2 ? LifeIconTile : ((p.Pattern & 0x7FF) + MonitorTile) | (p.Pattern & 0x6000);
        _sprites.Piece(col * 8 + 32, row * 8 - 8, p.Size, 0x8000 | pattern);
    }

    /// <summary>The status bar's card slots (the level cards, as on the map).</summary>
    private void DrawCards()
    {
        for (int i = 0; i < _progress.Cards.Count && i < 3; i++)
        {
            var p = _monitor.Frames[WorldMapScreen.CardMonitorFrame(_progress.Cards[i])][0];
            _sprites.Piece(StatusBar.CardSlotX(i) + 32, StatusBar.CardSlotY(24) - 8, p.Size, ((p.Pattern + MonitorTile) | 0x8000) & 0xFFFF);
        }
    }

    private void LoadOwnArt()
    {
        var art = S2Port.Extraction.Levels.LevelLoader.LoadTileArt(Path.Combine(DataDir, "art", "nemesis", "nem_080B08.png"), 60);
        art.CopyTo(Vram.AsSpan(MonitorTile * 32));
        var life = S2Port.Extraction.Levels.LevelLoader.LoadTileArt(Path.Combine(DataDir, "art", "nemesis", "nem_0808FE.png"), 12);
        life.AsSpan(0, 4 * 32).CopyTo(Vram.AsSpan(LifeIconTile * 32));
    }

    /// <summary>The card back's picture (CHR $2E/$2F/$3E/$3F of bank $5C, SMB3's spade with an N), redrawn as a gold
    /// ring: '.' black, 'b' the card's white, 'r'/'w' the ring's gold and shade (back palette 3: $27, $17).</summary>
    private static readonly string[] RingBack =
    [
        "bbbbbbbbbbbbbbbb",
        "bbbbb......bbbbb",
        "bbb..rrrrrr..bbb",
        "bb.rrrrrrrrrr.bb",
        "bb.rrr....rrw.bb",
        "b.rrr.bbbb.rrw.b",
        "b.rr.bbbbbb.rw.b",
        "b.rr.bbbbbb.rw.b",
        "b.rr.bbbbbb.rw.b",
        "b.rr.bbbbbb.rw.b",
        "b.rrr.bbbb.rww.b",
        "bb.rrr....rww.bb",
        "bb.wrrrrrrrww.bb",
        "bbb..wwwwww..bbb",
        "bbbbb......bbbbb",
        "bbbbbbbbbbbbbbbb",
    ];
}

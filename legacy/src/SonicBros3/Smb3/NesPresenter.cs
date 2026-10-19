using S2Port.Extraction.Smb3;
using SonicBros3.Logic;

namespace SonicBros3.Smb3;

/// <summary>
/// Shows NES graphics on the Mega Drive screen: each (CHR tile, NES palette) pair used becomes one MD tile in VRAM
/// (made on first use, like Smb3ZoneBuilder does for 1-1), NES background palettes go to palette line 2 (colour =
/// palette * 4 + value, value 0 transparent so the backdrop shows the NES universal colour) and sprite palettes to
/// line 3. The NES picture is 256 wide: NES column c is plane column c + 4, so the Mega Drive's extra 64 pixels are
/// two 32-pixel margins the screens fill as they like.
/// </summary>
public sealed class NesPresenter(byte[] vram, Ram ram, Smb3Assets assets)
{
    /// <summary>VRAM tiles used for converted NES tiles (the rest is the screens' own art).</summary>
    public const int FirstTile = 0x001, LastTile = 0x2FF;

    /// <summary>The four 1 KiB CHR banks behind background tiles $00-$3F, $40-$7F, $80-$BF, $C0-$FF.</summary>
    public int[] BgBanks { get; } = new int[4];
    /// <summary>The CHR banks behind sprite tiles (8x16 sprites: odd tile numbers use these, PPU $1000).</summary>
    public int[] SpriteBanks { get; } = new int[4];

    /// <summary>Hand-drawn replacements for CHR tiles: 64 pixel values 0-3 per (bank, tile).</summary>
    public Dictionary<(int Bank, int Tile), byte[]> TileOverrides { get; } = [];

    public Smb3Assets Assets => assets;

    private readonly Dictionary<(int Bank, int Tile, int Pal), int> _cache = [];
    private int _next = FirstTile;

    /// <summary>The MD tile for background tile <paramref name="nesTile"/> in NES palette <paramref name="pal"/>.</summary>
    public int BgTile(int nesTile, int pal) => Tile(BgBanks[nesTile >> 6], nesTile & 63, pal);

    public int Tile(int bank, int tile, int pal)
    {
        if (_cache.TryGetValue((bank, tile, pal), out int t)) return t;
        if (_next > LastTile) { _cache.Clear(); _next = FirstTile; }
        t = _next++;
        Convert(bank, tile, pal).CopyTo(vram.AsSpan(t * 32));
        return _cache[(bank, tile, pal)] = t;
    }

    /// <summary>An 8x16 sprite (SMB3 uses odd tile numbers: table $1000) as two consecutive MD tiles, top first.</summary>
    public int SpriteTile(int nesTile, int pal)
    {
        int top = nesTile & 0xFE;
        var key = (-1 - SpriteBanks[top >> 6], top & 63, pal);
        if (_cache.TryGetValue(key, out int t)) return t;
        if (_next + 1 > LastTile) { _cache.Clear(); _next = FirstTile; }
        t = _next;
        _next += 2;
        Convert(SpriteBanks[top >> 6], top & 63, pal).CopyTo(vram.AsSpan(t * 32));
        Convert(SpriteBanks[top >> 6], (top & 63) + 1, pal).CopyTo(vram.AsSpan((t + 1) * 32));
        return _cache[key] = t;
    }

    private byte[] Convert(int bank, int tile, int pal)
    {
        if (!TileOverrides.TryGetValue((bank, tile), out var pixels)) return Smb3ZoneBuilder.NesToMd(assets.Chr(bank, tile), pal);
        var md = new byte[32];
        for (int i = 0; i < 64; i++)
        {
            int c = pixels[i] == 0 ? 0 : pal * 4 + pixels[i];
            md[i / 2] |= (byte)((i & 1) == 0 ? c << 4 : c);
        }
        return md;
    }

    /// <summary>A plane cell showing NES background tile <paramref name="nesTile"/> (palette line 2).</summary>
    public void Cell(int plane, int col, int row, int nesTile, int pal, bool high = false) =>
        Word(plane, col, row, BgTile(nesTile, pal) | (2 << 13) | (high ? 0x8000 : 0));

    public void Word(int plane, int col, int row, int word)
    {
        int a = plane + (row & 31) * 128 + (col & 63) * 2;
        vram[a] = (byte)(word >> 8);
        vram[a + 1] = (byte)word;
    }

    /// <summary>One NES nametable row into plane row <paramref name="planeRow"/>; columns outside 0-31 come from
    /// <paramref name="margin"/> (the NES column they should copy, or null for blank);
    /// <paramref name="high"/> picks the columns drawn with high priority (in front of low-priority sprites).</summary>
    public void Row(NesPpu ppu, int nt, int ntRow, int plane, int planeRow, Func<int, int?>? margin = null, Func<int, bool>? high = null)
    {
        for (int c = -4; c < 36; c++)
        {
            int? src = c is >= 0 and < 32 ? c : margin?.Invoke(c);
            if (src is not { } s) { Word(plane, c + 4, planeRow, 0); continue; }
            Cell(plane, c + 4, planeRow, ppu.Tile(nt, s, ntRow), ppu.CellPalette(nt, s, ntRow), high?.Invoke(s) ?? false);
        }
    }

    /// <summary>NES palette RAM to palette lines 2 (background) and 3 (sprites) of the palette at
    /// <paramref name="palette"/> (Normal_palette, or the fade's target while a fade runs).</summary>
    public void Palettes(NesPpu ppu, int palette = Vars.NormalPalette)
    {
        for (int i = 0; i < 16; i++)
        {
            ram.W16(palette + 0x40 + i * 2, Smb3Palettes.ToMd(ppu.Palette[i]));
            ram.W16(palette + 0x60 + i * 2, Smb3Palettes.ToMd(ppu.Palette[(i & 3) == 0 ? 0 : 16 + i]));
        }
    }
}

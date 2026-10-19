namespace S2Port.Extraction.Smb3;

public sealed record Smb3Pipe(int X, int Y, int Direction, string Kind, int TargetX, int TargetY, int ExitDirection)
{
    public int[][] Path { get; init; } = [];
}
public sealed record Smb3Gameplay(int Columns, int Rows, int Tileset, int[] Tiles,
    Smb3Pipe[] Pipes, int InitialExitDirection, bool MapPipeway)
{
    public bool IsWater(int x, int y)
    {
        int c = x / 16, r = y / 16;
        if (x < 0 || y < 0 || c >= Columns || r >= Rows) return false;
        return WaterTile(Tileset, Tiles[r * Columns + c]);
    }

    public static bool WaterTile(int set, int tile) => set switch
    {
        3 or 14 => tile is 0xC2 or 0xC3,
        6 => tile is 0x8B or 0x8C or 0x8F or 0x90,
        1 or 4 or 5 or 9 or 11 or 12 or 13 => tile is >= 0xD8 and <= 0xDE or 0xE0 or 0xE1,
        _ => false,
    };

    public static Smb3Gameplay Build(Smb3DecodedArea area, int tileset, bool mapPipeway)
    {
        int columns = area.Vertical ? 16 : (area.Width + 1) * 16;
        int rows = area.Vertical ? (area.Width + 1) * 15 : 27;
        var tiles = new int[columns * rows];
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < columns; c++) tiles[r * columns + c] = area.Tile(r, c);
        int At(int r, int c) => r < 0 || c < 0 || r >= rows || c >= columns ? -1 : tiles[r * columns + c];
        bool BodyBelow(int r, int c)
        {
            for (int offset = 1; offset <= 8; offset++)
            {
                int t = At(r + offset, c);
                if (t is 0xBA or 0xA1 or 0xA3 or 0xA8) return true;
                if (tileset != 8 || t is not (>= 0x99 and <= 0x9D or >= 0xA5 and <= 0xAC or >= 0xB7 and <= 0xB9)) return false;
            }
            return false;
        }
        var pipes = new List<Smb3Pipe>();
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < columns; c++)
        {
            int tile = At(r, c);
            string? kind = tile switch
            {
                0xAD => "alternate",
                0xAF => "bonus",
                0xB3 => "local",
                0xBD when tileset is 3 or 14 => "common-exit",
                0x91 when tileset == 8 => "local",
                _ => null,
            };
            if (kind is not null && At(r, c + 1) == tile + 1)
            {
                bool below = BodyBelow(r, c);
                int direction = below ? 2 : 1; // Down into a top mouth, Up into a bottom mouth.
                Add(c * 16 + 16, (r + (below ? 0 : 1)) * 16, direction, kind);
            }
            if (tile == 0xB5 && At(r - 1, c) == 0xB7 ||
                tileset == 8 && tile == 0x9E && At(r - 1, c) == 0x9A)
            {
                bool bodyRight = At(r, c + 1) is 0xB9 or 0x9D;
                Add((c + (bodyRight ? 0 : 1)) * 16, r * 16, bodyRight ? 8 : 4,
                    tileset == 8 ? "local" : "alternate");
            }
        }
        for (int i = 0; i < pipes.Count; i++)
        {
            var pipe = pipes[i];
            if (pipe.Kind != "local") continue;
            int x = pipe.X, y = pipe.Y, direction = pipe.Direction;
            var path = new List<int[]> { new[] { x, y } };
            var visited = new HashSet<(int, int, int)>();
            while (visited.Add((x, y, direction)) && x >= 0 && y >= 0 && x <= columns * 16 && y <= rows * 16)
            {
                x += direction == 8 ? 16 : direction == 4 ? -16 : 0;
                y += direction == 2 ? 16 : direction == 1 ? -16 : 0;
                bool wrapped = tileset == 8 && (x < 0 || x >= 256);
                if (wrapped) x = (x + 256) % 256;
                path.Add([x, y]);
                int opposite = direction switch { 1 => 2, 2 => 1, 4 => 8, _ => 4 };
                var end = pipes.FirstOrDefault(p => p.Kind == "local" && p.X == x && p.Y == y &&
                    (p.Direction == opposite || wrapped));
                if (end is not null)
                {
                    pipes[i] = pipe with { TargetX = x, TargetY = y, ExitDirection = direction, Path = path.ToArray() };
                    break;
                }
                if (tileset != 8) continue;
                (int Dx, int Dy)[] probes = direction switch
                {
                    8 => [(10, 14), (10, -14)],
                    4 => [(-6, 14), (-6, -14)],
                    2 => [(6, 14), (-6, 14)],
                    _ => [(6, -14), (-6, -14)],
                };
                for (int probe = 0; probe < 2; probe++)
                    if (At((y + probes[probe].Dy) / 16, (x + probes[probe].Dx) / 16) is >= 0x50 and <= 0x53)
                    {
                        direction = direction is 4 or 8 ? (probe == 0 ? 1 : 2) : (probe == 0 ? 4 : 8);
                        break;
                    }
            }
            if (pipes[i].Path.Length == 0)
                throw new InvalidDataException($"Transit pipe at {pipe.X},{pipe.Y} has no connected source exit.");
        }
        return new(columns, rows, tileset, tiles, pipes.ToArray(),
            (area.Header[7] >> 5) switch { 2 => 1, 3 => 2, 4 => 8, 5 => 4, _ => 0 }, mapPipeway);

        void Add(int x, int y, int direction, string kind)
        {
            int screen = area.Vertical ? Math.Clamp(y / 240, 0, 15) : Math.Clamp(x / 256, 0, 15);
            byte px = area.JunctionX[screen], py = area.JunctionY[screen];
            int targetX = (px & 15) * 256 + (px & 0xF0) + 8;
            int targetY = new[] { 0, 0x40, 0x70, 0xB0, 0xF0, 0x140, 0x170, 0x180 }[(py >> 4) & 7];
            int exit = (py & 15) switch { 1 => 1, 2 => 2, 3 => 8, 4 => 4, _ => 0 };
            if (kind == "common-exit") { targetX = 40; targetY = 384; exit = 1; }
            pipes.Add(new(x, y, direction, mapPipeway || (area.Header[6] & 0x80) == 0 && kind == "alternate" ? "map" : kind,
                targetX, targetY, exit));
        }
    }
}

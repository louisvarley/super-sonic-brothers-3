using S2Port.Validate;

try
{
    if (args.Length >= 2)
        switch (args[0])
        {
            case "smb3-level" when args.Length >= 3: return Smb3LevelCheck.Run(args[1..]);
            case "smb3-level-6502": return Smb3Level6502Check.Run(args[1..]);
            case "smb3-music" when args.Length >= 3: return Smb3MusicCheck.Run(args[1..]);
        }
    Console.Error.WriteLine("usage: smb3-level <smb3.nes> <cpu.bin> [pool hex] | smb3-level-6502 <smb3.nes> [1-1 1-2] | smb3-music <smb3.nes> <apu.csv> [first frame] [frames]");
    return 1;
}
catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

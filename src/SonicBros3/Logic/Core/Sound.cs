namespace SonicBros3.Logic;

/// <summary>Sound requests the logic makes (PlaySound 0x14FA / PlayMusic 0x14DC).</summary>
public interface ISoundRequests
{
    void PlaySound(int id);
    void PlayMusic(int id);
    /// <summary>0x150C: the second SFX queue slot.</summary>
    void PlaySoundSecondary(int id) => PlaySound(id);
}

/// <summary>
/// The original's sound queue in RAM: PlayMusic (0x14DC), PlaySound (0x14FA) and 0x150C write the request bytes the
/// sound driver reads ($FFE0/$FFE4/$FFE1/$FFE2 for the Z80 driver; $F00A/$F00B when the 68k driver is active,
/// flag $FFE5). The audio player consumes them from RAM.
/// </summary>
public sealed class RamSound(Ram ram) : ISoundRequests
{
    private bool Driver68k => ram.U8(Vars.SoundDriverSelect) != 0;

    public void PlayMusic(int id)
    {
        if (Driver68k) ram.W8(Vars.SmpsQueue1, id);
        else if (ram.U8(Vars.MusicToPlay) == 0) ram.W8(Vars.MusicToPlay, id);
        else ram.W8(Vars.MusicToPlay2, id);
    }

    public void PlaySound(int id)
    {
        if (Driver68k) ram.W8(Vars.SmpsQueue2, id);
        else ram.W8(Vars.SfxToPlay, id);
    }

    public void PlaySoundSecondary(int id)
    {
        if (Driver68k) PlaySound(id);
        else ram.W8(Vars.SfxToPlay2, id);
    }
}

public sealed class NoSound : ISoundRequests
{
    public void PlaySound(int id) { }
    public void PlayMusic(int id) { }
}

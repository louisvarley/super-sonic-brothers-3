namespace SonicBros3.Logic;

/// <summary>Sonic's and Tails' animations (anim, $1C; Sonic 2's AniIDSonAni_*). Tails' list matches up to $1E.</summary>
public static class PlayerAnim
{
    public const int Walk = 0, Run = 1, Roll = 2, Roll2 = 3, Push = 4, Wait = 5, Balance = 6, LookUp = 7, Duck = 8,
        Spindash = 9, Blink = 0x0A, GetUp = 0x0B, Balance2 = 0x0C, Stop = 0x0D, Float = 0x0E, Float2 = 0x0F,
        Spring = 0x10, Hang = 0x11, Hang2 = 0x14, Bubble = 0x15, DeathBW = 0x16, Drown = 0x17, Death = 0x18,
        Hurt = 0x19, Hurt2 = 0x1A, Slide = 0x1B, Blank = 0x1C, Balance3 = 0x1D, Balance4 = 0x1E;
    /// <summary>Tails only: flying (the CPU flying him back in).</summary>
    public const int TailsFly = 0x20;
}

/// <summary>Music ids (Sonic 2's MusID_*; the hack's zones play their own songs through the 68k driver).</summary>
public static class Music
{
    public const int Boss = 0x93, Invincible = 0x97, ExtraLife = 0x98, EndLevel = 0x9A, GameOver = 0x9B;
    /// <summary>Driver commands.</summary>
    public const int StopSfx = 0xF8, FadeOut = 0xF9, SpeedUp = 0xFB, SlowDown = 0xFC, Stop = 0xFD;
}

/// <summary>
/// Sound effect ids (Sonic 2's SndID_*). The hack's zones play Sonic 1's effects through the 68k driver; most ids
/// mean the same there (Sonic 2 kept Sonic 1's order), the exceptions are noted where they are used.
/// </summary>
public static class Sfx
{
    public const int Jump = 0xA0, Checkpoint = 0xA1, Hurt = 0xA3, Skidding = 0xA4, HurtBySpikes = 0xA6, Push = 0xA7,
        Splash = 0xAA, BossHit = 0xAC, LavaBall = 0xAE, Shield = 0xAF, Drown = 0xB2, Ring = 0xB5, SpikesMove = 0xB6,
        Smash = 0xB9, SpindashRelease = 0xBC, Hammer = 0xBD, Roll = 0xBE, ContinueJingle = 0xBF, Explosion = 0xC1,
        WaterWarning = 0xC2, EnterGiantRing = 0xC3, BossExplosion = 0xC4, TallyEnd = 0xC5, RingSpill = 0xC6,
        Flamethrower = 0xC8, SlowSmash = 0xCB, Spring = 0xCC, Blip = 0xCD, RingLeft = 0xCE, Signpost = 0xCF,
        Helicopter = 0xDE, SpindashRev = 0xE0;
}

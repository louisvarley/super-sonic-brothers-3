using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Input;
using SonicBros3.Audio;
using S2Port.Extraction.Graphics;
using SonicBros3.Runtime;
using SonicBros3.Frontend;

namespace SonicBros3;

/// <summary>MonoGame host: steps the game flow (title, level select, levels) at 60 Hz, uploads the VDP frame and scales it to the window.</summary>
public sealed class S2Game : Microsoft.Xna.Framework.Game
{
    private const int Scale = 3;
    private readonly GameFlow _flow;
    private readonly GraphicsDeviceManager _graphics;
    private readonly uint[] _rgba = new uint[Vdp.Width * Vdp.Height];
    private SpriteBatch? _batch;
    private Texture2D? _frame;
    private readonly SoundMixer? _mixer;
    private DynamicSoundEffectInstance? _sound;
    private readonly short[] _soundFrame = new short[800 * 2];
    private readonly byte[] _soundBytes = new byte[800 * 4];

    public S2Game(GameFlow flow, SoundMixer? mixer = null)
    {
        _flow = flow;
        _mixer = mixer;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Vdp.Width * Scale,
            PreferredBackBufferHeight = Vdp.Height * Scale,
        };
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1 / 60.0);
        Window.Title = "Sonic Bros. 3";
    }

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);
        _frame = new Texture2D(GraphicsDevice, Vdp.Width, Vdp.Height);
        if (_mixer is not null)
        {
            try
            {
                _sound = new DynamicSoundEffectInstance(_mixer.OutputRate, AudioChannels.Stereo);
                _sound.Play();
            }
            catch (Exception e)   // no audio device: play on silently
            {
                Console.Error.WriteLine($"Audio unavailable: {e.Message}");
                _sound = null;
            }
        }
    }

    /// <summary>One frame of audio per logic step (800 samples at 48 kHz); a short queue is kept primed.</summary>
    private void SubmitAudio()
    {
        if (_sound is null || _mixer is null) return;
        if (_sound.PendingBufferCount > 6) return;   // running ahead of the audio clock: drop this frame's audio
        int count = _sound.PendingBufferCount < 2 ? 2 : 1;
        for (int n = 0; n < count; n++)
        {
            _mixer.Render(_soundFrame, 800);
            Buffer.BlockCopy(_soundFrame, 0, _soundBytes, 0, _soundBytes.Length);
            _sound.SubmitBuffer(_soundBytes);
        }
    }

    protected override void Update(GameTime gameTime)
    {
        var k = Keyboard.GetState();
        if (k.IsKeyDown(Keys.Escape)) Exit();
        if (_flow.Session is { FreeCamera: true } free)
        {
            int speed = k.IsKeyDown(Keys.LeftShift) ? 16 : 4;
            if (k.IsKeyDown(Keys.Left)) free.CameraX -= speed;
            if (k.IsKeyDown(Keys.Right)) free.CameraX += speed;
            if (k.IsKeyDown(Keys.Up)) free.CameraY -= speed;
            if (k.IsKeyDown(Keys.Down)) free.CameraY += speed;
        }
        if (k.IsKeyDown(Keys.R) && _flow.Session is { IsStopped: true } stopped) stopped.Restart();
        _flow.Step(PadButtons(k));
        SubmitAudio();
        string title = _flow.StopMessage is { } e ? $"Sonic Bros. 3 - stopped: {e}" : "Sonic Bros. 3";
        if (Window.Title != title)
        {
            Window.Title = title;
            if (_flow.StopMessage is not null) Console.Error.WriteLine(title);
        }
        base.Update(gameTime);
    }

    /// <summary>Keyboard to Mega Drive pad bits: arrows, Z/X/C = A/B/C, Enter = Start.</summary>
    private static int PadButtons(KeyboardState k)
    {
        int b = 0;
        if (k.IsKeyDown(Keys.Up)) b |= 0x01;
        if (k.IsKeyDown(Keys.Down)) b |= 0x02;
        if (k.IsKeyDown(Keys.Left)) b |= 0x04;
        if (k.IsKeyDown(Keys.Right)) b |= 0x08;
        if (k.IsKeyDown(Keys.X)) b |= 0x10;   // B
        if (k.IsKeyDown(Keys.C)) b |= 0x20;   // C
        if (k.IsKeyDown(Keys.Z)) b |= 0x40;   // A
        if (k.IsKeyDown(Keys.Enter)) b |= 0x80;
        return b;
    }

    protected override void Draw(GameTime gameTime)
    {
        _flow.Render();
        _flow.Vdp.ToRgba(_rgba);
        _frame!.SetData(_rgba);

        GraphicsDevice.Clear(Color.Black);
        _batch!.Begin(samplerState: SamplerState.PointClamp);
        _batch.Draw(_frame, new Rectangle(0, 0, Vdp.Width * Scale, Vdp.Height * Scale), Color.White);
        _batch.End();
        base.Draw(gameTime);
    }
}

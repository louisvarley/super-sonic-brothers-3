using System.Text;

namespace S2Port.Extraction.Audio;

/// <summary>Minimal PCM WAV writer.</summary>
public static class Wav
{
    public static void WriteUnsigned8Mono(string path, ReadOnlySpan<byte> samples, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + samples.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(sampleRate);
        w.Write(sampleRate);        // byte rate
        w.Write((short)1);          // block align
        w.Write((short)8);          // bits
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(samples.Length);
        w.Write(samples);
    }
}

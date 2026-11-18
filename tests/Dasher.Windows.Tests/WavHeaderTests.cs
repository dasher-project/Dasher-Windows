using System.Linq;
using Dasher.Windows.Speech;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// SpeechService.BuildWavHeader: the Rust bindings' sherpa
/// SynthToBytesAsync returns headerless 16-bit PCM, and the in-process
/// player needs a canonical WAV header. These pin the header layout.
/// </summary>
public class WavHeaderTests
{
    [Fact]
    public void Header_is_canonical_wav()
    {
        var pcm = new byte[] { 0x04, 0x00, 0x03, 0x00, 0x01, 0x00, 0xFF, 0xFF };
        var wav = SpeechService.BuildWavHeader(pcm, 24000, 1);

        Assert.Equal("RIFF"u8.ToArray(), wav.Take(4).ToArray());
        Assert.Equal("WAVE"u8.ToArray(), wav.Skip(8).Take(4).ToArray());
        Assert.Equal("fmt "u8.ToArray(), wav.Skip(12).Take(4).ToArray());
        using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(wav));
        r.BaseStream.Position = 16;
        Assert.Equal(16, r.ReadInt32());      // fmt chunk size
        Assert.Equal((short)1, r.ReadInt16()); // PCM
        Assert.Equal((short)1, r.ReadInt16()); // channels
        Assert.Equal(24000, r.ReadInt32());    // sample rate
        Assert.Equal(48000, r.ReadInt32());    // byte rate = rate * blockAlign
        Assert.Equal((short)2, r.ReadInt16()); // block align
        Assert.Equal((short)16, r.ReadInt16());// bits
        r.BaseStream.Position = 36;
        Assert.Equal("data"u8.ToArray(), System.Linq.Enumerable.ToArray(r.ReadBytes(4)));
        Assert.Equal(pcm.Length, r.ReadInt32());
        Assert.Equal(pcm, r.ReadBytes(pcm.Length));
        Assert.Equal(44 + pcm.Length, wav.Length);
        // RIFF size field = file size - 8
        r.BaseStream.Position = 4;
        Assert.Equal(wav.Length - 8, r.ReadInt32());
    }

    [Fact]
    public void Stereo_doubles_block_align_and_byte_rate()
    {
        var wav = SpeechService.BuildWavHeader(new byte[4000], 22050, 2);
        using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(wav));
        r.BaseStream.Position = 16 + 8;
        Assert.Equal(22050, r.ReadInt32());
        Assert.Equal(22050 * 4, r.ReadInt32()); // 22050 * 2ch * 2 bytes
        Assert.Equal((short)4, r.ReadInt16());
    }
}

using System.Buffers.Binary;

namespace DeviceDeck.Core.Toolbox;

/// <summary>
/// Trims WAV (PCM, cut on sample boundaries) and MP3 (cut on frame boundaries, no re-encoding) without any codec.
/// Other formats go to the Shell's Media Foundation trimmer.
/// </summary>
public sealed class ManagedAudioTrimmer : IAudioTrimmer
{
    /// <summary>Audio files are read whole; anything bigger is not a ringtone source anyone needs.</summary>
    public const long MaxInputBytes = 512L * 1024 * 1024;

    public bool CanTrim(string extension) =>
        extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);

    public async Task<string> TrimAsync(string source, TimeSpan start, TimeSpan end, string destinationWithoutExtension, CancellationToken ct)
    {
        if (end <= start)
        {
            throw new ArgumentException("The end must come after the start.");
        }

        var info = new FileInfo(source);
        if (info.Length > MaxInputBytes)
        {
            throw new InvalidDataException("That audio file is too big to cut here.");
        }

        var bytes = await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var output = extension switch
        {
            ".wav" => TrimWav(bytes, start, end),
            ".mp3" => TrimMp3(bytes, start, end),
            _ => throw new NotSupportedException($"{extension} files are not cut here."),
        };
        var path = destinationWithoutExtension + extension;
        await File.WriteAllBytesAsync(path, output, ct).ConfigureAwait(false);
        return path;
    }

    // ---- WAV ----

    private readonly ref struct Wav(ReadOnlySpan<byte> fmt, int dataOffset, int dataLength)
    {
        public ReadOnlySpan<byte> Fmt { get; } = fmt;
        public int DataOffset { get; } = dataOffset;
        public int DataLength { get; } = dataLength;
        public uint ByteRate => BinaryPrimitives.ReadUInt32LittleEndian(Fmt.Slice(8, 4));
        public ushort BlockAlign => BinaryPrimitives.ReadUInt16LittleEndian(Fmt.Slice(12, 2));
    }

    private static Wav ParseWav(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("This isn't a WAV file.");
        }

        ReadOnlySpan<byte> fmt = default;
        var at = 12;
        while (at + 8 <= wav.Length)
        {
            var id = wav.Slice(at, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(at + 4, 4)), (uint)(wav.Length - at - 8));
            if (id.SequenceEqual("fmt "u8))
            {
                fmt = wav.Slice(at + 8, size);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (fmt.Length < 16)
                {
                    break;
                }

                var parsed = new Wav(fmt, at + 8, size);
                if (parsed.ByteRate == 0 || parsed.BlockAlign == 0)
                {
                    throw new InvalidDataException("The WAV file's format is broken.");
                }

                return parsed;
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidDataException("The WAV file has no audio data.");
    }

    /// <summary>Keeps the fmt chunk as it is and the data chunk's bytes between start and end, aligned to whole sample frames.</summary>
    public static byte[] TrimWav(ReadOnlySpan<byte> wav, TimeSpan start, TimeSpan end)
    {
        var parsed = ParseWav(wav);
        var fmt = parsed.Fmt;
        uint byteRate = parsed.ByteRate;
        int blockAlign = parsed.BlockAlign;
        var whole = parsed.DataLength / blockAlign * blockAlign;
        long Offset(TimeSpan t) => Math.Clamp((long)(t.TotalSeconds * byteRate) / blockAlign * blockAlign, 0, whole);
        var from = Offset(start);
        var to = Offset(end);
        if (to <= from)
        {
            throw new InvalidDataException("That part of the file has no audio.");
        }

        var cut = (int)(to - from);
        var fmtPadded = fmt.Length + (fmt.Length & 1);
        var result = new byte[12 + 8 + fmtPadded + 8 + cut];
        var span = result.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(result.Length - 8));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)fmt.Length);
        fmt.CopyTo(span[20..]);
        var data = 20 + fmtPadded;
        "data"u8.CopyTo(span[data..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(data + 4)..], (uint)cut);
        wav.Slice(parsed.DataOffset + (int)from, cut).CopyTo(span[(data + 8)..]);
        return result;
    }

    // ---- MP3 ----

    private static readonly int[] BitratesV1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] BitratesV2L3 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
    private static readonly int[] RatesV1 = [44100, 48000, 32000, 0];

    /// <summary>One MPEG audio layer III frame header.</summary>
    public readonly record struct Mp3Frame(int Offset, int Length, int SampleRate, int Samples);

    /// <summary>The frame starting at <paramref name="at"/>, or null when the bytes there aren't a layer III header.</summary>
    public static Mp3Frame? ReadFrame(ReadOnlySpan<byte> mp3, int at)
    {
        if (at + 4 > mp3.Length || mp3[at] != 0xFF || (mp3[at + 1] & 0xE0) != 0xE0)
        {
            return null;
        }

        var version = (mp3[at + 1] >> 3) & 3; // 0 = MPEG 2.5, 2 = MPEG 2, 3 = MPEG 1
        var layer = (mp3[at + 1] >> 1) & 3;   // 1 = layer III
        var bitrateIndex = mp3[at + 2] >> 4;
        var rateIndex = (mp3[at + 2] >> 2) & 3;
        var padding = (mp3[at + 2] >> 1) & 1;
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3)
        {
            return null;
        }

        var mpeg1 = version == 3;
        var bitrate = (mpeg1 ? BitratesV1L3 : BitratesV2L3)[bitrateIndex] * 1000;
        var rate = RatesV1[rateIndex] / (version switch { 3 => 1, 2 => 2, _ => 4 });
        var length = (mpeg1 ? 144 : 72) * bitrate / rate + padding;
        return new Mp3Frame(at, length, rate, mpeg1 ? 1152 : 576);
    }

    /// <summary>Every audio frame, after any ID3v2 tag and without the Xing / Info / VBRI header frame.</summary>
    public static List<Mp3Frame> ReadFrames(ReadOnlySpan<byte> mp3)
    {
        var at = 0;
        if (mp3.Length >= 10 && mp3[..3].SequenceEqual("ID3"u8))
        {
            var size = (mp3[6] & 0x7F) << 21 | (mp3[7] & 0x7F) << 14 | (mp3[8] & 0x7F) << 7 | (mp3[9] & 0x7F);
            at = 10 + size + ((mp3[5] & 0x10) != 0 ? 10 : 0);
        }

        var frames = new List<Mp3Frame>();
        while (at + 4 <= mp3.Length)
        {
            if (mp3.Length - at >= 3 && mp3.Slice(at, 3).SequenceEqual("TAG"u8))
            {
                break; // ID3v1 at the end
            }

            if (ReadFrame(mp3, at) is { } frame && at + frame.Length <= mp3.Length
                && (at + frame.Length == mp3.Length || frames.Count > 0 || ReadFrame(mp3, at + frame.Length) is not null))
            {
                frames.Add(frame);
                at += frame.Length;
            }
            else
            {
                at++; // resync
            }
        }

        if (frames.Count > 0)
        {
            var first = mp3.Slice(frames[0].Offset, Math.Min(frames[0].Length, 64));
            if (first.IndexOf("Xing"u8) >= 0 || first.IndexOf("Info"u8) >= 0 || first.IndexOf("VBRI"u8) >= 0)
            {
                frames.RemoveAt(0);
            }
        }

        return frames;
    }

    public static byte[] TrimMp3(ReadOnlySpan<byte> mp3, TimeSpan start, TimeSpan end)
    {
        var frames = ReadFrames(mp3);
        if (frames.Count == 0)
        {
            throw new InvalidDataException("This MP3 file has no audio frames DeviceDeck can cut.");
        }

        using var output = new MemoryStream();
        double time = 0;
        foreach (var frame in frames)
        {
            var duration = (double)frame.Samples / frame.SampleRate;
            if (time >= start.TotalSeconds - duration / 2 && time < end.TotalSeconds - duration / 2)
            {
                output.Write(mp3.Slice(frame.Offset, frame.Length));
            }

            time += duration;
        }

        if (output.Length == 0)
        {
            throw new InvalidDataException("That part of the file has no audio.");
        }

        return output.ToArray();
    }

    /// <summary>Length of an MP3 or WAV file in seconds, for checks; null when it can't be read.</summary>
    public static double? DurationSeconds(ReadOnlySpan<byte> bytes, string extension)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var frames = ReadFrames(bytes);
            return frames.Count == 0 ? null : frames.Sum(f => (double)f.Samples / f.SampleRate);
        }

        try
        {
            var wav = ParseWav(bytes);
            return (double)wav.DataLength / wav.ByteRate;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

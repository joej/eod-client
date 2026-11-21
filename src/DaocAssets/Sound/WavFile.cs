// SPDX-License-Identifier: GPL-3.0-or-later
//
// The install's .wav files (roadmap M12), decoded to 16-bit PCM for the client to play. The install holds three kinds
// (counted 2026-11-21 over the 2,703 in sounds/): PCM 16-bit (2,110, mono and stereo, 11,025 and 22,050 Hz) and IMA ADPCM
// (format 0x11, 593 files, 4 bits per sample, mono). Godot plays PCM from memory but not the IMA blocks as written, so
// both come out as PCM here. IMA ADPCM is the standard Intel/DVI one: per channel and block, a 4-byte header (first
// sample, step index), then nibbles low first, 8 per 4-byte word per channel.

using System.Buffers.Binary;

namespace DaocAssets.Sound;

/// <summary>A decoded sound: interleaved 16-bit little-endian samples.</summary>
public sealed record WavFile(int Channels, int SampleRate, byte[] Pcm16)
{
    public int Frames => Pcm16.Length / (2 * Channels);
    public double Seconds => (double)Frames / SampleRate;

    public static WavFile Decode(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("not a RIFF WAVE file");
        int format = 0, channels = 0, rate = 0, bits = 0, blockAlign = 0;
        ReadOnlySpan<byte> data = default;
        for (int at = 12; at + 8 <= file.Length;)
        {
            ReadOnlySpan<byte> id = file.Slice(at, 4);
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 4)..]), (uint)(file.Length - at - 8));
            ReadOnlySpan<byte> body = file.Slice(at + 8, size);
            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
            }
            else if (id.SequenceEqual("data"u8))
                data = body;
            at += 8 + size + (size & 1);
        }
        if (channels is < 1 or > 2 || rate <= 0)
            throw new InvalidDataException($"unsupported wave: {channels} channels at {rate} Hz");
        return format switch
        {
            1 when bits == 16 => new WavFile(channels, rate, data[..(data.Length & ~1)].ToArray()),
            1 when bits == 8 => new WavFile(channels, rate, FromU8(data)),
            0x11 => new WavFile(channels, rate, FromImaAdpcm(data, channels, blockAlign)),
            _ => throw new InvalidDataException($"unsupported wave format {format} ({bits} bits)"),
        };
    }

    private static byte[] FromU8(ReadOnlySpan<byte> data)
    {
        var pcm = new byte[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)((data[i] - 128) << 8));
        return pcm;
    }

    private static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107,
        118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894,
        6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
        32767,
    ];

    private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

    private static byte[] FromImaAdpcm(ReadOnlySpan<byte> data, int channels, int blockAlign)
    {
        if (blockAlign < 4 * channels)
            throw new InvalidDataException($"IMA ADPCM block of {blockAlign} bytes");
        int framesPerBlock = (blockAlign - 4 * channels) * 8 / (4 * channels) + 1;
        var output = new List<short>(data.Length / blockAlign * framesPerBlock * channels + 64);
        var predictor = new int[channels];
        var index = new int[channels];
        var block = new short[framesPerBlock * channels];
        for (int b = 0; b + 4 * channels <= data.Length; b += blockAlign)
        {
            ReadOnlySpan<byte> blk = data.Slice(b, Math.Min(blockAlign, data.Length - b));
            for (int c = 0; c < channels; c++)
            {
                predictor[c] = BinaryPrimitives.ReadInt16LittleEndian(blk[(4 * c)..]);
                index[c] = Math.Clamp((int)blk[4 * c + 2], 0, 88);
                block[c] = (short)predictor[c];
            }
            int frames = 1;
            int at = 4 * channels;
            // Each step: 4 bytes (8 samples) per channel, channels in turn.
            while (at + 4 * channels <= blk.Length)
            {
                for (int c = 0; c < channels; c++)
                    for (int i = 0; i < 8; i++)
                    {
                        int nibble = blk[at + 4 * c + i / 2] >> (i % 2 * 4) & 0xF;
                        block[(frames + i) * channels + c] = Next(nibble, ref predictor[c], ref index[c]);
                    }
                frames += 8;
                at += 4 * channels;
            }
            for (int i = 0; i < frames * channels; i++)
                output.Add(block[i]);
        }
        var pcm = new byte[output.Count * 2];
        for (int i = 0; i < output.Count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), output[i]);
        return pcm;
    }

    private static short Next(int nibble, ref int predictor, ref int index)
    {
        int step = StepTable[index];
        int diff = step >> 3;
        if ((nibble & 4) != 0) diff += step;
        if ((nibble & 2) != 0) diff += step >> 1;
        if ((nibble & 1) != 0) diff += step >> 2;
        predictor = Math.Clamp((nibble & 8) != 0 ? predictor - diff : predictor + diff, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[nibble], 0, 88);
        return (short)predictor;
    }
}

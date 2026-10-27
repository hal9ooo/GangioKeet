using System.Text;

namespace GangioKeet.Eval;

public sealed record WavInput(byte[] Pcm, double DurationSeconds)
{
    public static WavInput Read(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 12) throw new InvalidDataException("A complete seekable RIFF WAV is required.");
        using BinaryReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a RIFF WAV.");
        long end = 8L + reader.ReadUInt32();
        if (end > stream.Length || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("Truncated or invalid WAV.");
        bool validFormat = false;
        byte[]? pcm = null;
        while (stream.Position + 8 <= end)
        {
            string kind = Encoding.ASCII.GetString(reader.ReadBytes(4));
            uint size = reader.ReadUInt32();
            long next = stream.Position + size + (size % 2);
            if (next > end) throw new InvalidDataException("Truncated WAV chunk.");
            if (kind == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("Incomplete WAV format.");
                ushort format = reader.ReadUInt16(), channels = reader.ReadUInt16();
                uint rate = reader.ReadUInt32(), bytesPerSecond = reader.ReadUInt32();
                ushort alignment = reader.ReadUInt16(), bits = reader.ReadUInt16();
                validFormat = format == 1 && channels == 1 && rate == 16000 && bits == 16
                    && alignment == 2 && bytesPerSecond == 32000;
            }
            else if (kind == "data")
            {
                if (pcm is not null || size is 0 or > 67_108_864 || size % 2 != 0)
                    throw new InvalidDataException("WAV must contain one bounded, nonempty PCM16 payload.");
                pcm = reader.ReadBytes((int)size);
            }
            stream.Position = next;
        }
        if (!validFormat || pcm is null) throw new InvalidDataException("WAV must be PCM16 mono 16000 Hz.");
        return new WavInput(pcm, pcm.Length / 32000d);
    }
}

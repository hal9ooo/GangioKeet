using System;
using System.Threading;

namespace GangioKeet.Client.Audio;

/// <summary>
/// Buffer RAM-only del dettato: incorpora fino a <c>maxBytes</c> byte (64 MiB, backlog incluso).
/// <see cref="TryAppend"/> è tutto-o-niente: oltre il limite viene rifiutata senza appendere nemmeno
/// una parte del chunk. <see cref="Snapshot"/> restituisce una copia indipendente dal buffer di
/// cattura e da append successivi; <see cref="Clear"/> rilascia i riferimenti interni.
/// </summary>
public sealed class RamAudioBuffer(int maxBytes = 67108864)
{
    public const int DefaultMaxBytes = 67108864;

    private const int MinCapacity = 64 * 1024;

    private byte[] _data = [];
    private int _length;

    public int Length => Volatile.Read(ref _length);

    public bool TryAppend(ReadOnlySpan<byte> pcm)
    {
        int current = _length;
        if (current + pcm.Length > maxBytes)
        {
            return false;
        }

        EnsureCapacity(current + pcm.Length);
        pcm.CopyTo(_data.AsSpan(current));
        _length = current + pcm.Length;
        return true;
    }

    public byte[] Snapshot()
    {
        int current = Volatile.Read(ref _length);
        byte[] copy = new byte[current];
        _data.AsSpan(0, current).CopyTo(copy);
        return copy;
    }

    public void Clear()
    {
        _data = Array.Empty<byte>();
        _length = 0;
    }

    private void EnsureCapacity(int required)
    {
        if (_data.Length >= required)
        {
            return;
        }

        int next = Math.Max(_data.Length * 2, required);
        if (next < MinCapacity)
        {
            next = MinCapacity;
        }

        if (next > maxBytes)
        {
            next = maxBytes;
        }

        byte[] grown = new byte[next];
        _data.AsSpan(0, _length).CopyTo(grown);
        _data = grown;
    }
}

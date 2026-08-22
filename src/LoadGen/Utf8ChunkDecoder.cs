using System.Text;

namespace SevenDTD.LoadGen;

/// <summary>
/// Stateful UTF-8 decoder for stream chunks. TCP reads split multi-byte
/// sequences at arbitrary byte offsets; decoding each chunk independently
/// turns every split into U+FFFD. A persistent Decoder carries the pending
/// bytes across calls instead.
/// </summary>
internal sealed class Utf8ChunkDecoder
{
    readonly Decoder _dec = Encoding.UTF8.GetDecoder();

    /// <summary>Decode one chunk, resuming any sequence left incomplete by the
    /// previous call. Output is bounded by chunk length plus 2: completing a
    /// pending 4-byte astral sequence emits a whole surrogate pair on top of
    /// this chunk's own characters.</summary>
    public string Decode(ReadOnlySpan<byte> chunk)
    {
        var chars = new char[chunk.Length + 2];
        int n = _dec.GetChars(chunk, chars, flush: false);
        return new string(chars, 0, n);
    }

    public void Reset() => _dec.Reset();
}

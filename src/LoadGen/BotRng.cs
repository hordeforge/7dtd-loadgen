namespace SevenDTD.LoadGen;

/// <summary>
/// Per-bot RNG stream seeds. Every bot in a cohort draws from
/// <c>actionSeed + clientId + life * 997</c>, so neighbouring bots hand
/// <see cref="Random"/> seeds that sit next to each other. A seeded
/// <see cref="Random"/> does not spread nearby seeds: its opening values land
/// on a fixed lattice (consecutive seeds are about 0.045 apart, and seeds two
/// apart repeat the same step), so bots whose client ids differ by 2 start with
/// near-identical headings and think times, and a cohort drifts into the lockstep
/// burst the pace jitter exists to break. Mixing the bits first costs one
/// multiply-xorshift round and removes the structure while keeping the seed a
/// sufficient key: the same seed always yields the same stream, so a run still
/// replays from the recorded seed.
/// </summary>
public static class BotRng
{
    /// <summary>Mix a bot's identity seed into a well-spread <see cref="Random"/> seed.</summary>
    public static int Decorrelate(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16;
            x *= 0x85ebca6bu;
            x ^= x >> 13;
            x *= 0xc2b2ae35u;
            x ^= x >> 16;
            return (int)x;
        }
    }
}

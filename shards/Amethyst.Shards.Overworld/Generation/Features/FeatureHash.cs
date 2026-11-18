namespace Amethyst.Generation.Features;

/// <summary>Stable, order-independent feature randomness. Never use Random.Shared here.</summary>
internal static class FeatureHash
{
    public static ulong Mix(ulong x)
    {
        unchecked
        {
            x ^= x >> 30; x *= 0xbf58476d1ce4e5b9UL;
            x ^= x >> 27; x *= 0x94d049bb133111ebUL;
            return x ^ (x >> 31);
        }
    }

    public static ulong At(long seed, long x, long y, long z, ulong salt)
    {
        unchecked
        {
            return Mix(unchecked((ulong)seed) ^ salt ^
                Mix(unchecked((ulong)x) * 0x9e3779b185ebca87UL) ^
                Mix(unchecked((ulong)y) * 0xc2b2ae3d27d4eb4fUL) ^
                Mix(unchecked((ulong)z) * 0x165667b19e3779f9UL));
        }
    }

    public static double Unit(ulong h) => (h >> 11) * (1.0 / (1UL << 53));
    public static int Index(ulong h, int n) => (int)(h % (uint)n);
    public static int FloorDiv(int n, int divisor)
    {
        int q = n / divisor, r = n % divisor;
        return r < 0 ? q - 1 : q;
    }
}

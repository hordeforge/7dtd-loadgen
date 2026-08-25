using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// PackageCodec's BinaryReader/BinaryWriter paths serialize in host byte
/// order (see the PackageCodec header note). That matches the little-endian
/// game wire format only while the runtime itself is little-endian; .NET 5+
/// ships no big-endian targets, so the invariant holds today. Pin it: if a
/// future runtime ever violates it, this fails before wire frames corrupt.
/// </summary>
public sealed class WireEndiannessTests
{
    [Fact]
    public void HostIsLittleEndian()
    {
        Assert.True(BitConverter.IsLittleEndian);
    }
}

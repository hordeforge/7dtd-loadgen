using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// --spawn-entity comma-list resolution for telnet pressure waves. A list with
/// no entries (",", " , ") must fall back to the default mix: the spawn loop
/// indexes types[i % types.Length], so an empty selection used to raise
/// DivideByZeroException on every pressure wave instead of spawning.
/// </summary>
public sealed class TelnetSpawnEntityTypesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetOrBlank_FallsBackToDefaultMix(string? entityName)
    {
        string[] types = TelnetAdmin.ResolveSpawnEntityTypes(entityName);
        Assert.Equal(new[] { "zombieBoe", "zombieSteve", "zombieArlene" }, types);
    }

    [Theory]
    [InlineData(",")]
    [InlineData(" , , ")]
    [InlineData(",,,")]
    public void CommaOnlyList_SplitsToEmptyEntries_FallsBackToDefaultMix(string entityName)
    {
        string[] types = TelnetAdmin.ResolveSpawnEntityTypes(entityName);
        Assert.NotEmpty(types);
        Assert.Equal(new[] { "zombieBoe", "zombieSteve", "zombieArlene" }, types);
    }

    [Theory]
    [InlineData("zombieBoe", new[] { "zombieBoe" })]
    [InlineData("zombieFatCop,zombieDemolition", new[] { "zombieFatCop", "zombieDemolition" })]
    [InlineData(" a , b ", new[] { "a", "b" })]
    public void ExplicitList_IsKept_TrimmedInOrder(string entityName, string[] expected)
        => Assert.Equal(expected, TelnetAdmin.ResolveSpawnEntityTypes(entityName));
}

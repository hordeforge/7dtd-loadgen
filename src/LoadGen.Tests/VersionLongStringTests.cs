using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// PackageCodec.VersionLongString packs Minor as Major.(Minor/10).(Minor%10)
/// for EGameReleaseType.V with Major &gt;= 3 (VersionInformation.LongStringNoBuild).
///
/// The Minor &gt;= 10 strings here are not sourced from a real VersionInformation
/// build: the live-observed accepted display forms are "V 3.0.1" and "V 3.1.0"
/// (checked against a stock VersionAuthorizer). The current pin, V3.4.0 (b10),
/// derives "V 3.4.0" from the shipped Constants..cctor IL; a live V3.4.0 join on
/// 2026-11-05 accepted it. The Minor=11 case asserts that the divisor logic
/// separates the three Minors, not a captured value.
/// </summary>
public sealed class VersionLongStringTests
{
    [Fact]
    public void Minor1_Major3_PacksTo_V_3_0_1()
    {
        var v = new PackageCodec.VersionInfo(ReleaseType: 1, Major: 3, Minor: 1, Build: 0);
        Assert.Equal("V 3.0.1", PackageCodec.VersionLongString(v));
    }

    [Fact]
    public void Minor1_vs_10_vs_11_ProduceDistinctStrings()
    {
        var s1 = PackageCodec.VersionLongString(new PackageCodec.VersionInfo(1, 3, 1, 0));
        var s10 = PackageCodec.VersionLongString(new PackageCodec.VersionInfo(1, 3, 10, 0));
        var s11 = PackageCodec.VersionLongString(new PackageCodec.VersionInfo(1, 3, 11, 0));

        // The divisor packing must split these three Minors apart. The single
        // pre-existing Minor=1 test could not catch a Minor>=10 packing regression.
        Assert.NotEqual(s1, s10);
        Assert.NotEqual(s1, s11);
        Assert.NotEqual(s10, s11);
    }

    [Fact]
    public void Minor10_And_11_FollowDocumentedDivisorLayout()
    {
        // Derived from the documented Major.(Minor/10).(Minor%10) packing, not
        // from a real VersionInformation build (see the class note).
        Assert.Equal("V 3.1.0", PackageCodec.VersionLongString(new PackageCodec.VersionInfo(1, 3, 10, 0)));
        Assert.Equal("V 3.1.1", PackageCodec.VersionLongString(new PackageCodec.VersionInfo(1, 3, 11, 0)));
        // V3.4.0 (b10) pin: Minor 40 packs to the display form the login gate
        // compares (Constants..cctor builds (1, 3, 40, 10)).
        Assert.Equal("V 3.4.0", PackageCodec.VersionLongString(new PackageCodec.VersionInfo(1, 3, 40, 10)));
    }

    [Fact]
    public void LoginVersion_IsTheDisplayForm_Empirically()
    {
        // EMPIRICAL 2026-08-22 (live stock V3.1.0 b14): the VersionAuthorizer
        // ACCEPTS compVersion "V 3.1.0" and KICKS "V 3.10"
        // (EKickReason.VersionMismatch=4). b5c3069 switched the login to
        // LongStringNoBuild ("V 3.10", the raw-Minor form) and every stock join
        // then failed until reverted. Keep the login on VersionLongString and
        // pin it apart from that rejected raw-Minor form so the regression
        // cannot silently return.
        var v310 = new PackageCodec.VersionInfo(ReleaseType: 1, Major: 3, Minor: 10, Build: 14);
        Assert.Equal("V 3.1.0", PackageCodec.VersionLongString(v310));
        Assert.NotEqual("V 3.10", PackageCodec.VersionLongString(v310));
    }
}

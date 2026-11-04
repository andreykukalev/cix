using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;

namespace Cix.Exchange.RevolutX.Tests;

public sealed class BouncyCastleEd25519SignerTests
{
    // RFC 8032, section 7.1, test 1 (public test vector, not a real credential).
    private const string Rfc8032Test1Seed = "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
    private const string Rfc8032Test1Signature =
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
        "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b";

    [Fact]
    public void Sign_Rfc8032Vector_MatchesExpectedSignature()
    {
        var signer = BouncyCastleEd25519Signer.FromSeed(Convert.FromHexString(Rfc8032Test1Seed));

        var signature = signer.Sign(ReadOnlySpan<byte>.Empty);

        Assert.Equal(Rfc8032Test1Signature, Convert.ToHexStringLower(signature));
    }

    [Fact]
    public void FromPem_Pkcs8Key_SignsLikeTheSameSeed()
    {
        // Cross-checked with Python 'cryptography' using the same seed.
        const string expected = "pHF2VytIzzsCtOsIJVNVKG3uLZAZLH/2zIxoCsBDO8ZyYAG/kJLWpnLMbrv0pH+w1Q9Vd5Tv+RbUa4HzbgBtBQ==";
        var pem = ToPkcs8Pem(Convert.FromHexString(Rfc8032Test1Seed));
        var message = Encoding.UTF8.GetBytes("1765360896219GET/api/1.0/balances");

        var signature = BouncyCastleEd25519Signer.FromPem(pem).Sign(message);

        Assert.Equal(expected, Convert.ToBase64String(signature));
    }

    [Fact]
    public void FromPem_NotAPrivateKey_Throws()
    {
        Assert.Throws<FormatException>(() => BouncyCastleEd25519Signer.FromPem("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----"));
    }

    [Fact]
    public void FromSeed_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => BouncyCastleEd25519Signer.FromSeed(new byte[16]));
    }

    // Builds the PEM at test time so no private key file lives in the repo.
    private static string ToPkcs8Pem(byte[] seed)
    {
        var info = PrivateKeyInfoFactory.CreatePrivateKeyInfo(new Ed25519PrivateKeyParameters(seed, 0));
        var base64 = Convert.ToBase64String(info.GetEncoded(), Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN PRIVATE KEY-----\n{base64}\n-----END PRIVATE KEY-----\n";
    }
}

public sealed class SlidingWindowRateLimiterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reserve_UnderLimit_NoWait()
    {
        var limiter = new SlidingWindowRateLimiter(perSecond: 3, perMinute: 10);

        Assert.All(Enumerable.Range(0, 3), _ => Assert.Equal(TimeSpan.Zero, limiter.Reserve(T0)));
    }

    [Fact]
    public void Reserve_PerSecondFull_WaitsUntilOldestExpires()
    {
        var limiter = new SlidingWindowRateLimiter(perSecond: 3, perMinute: 10);
        limiter.Reserve(T0);
        limiter.Reserve(T0.AddMilliseconds(100));
        limiter.Reserve(T0.AddMilliseconds(200));

        var wait = limiter.Reserve(T0.AddMilliseconds(300));

        Assert.Equal(TimeSpan.FromMilliseconds(700), wait);
    }

    [Fact]
    public void Reserve_PerMinuteFull_WaitsForMinuteWindow()
    {
        var limiter = new SlidingWindowRateLimiter(perSecond: 5, perMinute: 5);
        for (var i = 0; i < 5; i++)
            limiter.Reserve(T0.AddSeconds(i * 2));

        var wait = limiter.Reserve(T0.AddSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(50), wait);
    }

    [Fact]
    public void Reserve_DeepQueue_NeverOverbooksAWindow()
    {
        var limiter = new SlidingWindowRateLimiter(perSecond: 1, perMinute: 100);

        var waits = Enumerable.Range(0, 6).Select(_ => limiter.Reserve(T0).TotalSeconds).ToList();

        Assert.Equal(new double[] { 0, 1, 2, 3, 4, 5 }, waits);
    }

    [Fact]
    public void Reserve_QueuedReservations_SpreadAcrossSeconds()
    {
        var limiter = new SlidingWindowRateLimiter(perSecond: 2, perMinute: 100);

        var waits = Enumerable.Range(0, 5).Select(_ => limiter.Reserve(T0)).ToList();

        Assert.Equal(
            new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            waits);
    }
}

public sealed class PriorityRateLimiterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OrdinaryRequests_LeaveTheUrgentShareFree_ForTheStops()
    {
        var limiter = new PriorityRateLimiter(perSecond: 10, perMinute: 100, urgentShare: 0.2m); // ordinary: 8 a second

        var ordinary = Enumerable.Range(0, 9).Select(_ => limiter.Reserve(T0, urgent: false).TotalSeconds).ToList();
        var urgent = Enumerable.Range(0, 3).Select(_ => limiter.Reserve(T0, urgent: true).TotalSeconds).ToList();

        Assert.Equal(new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 1 }, ordinary); // the ninth waits for its own share
        Assert.Equal(new double[] { 0, 0, 1 }, urgent); // two go at once, then the whole budget of 10 is full
    }

    [Fact]
    public void AnOrdinaryRequestBookedAMinuteAhead_NeverLetsUrgentOnesOverTheWholeBudget()
    {
        var limiter = new PriorityRateLimiter(perSecond: 10, perMinute: 100, urgentShare: 0.2m); // ordinary: 8 a second, 80 a minute

        // 80 ordinary requests fill their minute; the 81st is booked a minute ahead, which once made the whole budget forget
        // the sends still in its window.
        var slots = Enumerable.Range(0, 81).Select(_ => T0 + limiter.Reserve(T0, urgent: false)).ToList();
        Assert.Equal(T0.AddMinutes(1), slots[^1]);
        slots.AddRange(Enumerable.Range(0, 30).Select(_ => T0 + limiter.Reserve(T0, urgent: true)));

        foreach (var slot in slots)
        {
            Assert.True(slots.Count(t => t > slot.AddSeconds(-1) && t <= slot) <= 10, $"Over 10 sends in the second up to {slot:O}.");
            Assert.True(slots.Count(t => t > slot.AddMinutes(-1) && t <= slot) <= 100, $"Over 100 sends in the minute up to {slot:O}.");
        }
    }

    [Fact]
    public void WithoutAnUrgentShare_EveryRequestUsesTheWholeBudget()
    {
        var limiter = new PriorityRateLimiter(perSecond: 10, perMinute: 100);

        Assert.All(Enumerable.Range(0, 10), _ => Assert.Equal(TimeSpan.Zero, limiter.Reserve(T0, urgent: false)));
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.Reserve(T0, urgent: true));
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("0.6")]
    public void AnUrgentShare_OutsideZeroToAHalf_IsRefused(string share) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriorityRateLimiter(10, 100, decimal.Parse(share, System.Globalization.CultureInfo.InvariantCulture)));
}

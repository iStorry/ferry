using System.Net;
using Ferry.Core.Http;

namespace Ferry.Core.Tests;

/// <summary>Reading the JSON answers of public DNS over HTTPS (Cloudflare's and Google's are the same shape).</summary>
public sealed class PublicDnsTests
{
    [Fact]
    public void AnAnswerGivesItsIPv4AddressesAndTheShortestTtl()
    {
        const string Json = """
            {"Status":0,"TC":false,"RD":true,"RA":true,"AD":false,"CD":false,
             "Question":[{"name":"slow.example","type":1}],
             "Answer":[{"name":"slow.example","type":5,"TTL":60,"data":"edge.example."},
                       {"name":"edge.example","type":1,"TTL":300,"data":"188.114.97.0"},
                       {"name":"edge.example","type":1,"TTL":120,"data":"188.114.96.0"}]}
            """;

        var answer = PublicDns.Answer(Json);

        Assert.NotNull(answer);
        Assert.Equal([IPAddress.Parse("188.114.97.0"), IPAddress.Parse("188.114.96.0")], answer.Value.Addresses);
        Assert.Equal(120, answer.Value.Ttl);
    }

    [Theory]
    [InlineData("""{"Status":3,"Question":[{"name":"nowhere.example","type":1}]}""")] // NXDOMAIN
    [InlineData("""{"Status":0,"Answer":[{"name":"x.example","type":5,"TTL":60,"data":"y.example."}]}""")]
    [InlineData("""{"Status":0}""")]
    [InlineData("<html>not json</html>")]
    [InlineData("[]")]
    public void NoAddressOrNoAnswerIsNull(string json) => Assert.Null(PublicDns.Answer(json));

    [Fact]
    public void OnlyTheNamedHostsAreLookedUpThisWay()
    {
        using var dns = new PublicDns(["slow.example"]);
        Assert.True(dns.Handles("SLOW.example"));
        Assert.False(dns.Handles("example.com"));
    }
}

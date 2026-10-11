using Brezza.Core.Bridge;

namespace Brezza.Core.Tests.Bridge;

public sealed class TrustedChromeOriginPolicyTests
{
    [Theory]
    [InlineData("https://brezza.local/index.html", true)]
    [InlineData("https://BREZZA.LOCAL/app.js", true)]
    [InlineData("http://brezza.local/index.html", false)]
    [InlineData("https://brezza.local:8443/index.html", false)]
    [InlineData("https://brezza.local.example/index.html", false)]
    [InlineData("https://user@brezza.local/index.html", false)]
    [InlineData("https://example.com/", false)]
    public void Allows_only_the_trusted_virtual_host(string source, bool expected)
    {
        Assert.Equal(expected, TrustedChromeOriginPolicy.IsTrusted(source));
    }
}

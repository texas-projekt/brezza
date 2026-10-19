using Brezza.Core.Navigation;

namespace Brezza.Core.Tests.Navigation;

public sealed class ExternalSchemePolicyTests
{
    [Theory]
    [InlineData("mailto:user@example.com")]
    [InlineData("tel:+15551234567")]
    [InlineData("sms:+15551234567")]
    public void A_user_gesture_may_launch_a_known_external_application(string uri)
    {
        Assert.True(ExternalSchemePolicy.IsAllowed(uri, userInitiated: true));
    }

    [Fact]
    public void A_script_initiated_external_launch_is_blocked()
    {
        Assert.False(ExternalSchemePolicy.IsAllowed("mailto:user@example.com", userInitiated: false));
    }

    [Theory]
    [InlineData("calculator://open")]
    [InlineData("ms-settings:privacy")]
    [InlineData("not a uri")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_or_malformed_scheme_is_blocked(string? uri)
    {
        Assert.False(ExternalSchemePolicy.IsAllowed(uri, userInitiated: true));
    }
}

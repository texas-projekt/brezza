using Brezza.Core.Permissions;

namespace Brezza.Core.Tests.Permissions;

public sealed class BrowserPermissionPolicyTests
{
    [Theory]
    [InlineData(BrowserPermissionKind.MediaCapture)]
    [InlineData(BrowserPermissionKind.Geolocation)]
    [InlineData(BrowserPermissionKind.Notifications)]
    [InlineData(BrowserPermissionKind.Clipboard)]
    [InlineData(BrowserPermissionKind.FileSystem)]
    [InlineData(BrowserPermissionKind.Other)]
    [InlineData(BrowserPermissionKind.Unknown)]
    public void No_capability_is_granted_without_a_user_prompt(BrowserPermissionKind kind)
    {
        var decision = BrowserPermissionPolicy.Evaluate(kind);

        Assert.False(decision.IsAllowed);
        Assert.Equal(kind, decision.Kind);
        Assert.NotEmpty(decision.Reason);
    }

    [Fact]
    public void A_granted_decision_reports_the_kind_it_applies_to()
    {
        var decision = BrowserPermissionDecision.Grant(BrowserPermissionKind.MediaCapture);

        Assert.True(decision.IsAllowed);
        Assert.Equal(BrowserPermissionKind.MediaCapture, decision.Kind);
    }
}

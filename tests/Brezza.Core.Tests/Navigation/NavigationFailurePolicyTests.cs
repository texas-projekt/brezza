using Brezza.Core.Navigation;

namespace Brezza.Core.Tests.Navigation;

public sealed class NavigationFailurePolicyTests
{
    [Theory]
    [InlineData("HostNameNotResolved", NavigationFailureKind.NameNotResolved)]
    [InlineData("CannotConnect", NavigationFailureKind.Connection)]
    [InlineData("ConnectionAborted", NavigationFailureKind.Connection)]
    [InlineData("ConnectionReset", NavigationFailureKind.Connection)]
    [InlineData("Disconnected", NavigationFailureKind.Connection)]
    [InlineData("ServerUnreachable", NavigationFailureKind.Connection)]
    [InlineData("Timeout", NavigationFailureKind.Timeout)]
    [InlineData("CertificateExpired", NavigationFailureKind.Certificate)]
    [InlineData("CertificateCommonNameIsIncorrect", NavigationFailureKind.Certificate)]
    [InlineData("CertificateRevoked", NavigationFailureKind.Certificate)]
    [InlineData("CertificateIsInvalid", NavigationFailureKind.Certificate)]
    [InlineData("ClientCertificateContainsErrors", NavigationFailureKind.Certificate)]
    [InlineData("ErrorHttpInvalidServerResponse", NavigationFailureKind.Server)]
    [InlineData("RedirectFailed", NavigationFailureKind.Server)]
    [InlineData("ValidAuthenticationCredentialsRequired", NavigationFailureKind.Authentication)]
    [InlineData("ValidProxyAuthenticationRequired", NavigationFailureKind.Authentication)]
    [InlineData("Unknown", NavigationFailureKind.Other)]
    [InlineData("UnexpectedError", NavigationFailureKind.Other)]
    [InlineData("", NavigationFailureKind.Other)]
    [InlineData(null, NavigationFailureKind.Other)]
    public void Runtime_statuses_map_to_a_reason_a_user_can_act_on(string? status, NavigationFailureKind expected)
    {
        Assert.Equal(expected, NavigationFailurePolicy.Classify(status));
    }

    [Theory]
    [InlineData(NavigationFailureKind.NameNotResolved, true)]
    [InlineData(NavigationFailureKind.Connection, true)]
    [InlineData(NavigationFailureKind.Timeout, true)]
    [InlineData(NavigationFailureKind.Server, true)]
    [InlineData(NavigationFailureKind.Other, true)]
    [InlineData(NavigationFailureKind.Certificate, false)]
    [InlineData(NavigationFailureKind.Authentication, false)]
    public void Only_failures_that_can_change_on_their_own_offer_a_retry(NavigationFailureKind kind, bool retryable)
    {
        Assert.Equal(retryable, NavigationFailurePolicy.IsRetryable(kind));
    }
}

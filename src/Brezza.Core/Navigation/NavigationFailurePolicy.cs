namespace Brezza.Core.Navigation;

/// <summary>Why a page could not be loaded, in terms a user can act on.</summary>
public enum NavigationFailureKind
{
    /// <summary>The site's name could not be found: a typo, or no network.</summary>
    NameNotResolved,

    /// <summary>The connection could not be made or was cut: the network or the site is down.</summary>
    Connection,

    /// <summary>The site took too long to answer.</summary>
    Timeout,

    /// <summary>The site's certificate is not valid, so the browser did not trust the connection.</summary>
    Certificate,

    /// <summary>The site answered, but not with something the browser could use.</summary>
    Server,

    /// <summary>The site or a proxy asked for credentials the browser cannot supply.</summary>
    Authentication,

    /// <summary>Anything else the runtime reports.</summary>
    Other,
}

/// <summary>
/// Classifies the runtime's navigation error status. The runtime names about twenty statuses; a
/// user needs to know whether to check the address, the network, or the site, and whether trying
/// again can help.
/// </summary>
public static class NavigationFailurePolicy
{
    public static NavigationFailureKind Classify(string? webErrorStatus) => webErrorStatus switch
    {
        "HostNameNotResolved" => NavigationFailureKind.NameNotResolved,
        "CannotConnect" or "ConnectionAborted" or "ConnectionReset" or "Disconnected" or "ServerUnreachable"
            => NavigationFailureKind.Connection,
        "Timeout" => NavigationFailureKind.Timeout,
        "CertificateCommonNameIsIncorrect" or "CertificateExpired" or "ClientCertificateContainsErrors"
            or "CertificateRevoked" or "CertificateIsInvalid" => NavigationFailureKind.Certificate,
        "ErrorHttpInvalidServerResponse" or "RedirectFailed" => NavigationFailureKind.Server,
        "ValidAuthenticationCredentialsRequired" or "ValidProxyAuthenticationRequired"
            => NavigationFailureKind.Authentication,
        _ => NavigationFailureKind.Other,
    };

    /// <summary>
    /// Whether trying the same address again can succeed without the user changing anything. A
    /// certificate problem or a missing sign-in will fail the same way again.
    /// </summary>
    public static bool IsRetryable(NavigationFailureKind kind) =>
        kind is not (NavigationFailureKind.Certificate or NavigationFailureKind.Authentication);
}

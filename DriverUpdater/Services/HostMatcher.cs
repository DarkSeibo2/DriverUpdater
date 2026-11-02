namespace DriverUpdater.Services;

/// <summary>
/// Host allowlist matching.
///
/// A plain <c>host.EndsWith("intel.com")</c> also accepts "evilintel.com",
/// "notnvidia.com" and similar look-alike domains. A host is only trusted when
/// it is the domain itself or a real subdomain of it.
/// </summary>
public static class HostMatcher
{
    public static bool IsDomainOrSubdomain(string? host, string? domain)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(domain))
            return false;

        host = host.Trim().TrimEnd('.');
        domain = domain.Trim().TrimStart('.').TrimEnd('.');
        if (host.Length == 0 || domain.Length == 0)
            return false;

        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowed(string? host, IEnumerable<string>? domains)
        => domains is not null && domains.Any(d => IsDomainOrSubdomain(host, d));
}

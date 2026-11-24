namespace AOS.Messenger.Web.Middleware;

/// <summary>
/// Web-owned security header configuration deriving optional pure-S3 Cloudflare R2 CSP origin
/// strictly from <c>R2:ServiceUrl</c> without referencing Infrastructure types.
/// </summary>
public sealed class SecurityHeadersOptions
{
    public static SecurityHeadersOptions Default { get; } = new(null);

    public string? R2Origin { get; }
    public string ContentSecurityPolicy { get; }

    private SecurityHeadersOptions(string? r2Origin)
    {
        R2Origin = r2Origin;
        ContentSecurityPolicy = string.IsNullOrEmpty(r2Origin)
            ? "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self' blob:; media-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'"
            : $"default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self' blob: {r2Origin}; media-src 'self' blob: {r2Origin}; connect-src 'self' {r2Origin}; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    }

    public static SecurityHeadersOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return FromServiceUrl(configuration["R2:ServiceUrl"]);
    }

    public static SecurityHeadersOptions FromServiceUrl(string? serviceUrl)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            return Default;
        }

        var trimmed = serviceUrl.Trim();
        if (trimmed.Contains('*', StringComparison.Ordinal)
            || trimmed.Any(ch => char.IsWhiteSpace(ch) || ch is ';' or '\'' or '"' or '\r' or '\n'))
        {
            throw new InvalidOperationException("R2:ServiceUrl contains forbidden characters for CSP origin derivation.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath != "/" && !string.IsNullOrEmpty(uri.AbsolutePath)))
        {
            throw new InvalidOperationException(
                "R2:ServiceUrl must be an exact https:// origin with no userinfo, path, query, or fragment.");
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        return new SecurityHeadersOptions(origin);
    }
}

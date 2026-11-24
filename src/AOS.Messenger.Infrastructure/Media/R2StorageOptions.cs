using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AOS.Messenger.Infrastructure.Storage;

/// <summary>
/// Configuration options for Cloudflare R2 pure S3 API media storage.
/// Leave all properties unset in Development to activate the local-disk fallback simulator.
/// </summary>
public sealed class R2StorageOptions
{
    public const string SectionName = "R2";

    public string? ServiceUrl { get; set; }
    public string? BucketName { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }

    public bool HasAnySetting =>
        !string.IsNullOrWhiteSpace(ServiceUrl) ||
        !string.IsNullOrWhiteSpace(BucketName) ||
        !string.IsNullOrWhiteSpace(AccessKeyId) ||
        !string.IsNullOrWhiteSpace(SecretAccessKey);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServiceUrl) &&
        !string.IsNullOrWhiteSpace(BucketName) &&
        !string.IsNullOrWhiteSpace(AccessKeyId) &&
        !string.IsNullOrWhiteSpace(SecretAccessKey);
}

/// <summary>
/// Validates R2 configuration on startup: fails fast in non-Development when unconfigured,
/// and validates strict HTTPS pure-S3 origin constraints whenever any R2 setting is provided.
/// </summary>
public sealed class R2StorageOptionsValidator : IValidateOptions<R2StorageOptions>
{
    private readonly IHostEnvironment? _environment;

    public R2StorageOptionsValidator(IHostEnvironment? environment = null)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, R2StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var isDevelopment = _environment?.IsDevelopment() ?? true;
        if (!options.HasAnySetting)
        {
            return isDevelopment
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    "R2 storage configuration ('R2:ServiceUrl', 'R2:BucketName', 'R2:AccessKeyId', 'R2:SecretAccessKey') is required outside Development.");
        }

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            errors.Add("R2:ServiceUrl is required when R2 configuration is present.");
        }
        else if (!TryValidateServiceUrl(options.ServiceUrl, out var urlError))
        {
            errors.Add(urlError);
        }

        if (string.IsNullOrWhiteSpace(options.BucketName))
        {
            errors.Add("R2:BucketName is required when R2 configuration is present.");
        }
        else
        {
            var bucket = options.BucketName.Trim();
            if (bucket.Length < 3 || bucket.Length > 63 || bucket.Any(ch => char.IsWhiteSpace(ch) || ch == '/' || ch == '\\'))
            {
                errors.Add("R2:BucketName must be between 3 and 63 characters without whitespace or slashes.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.AccessKeyId))
        {
            errors.Add("R2:AccessKeyId is required when R2 configuration is present.");
        }

        if (string.IsNullOrWhiteSpace(options.SecretAccessKey))
        {
            errors.Add("R2:SecretAccessKey is required when R2 configuration is present.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    internal static bool TryValidateServiceUrl(string rawServiceUrl, out string error)
    {
        var trimmed = rawServiceUrl.Trim();
        if (trimmed.Contains('*', StringComparison.Ordinal))
        {
            error = "R2:ServiceUrl must not contain wildcards.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.AbsolutePath != "/" && !string.IsNullOrEmpty(uri.AbsolutePath)))
        {
            error = "R2:ServiceUrl must be an exact https:// origin with no userinfo, path, query, or fragment.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

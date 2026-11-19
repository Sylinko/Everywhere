using System.Text.RegularExpressions;

namespace Everywhere.Modding.Packaging;

/// <summary>
/// Shares identifier, localization, URL, and license validation across manifests and indexes.
/// </summary>
public static partial class ModValidationRules
{
    /// <summary>
    /// Copies a language map while reporting case-colliding tags as invalid package data.
    /// </summary>
    public static Dictionary<string, string> NormalizeTranslations(Dictionary<string, string> translations)
    {
        var normalized = new Dictionary<string, string>(translations.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (language, value) in translations)
        {
            if (!normalized.TryAdd(language, value))
            {
                throw new ModValidationException("DuplicateTranslation", $"Duplicate localization for language '{language}'.");
            }
        }

        return normalized;
    }

    /// <summary>
    /// Validates every declared language tag and rejects blank localized text.
    /// </summary>
    public static void ValidateTranslations(IReadOnlyDictionary<string, string> translations)
    {
        foreach (var (language, value) in translations)
        {
            if (!LanguageTagPattern().IsMatch(language) || string.IsNullOrWhiteSpace(value))
            {
                throw new ModValidationException("InvalidLocalization", $"Invalid language tag or empty value for '{language}'.");
            }
        }
    }

    /// <summary>
    /// Validates a logical mod or repository identifier.
    /// </summary>
    public static bool IsValidIdentifier(string identifier) => IdentifierPattern().IsMatch(identifier);

    /// <summary>
    /// Validates an optional author string without allowing blank values.
    /// </summary>
    public static void ValidateAuthor(string? author)
    {
        if (author is not null && string.IsNullOrWhiteSpace(author))
        {
            throw new ModValidationException("InvalidAuthor", "Declared author cannot be empty.");
        }
    }

    /// <summary>
    /// Validates an icon extension for package-relative image assets.
    /// </summary>
    public static bool IsSupportedIconExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates an absolute HTTP(S) URL for display or mirror metadata without embedded credentials.
    /// </summary>
    public static Uri ValidateHttpUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ModValidationException("InvalidUrl", $"Invalid URL '{value}'.");
        }

        var isHttpOrHttps =
            string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        if (!isHttpOrHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ModValidationException("InvalidUrl", $"Unsupported scheme or credentials in URL '{value}'.");
        }

        return uri;
    }

    /// <summary>
    /// Validates repository web page URL and optional sub-directory declaration.
    /// </summary>
    public static void ValidateRepository(ModRepositoryInfo? repository)
    {
        if (repository is null) return;

        ValidateHttpUri(repository.Url);
        if (repository.Directory is not null)
        {
            PackagePath.ValidateRelative(repository.Directory);
        }
    }

    /// <summary>
    /// Validates an SPDX expression or custom license reference identifier.
    /// </summary>
    public static void ValidateSpdx(string spdx)
    {
        if (string.IsNullOrWhiteSpace(spdx))
        {
            throw new ModValidationException("InvalidLicense", $"Invalid SPDX license expression '{spdx}'.");
        }
    }

    /// <summary>
    /// Validates package license metadata, relative notice paths, and uniqueness.
    /// </summary>
    public static void ValidateLicense(ModLicense? license)
    {
        if (license is null) return;

        ValidateSpdx(license.Spdx);
        if (license.File is not null)
        {
            PackagePath.ValidateRelative(license.File);
        }

        var notices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (license.File is not null)
        {
            notices.Add(license.File);
        }

        foreach (var notice in license.ThirdPartyFiles)
        {
            PackagePath.ValidateRelative(notice);
            if (!notices.Add(notice))
            {
                throw new ModValidationException("InvalidLicense", $"Duplicate license or third-party notice path '{notice}'.");
            }
        }
    }

    /// <summary>
    /// Checks the portable language-tag spelling without restricting authors to the host's current languages.
    /// </summary>
    [GeneratedRegex("^[A-Za-z]{1,8}(?:-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTagPattern();

    /// <summary>
    /// Restricts mod and repository IDs to portable directory-safe logical identifiers.
    /// </summary>
    [GeneratedRegex("^[a-z0-9]+(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}

namespace Everywhere.Common;

public static class LinkConstants
{
    // Official website
    private const string OfficialWebsiteUrl = "https://everywhere.sylinko.com";
    public static Uri OfficialWebsiteUri => new(OfficialWebsiteUrl, UriKind.Absolute);
    public static Uri OfficialWebsitePricingUri => new($"{OfficialWebsiteUrl}/pricing", UriKind.Absolute);
    public static Uri OfficialWebsiteDocsModelProviderUri => new($"{OfficialWebsiteUrl}/docs/model-provider", UriKind.Absolute);

    // GitHub & sponsorship
    private const string GitHubRepoUrl = "https://github.com/Sylinko/Everywhere";
    public static Uri GitHubRepoUri => new(GitHubRepoUrl, UriKind.Absolute);
    public static Uri GitHubOpenSourceLicensesUri => new($"{GitHubRepoUrl}/blob/main/ThirdPartyNotices.txt", UriKind.Absolute);
    public static Uri GitHubSponsorsUri => new("https://github.com/sponsors/Sylinko", UriKind.Absolute);
    public static Uri AfdianUri => new("https://afdian.com/a/DearVa", UriKind.Absolute);

    // aka
    public static Uri AkaDiscordUri => new("https://aka.sylinko.com/everywhere-discord", UriKind.Absolute);
    public static Uri AkaQQGroupUri => new("https://aka.sylinko.com/everywhere-qq-group", UriKind.Absolute);

    // Runtime dependencies
    public static Uri DockerInstallGuideUri => new("https://docs.docker.com/get-started/get-docker/", UriKind.Absolute);
}

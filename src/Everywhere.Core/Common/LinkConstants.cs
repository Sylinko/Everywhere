namespace Everywhere.Common;

public static class LinkConstants
{
    // Official website
    private const string OfficialWebsiteUrl = "https://everywhere.sylinko.com";
    public static Uri OfficialWebsiteUri => new(OfficialWebsiteUrl, UriKind.Absolute);
    public static Uri OfficialWebsitePricingUri => new($"{OfficialWebsiteUrl}/pricing", UriKind.Absolute);
    public static Uri OfficialWebsiteDocsModelProviderUri => new($"{OfficialWebsiteUrl}/docs/model-provider", UriKind.Absolute);

    // aka
    public static Uri AkaDiscordUri => new("https://aka.sylinko.com/everywhere-discord", UriKind.Absolute);
    public static Uri AkaQQGroupUri => new("https://aka.sylinko.com/everywhere-qq-group", UriKind.Absolute);

    // Runtime dependencies
    public static Uri DockerInstallGuideUri => new("https://docs.docker.com/get-started/get-docker/", UriKind.Absolute);
}

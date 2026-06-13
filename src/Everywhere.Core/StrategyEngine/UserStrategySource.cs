using Everywhere.Configuration;

namespace Everywhere.StrategyEngine;

public interface IUserStrategySource
{
    string RootDirectoryPath { get; }

    IEnumerable<string> EnumerateStrategyFiles();
}

public sealed class UserStrategySource : IUserStrategySource
{
    public string RootDirectoryPath { get; } = RuntimeConstants.EnsureConfigurationFolderPath("strategies");

    public IEnumerable<string> EnumerateStrategyFiles()
    {
        if (!Directory.Exists(RootDirectoryPath))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(RootDirectoryPath, "STRATEGY.md", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.GetFullPath(file);
        }
    }
}

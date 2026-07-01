using Everywhere.Common;

namespace Everywhere.Core.Tests.Common;

public class RuntimeManagerTests
{
    [TestCase("uvx", RuntimeKind.Uv)]
    [TestCase("uv", RuntimeKind.Uv)]
    [TestCase("npx", RuntimeKind.NodeJs)]
    [TestCase("npm.cmd", RuntimeKind.NodeJs)]
    [TestCase("node.exe", RuntimeKind.NodeJs)]
    [TestCase("bunx", RuntimeKind.Bun)]
    [TestCase("bun", RuntimeKind.Bun)]
    [TestCase("docker", RuntimeKind.Docker)]
    public void Detect_ReturnsKnownRuntimeDependency(string command, RuntimeKind kind)
    {
        var dependency = RuntimeDependencyDetector.Detect(command);

        Assert.Multiple(() =>
        {
            Assert.That(dependency, Is.Not.Null);
            Assert.That(dependency!.Kind, Is.EqualTo(kind));
        });
    }

    [Test]
    public void Detect_IgnoresUnknownCommand()
    {
        Assert.That(RuntimeDependencyDetector.Detect("python"), Is.Null);
    }

    [Test]
    public void Detect_IgnoresPathLikeCommand()
    {
        Assert.That(RuntimeDependencyDetector.Detect(@"C:\Tools\uvx.exe"), Is.Null);
    }

    [Test]
    public void SelectLatestLtsNodeVersion_ReturnsFirstLtsRelease()
    {
        const string json =
            """
            [
              { "version": "v26.0.0", "lts": false },
              { "version": "v24.11.1", "lts": "Krypton" },
              { "version": "v22.21.1", "lts": "Jod" }
            ]
            """;

        Assert.That(RuntimeManager.SelectLatestLtsNodeVersion(json), Is.EqualTo("v24.11.1"));
    }
}

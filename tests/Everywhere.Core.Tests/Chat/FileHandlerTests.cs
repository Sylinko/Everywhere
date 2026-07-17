using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Skills;
using Microsoft.Extensions.Logging;
using NSubstitute;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Everywhere.Core.Tests.Chat;

public class FileHandlerTests
{
    [Test]
    public async Task Factory_UsesRegistrationOrder()
    {
        var first = new TestHandler(true);
        var second = new TestHandler(true);
        var factory = new FileHandlerContextFactory([first, second]);

        var context = await factory.CreateAsync("anything", Path.GetTempPath());

        Assert.Multiple(() =>
        {
            Assert.That(context.Handler, Is.SameAs(first));
            Assert.That(first.MatchCount, Is.EqualTo(1));
            Assert.That(second.MatchCount, Is.Zero);
        });
    }

    [Test]
    public void Factory_RejectsUnknownUri()
    {
        var factory = CreateLocalFactory();

        Assert.ThrowsAsync<HandledException>(async () =>
            await factory.CreateAsync("unknown://resource/file.txt", Path.GetTempPath()));
    }

    [Test]
    public async Task Factory_NewFileIsText_EvenWithPdfExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");

        var context = await CreateLocalFactory().CreateAsync(path, Path.GetTempPath());

        Assert.Multiple(() =>
        {
            Assert.That(context.Handler, Is.TypeOf<TextFileHandler>());
            Assert.That(File.Exists(path), Is.False);
        });
    }

    [Test]
    public async Task Factory_PdfMagicWinsOverDisguisedExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin");
        await File.WriteAllBytesAsync(path, "%PDF-not-a-complete-pdf"u8.ToArray());
        try
        {
            var context = await CreateLocalFactory().CreateAsync(path, Path.GetTempPath());

            Assert.That(context.Handler, Is.TypeOf<PdfFileHandler>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Factory_BinaryAndDirectoryUseFallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "data.bin");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(file, Enumerable.Range(0, 256).Select(value => (byte)value).ToArray());
        try
        {
            var factory = CreateLocalFactory();
            var binary = await factory.CreateAsync(file, directory);
            var folder = await factory.CreateAsync(directory, directory);

            Assert.Multiple(() =>
            {
                Assert.That(binary.Handler, Is.TypeOf<BinaryFileHandler>());
                Assert.That(folder.Handler, Is.TypeOf<BinaryFileHandler>());
            });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task TextHandler_WritesAndAppends_WhileBinaryRejectsWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var textPath = Path.Combine(directory, "notes.txt");
        var binaryPath = Path.Combine(directory, "data.bin");
        await File.WriteAllBytesAsync(binaryPath, Enumerable.Range(0, 256).Select(value => (byte)value).ToArray());
        try
        {
            var factory = CreateLocalFactory();
            var text = await factory.CreateAsync(textPath, directory);
            await text.Handler.WriteAsync(text, "first", false, CancellationToken.None);
            await text.Handler.WriteAsync(text, " second", true, CancellationToken.None);
            var binary = await factory.CreateAsync(binaryPath, directory);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(textPath), Is.EqualTo("first second"));
                Assert.ThrowsAsync<HandledException>(async () =>
                    await binary.Handler.WriteAsync(binary, "no", false, CancellationToken.None));
            });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task PdfHandler_ReadsLogicalLinesAcrossPages_WithPageMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var firstPage = builder.AddPage(PageSize.A4);
        firstPage.AddText("page one first", 12, new PdfPoint(50, 750), font);
        firstPage.AddText("page one second", 12, new PdfPoint(50, 720), font);
        var secondPage = builder.AddPage(PageSize.A4);
        secondPage.AddText("page two first", 12, new PdfPoint(50, 750), font);
        secondPage.AddText("page two second", 12, new PdfPoint(50, 720), font);
        await File.WriteAllBytesAsync(path, builder.Build());
        try
        {
            var context = await CreateLocalFactory().CreateAsync(path, Path.GetTempPath());
            var result = await context.Handler.ReadAsync(context, 2, 2, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(context.Handler, Is.TypeOf<PdfFileHandler>());
                Assert.That(result.Items, Has.Count.EqualTo(2));
                Assert.That(result.Items[0].PageNumber, Is.EqualTo(1));
                Assert.That(result.Items[1].PageNumber, Is.EqualTo(2));
                Assert.That(result.Metadata["totalPages"], Is.EqualTo(2));
                Assert.That(result.HasMore, Is.True);
                Assert.That(result.NextOffset, Is.EqualTo(4));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task PdfHandler_DiagnosesEmptyAndDamagedDocuments()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var emptyPath = Path.Combine(directory, "empty.pdf");
        var damagedPath = Path.Combine(directory, "damaged.pdf");
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        await File.WriteAllBytesAsync(emptyPath, builder.Build());
        await File.WriteAllBytesAsync(damagedPath, "%PDF-damaged"u8.ToArray());
        try
        {
            var factory = CreateLocalFactory();
            var empty = await factory.CreateAsync(emptyPath, directory);
            var damaged = await factory.CreateAsync(damagedPath, directory);

            Assert.Multiple(() =>
            {
                Assert.That(
                    Assert.ThrowsAsync<HandledException>(async () =>
                        await empty.Handler.ReadAsync(empty, 1, 10, CancellationToken.None))!.Message,
                    Does.Contain("no extractable text"));
                Assert.That(
                    Assert.ThrowsAsync<HandledException>(async () =>
                        await damaged.Handler.ReadAsync(damaged, 1, 10, CancellationToken.None))!.Message,
                    Does.Contain("damaged"));
            });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task TextHandler_ReplacementDoesNotLockDuringReview_AndDetectsConflict()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(path, "original value");
        try
        {
            var context = await CreateLocalFactory().CreateAsync(path, Path.GetTempPath());
            var exception = Assert.ThrowsAsync<HandledException>(async () =>
                await context.Handler.ReplaceContentAsync(
                    context,
                    ["original"],
                    ["proposed"],
                    false,
                    false,
                    async (_, proposed, cancellationToken) =>
                    {
                        await using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                        }

                        await File.WriteAllTextAsync(path, "external change", cancellationToken);
                        return new FileReviewResult(proposed, "accepted");
                    },
                    CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.Contain("changed while"));
                Assert.That(File.ReadAllText(path), Is.EqualTo("external change"));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task SkillHandler_ReadsPhysicalAndVirtualResources_AndRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var physicalDirectory = Path.Combine(root, "sample");
        Directory.CreateDirectory(physicalDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(physicalDirectory, "SKILL.md"),
            "---\nname: Sample\ndescription: Physical sample\n---\n# Sample");
        await File.WriteAllTextAsync(Path.Combine(physicalDirectory, "reference.txt"), "physical resource");

        using var source = new SkillSource(
            Substitute.For<ILogger<SkillSource>>(),
            () => [new SkillRoot(SkillSourceRoot.Agents, "Agents", root)]);
        using var manager = new SkillManager(
            new PersistentState(new InMemoryKeyValueStorage()),
            source,
            [new StaticVirtualSkillProvider(
                new VirtualSkill(
                    "builtin.demo",
                    "---\nname: Demo\ndescription: Virtual sample\n---\n# Demo",
                    new Dictionary<string, string>
                    {
                        ["reference.txt"] = "virtual resource",
                        ["references/tips.md"] = "nested resource"
                    })
            )],
            Substitute.For<ILogger<SkillManager>>());
        await manager.RefreshAsync();
        var factory = new FileHandlerContextFactory([new SkillFileHandler(manager)]);
        try
        {
            var physical = await factory.CreateAsync("skill://agents.sample/reference.txt", root);
            var physicalRead = await physical.Handler.ReadAsync(physical, 1, 10, CancellationToken.None);
            var virtualContext = await factory.CreateAsync("skill://builtin.demo/reference.txt", root);
            var virtualRead = await virtualContext.Handler.ReadAsync(virtualContext, 1, 10, CancellationToken.None);
            var virtualRoot = await factory.CreateAsync("skill://builtin.demo/", root);
            var topLevelResources = new List<string>();
            await foreach (var resource in virtualRoot.Handler.EnumerateAsync(
                               virtualRoot,
                               new Regex(".*"),
                               false,
                               false,
                               CancellationToken.None))
            {
                topLevelResources.Add(resource);
            }

            Assert.Multiple(() =>
            {
                Assert.That(physical.Path, Is.EqualTo("skill://agents.sample/reference.txt"));
                Assert.That(physicalRead.Items.Single().Content, Is.EqualTo("physical resource"));
                Assert.That(virtualRead.Items.Single().Content, Is.EqualTo("virtual resource"));
                Assert.That(manager.GetPrompt(), Does.Contain("skill://builtin.demo/SKILL.md"));
                Assert.That(topLevelResources, Does.Contain("skill://builtin.demo/references"));
                Assert.ThrowsAsync<HandledException>(async () =>
                    await virtualContext.Handler.WriteAsync(
                        virtualContext,
                        "not allowed",
                        false,
                        CancellationToken.None));
            });

            Assert.ThrowsAsync<HandledException>(async () =>
                await factory.CreateAsync("skill://agents.sample/%2E%2E/outside.txt", root));
            Assert.ThrowsAsync<HandledException>(async () =>
                await factory.CreateAsync("skill://agents.sample/references%2Ftips.md", root));
            Assert.ThrowsAsync<HandledException>(async () =>
                await factory.CreateAsync("skill://sample/reference.txt", root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static FileHandlerContextFactory CreateLocalFactory() =>
        new([new PdfFileHandler(), new TextFileHandler(), new BinaryFileHandler()]);

    private sealed class TestHandler(bool result) : FileHandler
    {
        public int MatchCount { get; private set; }

        internal override ValueTask<FileHandlerContext?> TryCreateContextAsync(
            string path,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            MatchCount++;
            return ValueTask.FromResult<FileHandlerContext?>(
                result ? new FileHandlerContext(this, path, workingDirectory) : null);
        }
    }

    private sealed class StaticVirtualSkillProvider(params VirtualSkill[] skills) : IVirtualSkillProvider
    {
        public async IAsyncEnumerable<VirtualSkill> ListAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var skill in skills)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return skill;
                await Task.CompletedTask;
            }
        }
    }
}

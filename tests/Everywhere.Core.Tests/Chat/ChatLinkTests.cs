using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Chat.Links;
using Everywhere.Chat.Plugins;
using Everywhere.Configuration;
using Everywhere.Core.I18N;
using Everywhere.I18N;
using Everywhere.Interop;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using Lucide.Avalonia;
using Markdig;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShadUI;
using System.Buffers;
using System.Collections.Concurrent;

namespace Everywhere.Core.Tests.Chat;

[TestFixture]
public sealed class ChatLinkTests
{
    [AvaloniaTest]
    public async Task ChatLinks_WhenClickedOrHovered_RevealFilesAndExpandWebPreviews()
    {
        const string queryAddress = "https://8.8.8.8/Article?token=Aa&mode=read#Section";
        using var handler = new PreviewHandler();
        var fileOperations = new RecordingFileOperations();
        using var previews = new WebPreviewService(handler, NullLogger<WebPreviewService>.Instance);
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new ChatWindowSettings(services);
        var interaction = new ChatLinkInteraction(fileOperations, previews, settings, NullLogger<ChatLinkInteraction>.Instance);
        var path = Path.GetTempFileName();
        var directory = Directory.CreateTempSubdirectory("chat-link-");
        var renderer = new ChatLinkRenderer
        {
            Margin = new Thickness(16),
            DocumentUpdate = new MarkdownDocumentUpdate.Full(
                Markdown.Parse(
                    $"""
                     [local file]({ChatLinkTarget.ToFileUri(path)})

                     [query link]({queryAddress})

                     [web article](https://8.8.8.8/article#section?2)

                     [directory]({ChatLinkTarget.ToFileUri(directory.FullName)})

                     [directory menu]({ChatLinkTarget.ToFileUri(directory.FullName)})
                     """))
        };
        var toolReference = new ChatPluginFileReference(directory.FullName);
        var toolDetails = new ChatPluginDisplayBlockPresenter
        {
            Content = new ChatPluginFileReferencesDisplayBlock(toolReference)
        };
        var detailResources = new ResourceInclude(new Uri("avares://Everywhere.Core/"))
        {
            Source = new Uri("avares://Everywhere.Core/Views/Chat/ChatPluginDisplayBlockPresenter.axaml")
        };
        if (!detailResources.TryGetResource(typeof(ChatPluginDisplayBlockPresenter), null, out var detailTheme) ||
            detailTheme is not ControlTheme theme)
        {
            throw new AssertionException("No tool detail theme.");
        }

        toolDetails.Theme = theme;
        var window = new Window
        {
            Width = 360,
            Height = 460,
            Content = new StackPanel { Children = { renderer, toolDetails } },
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark
        };
        window.SetValue(ChatLinkInteraction.InteractionProperty, interaction);
        window.Resources["FontSizeM"] = 14d;
        window.Resources["FontSizeL"] = 16d;
        window.Styles.Add(new StyleInclude(new Uri("avares://Everywhere.Core/"))
        {
            Source = new Uri("avares://LiveMarkdown.Avalonia/Styles.axaml")
        });
        window.Styles.Add(new StyleInclude(new Uri("avares://Everywhere.Core/"))
        {
            Source = new Uri("avares://Everywhere.Core/Assets/Styles.axaml")
        });

        try
        {
            window.Show();
            window.UpdateLayout();
            var blocks = renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().ToArray();
            var localPoint = GetLinkPoint(blocks[0], window);
            window.MouseMove(localPoint);
            await EventuallyAsync(() => window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == path));
            window.MouseDown(localPoint, MouseButton.Left);
            window.MouseUp(localPoint, MouseButton.Left);
            Assert.That(await fileOperations.Revealed.Task.WaitAsync(TimeSpan.FromSeconds(3)), Is.EqualTo(path));

            window.MouseDown(localPoint, MouseButton.Right);
            window.MouseUp(localPoint, MouseButton.Right);
            var menu = renderer.LinkContextMenu ?? throw new AssertionException("No link menu.");
            Assert.That(menu.IsOpen, Is.True);
            Assert.That(menu.Items.OfType<MenuItem>().Select(item => item.Header), Does.Contain(LocaleKey.ChatLink_Reveal.I18N()));
            menu.Close();

            var queryPoint = GetLinkPoint(blocks[1], window);
            window.MouseMove(queryPoint);
            await EventuallyAsync(() => window.GetVisualDescendants().OfType<ChatWebLinkCard>().Any());
            var queryCard = window.GetVisualDescendants().OfType<ChatWebLinkCard>().Single();
            Assert.That(queryCard.FindControl<TextBlock>("AddressText")?.Text, Is.EqualTo("8.8.8.8"));
            Assert.That(queryCard.FindControl<Border>("Details")?.IsVisible, Is.False);
            Assert.That(queryCard.GetVisualDescendants().OfType<Button>(), Is.Empty);
            Assert.That(handler.Requests, Has.All.EqualTo(new Uri("https://8.8.8.8/favicon.ico")));

            window.MouseDown(queryPoint, MouseButton.Right);
            window.MouseUp(queryPoint, MouseButton.Right);
            Assert.That(menu.IsOpen, Is.True);
            var queryLink = blocks[1].Inlines?.OfType<Link>().Single() ?? throw new AssertionException("No query link.");
            var copyLink = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocaleKey.Common_CopyLink.I18N()));
            copyLink.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var clipboard = window.Clipboard ?? throw new AssertionException("No test clipboard.");
            Assert.That(await clipboard.TryGetTextAsync(), Is.EqualTo(queryAddress));
            Assert.That(queryLink.IsClicked, Is.False);
            menu.Close();

            var webPoint = GetLinkPoint(blocks[2], window);
            window.MouseMove(webPoint);
            ChatWebLinkCard? card = null;
            await EventuallyAsync(() => (card = window.GetVisualDescendants().OfType<ChatWebLinkCard>().SingleOrDefault()) is not null);
            var previewCard = card ?? throw new AssertionException("No preview card.");
            Assert.That(previewCard.GetVisualDescendants().OfType<Button>(), Is.Empty);
            window.UpdateLayout();
            var domainWidth = previewCard.Bounds.Width;
            handler.PageReady.TrySetResult();
            await EventuallyAsync(() => previewCard.FindControl<TextBlock>("TitleText") is { IsEffectivelyVisible: true, Text: "Preview article" });
            await EventuallyAsync(() =>
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                window.UpdateLayout();
                return previewCard.Bounds.Width > domainWidth;
            });
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await EventuallyAsync(() => !window.GetVisualDescendants().OfType<ChatWebLinkCard>().Any());

            settings.IsAutomaticLinkPreviewEnabled = false;
            var requestCount = handler.RequestCount;
            window.MouseMove(new Point(340, 220));
            window.MouseMove(webPoint);
            await EventuallyAsync(() => window.GetVisualDescendants().OfType<ChatWebLinkCard>().Any());
            var disabledCard = window.GetVisualDescendants().OfType<ChatWebLinkCard>().Single();
            Assert.That(disabledCard.FindControl<Border>("Details")?.IsVisible, Is.False);
            Assert.That(handler.RequestCount, Is.EqualTo(requestCount));

            var directoryPoint = GetLinkPoint(blocks[3], window);
            await EventuallyAsync(() => blocks[3].GetVisualDescendants().OfType<LucideIcon>().Any(icon => icon.Kind == LucideIconKind.Folder));
            await EventuallyAsync(() => toolDetails.GetVisualDescendants().OfType<LucideIcon>().Any(icon => icon.Kind == LucideIconKind.Folder));
            window.MouseMove(directoryPoint);
            window.MouseDown(directoryPoint, MouseButton.Left);
            window.MouseUp(directoryPoint, MouseButton.Left);
            Assert.That(await fileOperations.EnteredDirectory.Task.WaitAsync(TimeSpan.FromSeconds(3)), Is.EqualTo(directory.FullName));

            var directoryMenuPoint = GetLinkPoint(blocks[4], window);
            var directoryMenuLink = blocks[4].Inlines?.OfType<Link>().Single() ?? throw new AssertionException("No directory link.");
            window.MouseMove(directoryMenuPoint);
            window.MouseDown(directoryMenuPoint, MouseButton.Right);
            window.MouseUp(directoryMenuPoint, MouseButton.Right);
            var reveal = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocaleKey.ChatLink_Reveal.I18N()));
            reveal.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.That(
                await fileOperations.RevealedDirectory.Task.WaitAsync(TimeSpan.FromSeconds(3)),
                Is.EqualTo(directory.FullName));
            Assert.That(directoryMenuLink.IsClicked, Is.True);
        }
        finally
        {
            window.Close();
            File.Delete(path);
            directory.Delete();
        }
    }

    [AvaloniaTest]
    public async Task Reveal_WhenFileDisappears_ShowsErrorOnlyInInitiatingWindow()
    {
        using var handler = new PreviewHandler();
        using var previews = new WebPreviewService(handler, NullLogger<WebPreviewService>.Instance);
        using var services = new ServiceCollection().BuildServiceProvider();
        var interaction = new ChatLinkInteraction(new RecordingFileOperations(), previews, new ChatWindowSettings(services),
            NullLogger<ChatLinkInteraction>.Instance);
        var owner = CreateToastWindow();
        var other = CreateToastWindow();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "removed.txt");

        try
        {
            owner.Show();
            other.Show();
            owner.UpdateLayout();
            other.UpdateLayout();
            await interaction.ExecuteAsync(owner, ChatLinkTarget.Parse(path), ChatLinkAction.Reveal);

            Assert.That(GetToasts(owner).Single().Title, Is.EqualTo(LocaleKey.ChatLink_MissingTarget.I18N()));
            Assert.That(GetToasts(other), Is.Empty);
        }
        finally
        {
            other.Close();
            owner.Close();
        }

        static Window CreateToastWindow()
        {
            var resources = new ResourceInclude(new Uri("avares://ShadUI/"))
            {
                Source = new Uri("avares://ShadUI/Controls/Toast/ToastHost.axaml")
            };
            if (!resources.TryGetResource("ToastHostTheme", null, out var resource) || resource is not ControlTheme theme)
            {
                throw new AssertionException("No toast host theme.");
            }

            return new Window { Width = 400, Height = 220, Content = new ToastHost { Theme = theme } };
        }

        static IEnumerable<Toast> GetToasts(Window window) =>
            window.GetVisualDescendants().OfType<ItemsControl>().SelectMany(corner => corner.Items.OfType<Toast>());
    }

    [AvaloniaTest]
    public void FileIcons_ForMarkdownLinksAndToolReferences_UseDirectoryHintsAndMimeCategoriesWithoutFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        (string Name, LucideIconKind Icon, bool IsDirectory)[] cases =
        [
            ("program.EXE", LucideIconKind.AppWindow, false),
            ("data.bin", LucideIconKind.File, false),
            ("unknown.unknown-type", LucideIconKind.File, false),
            ("image.TIFF", LucideIconKind.FileImage, false),
            ("folder.TIFF", LucideIconKind.Folder, true),
            ("no-extension", LucideIconKind.File, false)
        ];
        var markdown = string.Join("\n\n", cases.Select(entry =>
        {
            var path = Path.Combine(root, entry.Name);
            if (entry.IsDirectory) path += Path.DirectorySeparatorChar;
            return $"[{entry.Name}]({ChatLinkTarget.ToFileUri(path)})";
        }));
        var renderer = new ChatLinkRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(markdown)) };
        var blocks = renderer.GetVisualDescendants().OfType<MarkdownTextBlock>();
        var references = cases.Select(entry => new ChatPluginFileReference(Path.Combine(root, entry.Name)) { IsDirectory = entry.IsDirectory });

        Assert.That(blocks.Select(block => block.Inlines?.OfType<Link>().Single().Inlines
                .OfType<InlineUIContainer>().Single().Child is Panel panel ? panel.Children.OfType<LucideIcon>().Single().Kind : null),
            Is.EqualTo(cases.Select(entry => entry.Icon)));
        Assert.That(references.Select(reference => reference.Icon), Is.EqualTo(cases.Select(entry => entry.Icon)));
    }

    [Test]
    public void FileReference_WhenSerialized_PreservesDirectoryHintAndReadsLegacyRecords()
    {
        const string path = "folder.TIFF";
        var reference = new ChatPluginFileReference(path) { IsDirectory = true };
        var restored = MessagePackSerializer.Deserialize<ChatPluginFileReference>(MessagePackSerializer.Serialize(reference));
        Assert.That(restored.FullPath, Is.EqualTo(path));
        Assert.That(restored.Icon, Is.EqualTo(LucideIconKind.Folder));

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(3);
        writer.Write(path);
        writer.WriteNil();
        writer.WriteNil();
        writer.Flush();

        var legacy = MessagePackSerializer.Deserialize<ChatPluginFileReference>(buffer.WrittenMemory);
        Assert.That(legacy.FullPath, Is.EqualTo(path));
        Assert.That(legacy.Icon, Is.EqualTo(LucideIconKind.FileImage));
    }

    [Test]
    public void Parse_NativePathAndEncodedFileUri_KeepSpacesUnicodeAndLiteralPercent()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\资料\a %23 #.bin" : "/资料/a %23 #.bin";
        Assert.Multiple(() =>
        {
            Assert.That(ChatLinkTarget.Parse(path), Is.EqualTo(new ChatLinkTarget(ChatLinkKind.Local, path)));
            Assert.That(ChatLinkTarget.Parse(ChatLinkTarget.ToFileUri(path)),
                Is.EqualTo(new ChatLinkTarget(ChatLinkKind.Local, path)));
        });
    }

    [TestCase("relative/file.txt")]
    [TestCase("file:relative.txt")]
    [TestCase("https://example.org/\n")]
    [TestCase("javascript:alert(1)")]
    public void Parse_NonActionableAddress_RejectsWithoutResolvingAgainstWorkingDirectory(string address)
    {
        Assert.That(ChatLinkTarget.Parse(address).Kind, Is.EqualTo(ChatLinkKind.Unsupported));
    }

    [Test]
    public void Parse_ForeignPlatformPath_DoesNotTreatItAsNative()
    {
        var path = OperatingSystem.IsWindows() ? "/home/doc.txt" : @"C:\doc.txt";
        Assert.That(ChatLinkTarget.Parse(path).Kind, Is.EqualTo(ChatLinkKind.Unsupported));
    }

    [TestCase("http://127.0.0.1/")]
    [TestCase("http://2130706433/")]
    [TestCase("http://[::ffff:192.168.1.1]/")]
    [TestCase("http://169.254.169.254/")]
    [TestCase("https://person:secret@example.com/")]
    [TestCase("https://myhost.local/")]
    public void IsAllowed_PrivateOrCredentialedPreview_RejectsBeforeNetworkAccess(string address)
    {
        Assert.That(LinkPreviewTransport.IsAllowed(new Uri(address)), Is.False);
    }

    [Test]
    public async Task ReadAsync_ShareTagsInLargeHtmlHead_PreservesMetadataAndRelativeMedia()
    {
        var html = "<head><title>Fallback title</title><meta property='og:title' content='Preview &amp; title'>" +
                   "<meta name='description' content='Page summary'><meta property='og:image' content='cover.png'>" +
                   "<link rel='icon' href='/icon.png'></head><body>" + new string('x', 600 * 1024);
        using var content = new StringContent(html);
        var metadata = await WebPreviewMetadata.ReadAsync(new Uri("https://example.org/article/index"), content, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(metadata.Title, Is.EqualTo("Preview & title"));
            Assert.That(metadata.Description, Is.EqualTo("Page summary"));
            Assert.That(metadata.Cover, Is.EqualTo(new Uri("https://example.org/article/cover.png")));
            Assert.That(metadata.Icon, Is.EqualTo(new Uri("https://example.org/icon.png")));
        });
    }

    [Test]
    public async Task GetAsync_WhenPageHasAQuery_DoesNotSendARequest()
    {
        using var handler = new PreviewHandler();
        handler.PageReady.TrySetResult();
        using var previews = new WebPreviewService(handler, NullLogger<WebPreviewService>.Instance);

        var result = await previews.GetAsync(new Uri("https://8.8.8.8/article?token=Aa"), CancellationToken.None);

        Assert.That(result.IsUnavailable, Is.True);
        Assert.That(await previews.GetSiteFaviconAsync(new Uri("https://8.8.8.8/article?token=Aa"), CancellationToken.None), Is.Null);
        Assert.That(handler.RequestCount, Is.Zero);
    }

    [Test]
    public async Task SiteFavicon_WhenMissing_SharesRequestsAndCachesMissesByOrigin()
    {
        using var handler = new PreviewHandler();
        using var previews = new WebPreviewService(handler, NullLogger<WebPreviewService>.Instance);
        var first = previews.GetSiteFaviconAsync(new Uri("https://8.8.8.8/story#fragment"), CancellationToken.None);
        var second = previews.GetSiteFaviconAsync(new Uri("https://8.8.8.8/other"), CancellationToken.None);
        handler.PageReady.TrySetResult();
        Assert.That(await Task.WhenAll(first, second), Has.All.Null);

        await previews.GetSiteFaviconAsync(new Uri("https://8.8.8.8/third"), CancellationToken.None);
        await previews.GetSiteFaviconAsync(new Uri("http://8.8.8.8/story"), CancellationToken.None);
        Assert.That(handler.Requests, Is.EqualTo(new[]
        {
            new Uri("https://8.8.8.8/favicon.ico"),
            new Uri("http://8.8.8.8/favicon.ico")
        }));
    }

    [AvaloniaTest]
    public async Task SiteFavicon_WhenLinksEnterTheViewport_OnlyFetchesTheirSiteIcons()
    {
        using var handler = new PreviewHandler();
        handler.PageReady.TrySetResult();
        handler.SiteIcon = CreateSiteIcon();
        using var previews = new WebPreviewService(handler, NullLogger<WebPreviewService>.Instance);
        using var services = new ServiceCollection().BuildServiceProvider();
        var interaction = new ChatLinkInteraction(new RecordingFileOperations(), previews, new ChatWindowSettings(services),
            NullLogger<ChatLinkInteraction>.Instance);

        var markdown = "[first](https://8.8.8.8/story) and [same site](https://8.8.8.8/other) " +
            "and [query](https://1.0.0.1/article?token=abc)\n\n" +
            string.Concat(Enumerable.Repeat("Spacer paragraph\n\n", 25)) +
            "[below fold](https://1.1.1.1/story)";
        var renderer = new ChatLinkRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(markdown)) };
        var scroll = new ScrollViewer
        {
            Content = renderer,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        var window = new Window { Width = 360, Height = 180, Content = scroll };
        window.SetValue(ChatLinkInteraction.InteractionProperty, interaction);
        // The icon needs nonzero bounds to receive a visible viewport.
        window.Resources["FontSizeM"] = 14d;
        window.Resources["FontSizeL"] = 16d;
        window.Styles.Add(new StyleInclude(new Uri("avares://Everywhere.Core/"))
        {
            Source = new Uri("avares://LiveMarkdown.Avalonia/Styles.axaml")
        });

        try
        {
            window.Show();
            window.UpdateLayout();
            var firstBlock = renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().First();
            await EventuallyAsync(() => firstBlock.GetVisualDescendants().OfType<Image>().Count() == 2);
            Assert.That(handler.Requests, Is.EqualTo(new[] { new Uri("https://8.8.8.8/favicon.ico") }));

            scroll.Offset = new Vector(0, scroll.ScrollBarMaximum.Y);
            window.UpdateLayout();
            Assert.That(scroll.Offset.Y, Is.EqualTo(scroll.ScrollBarMaximum.Y).And.GreaterThan(0));
            await EventuallyAsync(() => handler.RequestCount == 2);

            const string added = "\n\n[**streamed**](https://9.9.9.9/new)";
            // Everywhere publishes a full parsed snapshot for each accepted stream update.
            renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(
                Markdown.Parse(markdown + added, MarkdownUpdateProducer.DefaultPipeline), version: 1);
            window.UpdateLayout();
            scroll.Offset = new Vector(0, scroll.ScrollBarMaximum.Y);
            window.UpdateLayout();
            var lastBlock = renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().Last();
            Assert.That(lastBlock.ActualText, Is.EqualTo("streamed"));
            await EventuallyAsync(() => lastBlock.GetVisualDescendants().OfType<Image>().Any());
            var streamedLink = lastBlock.Inlines?.OfType<Link>().Single() ?? throw new AssertionException("No streamed link.");
            Assert.That(streamedLink.Inlines.OfType<InlineUIContainer>().Count(), Is.EqualTo(1));
            Assert.That(handler.Requests, Is.EquivalentTo(new[]
            {
                new Uri("https://8.8.8.8/favicon.ico"),
                new Uri("https://1.1.1.1/favicon.ico"),
                new Uri("https://9.9.9.9/favicon.ico")
            }));

            renderer.DocumentUpdate = null;
            Assert.That(streamedLink.Inlines.OfType<InlineUIContainer>(), Is.Empty);
        }
        finally
        {
            window.Close();
        }
    }

    private static Point GetLinkPoint(MarkdownTextBlock block, Window window)
    {
        var link = block.Inlines?.OfType<Link>().Single() ?? throw new AssertionException("No inline link.");
        var geometry = block.GetInlineGeometry(link) ?? throw new AssertionException("No inline geometry.");
        return block.TranslatePoint(geometry.Rects.Last().Center, window) ?? throw new AssertionException("No link coordinates.");
    }

    private static byte[] CreateSiteIcon()
    {
        using var bitmap = new SkiaSharp.SKBitmap(16, 16);
        bitmap.Erase(SkiaSharp.SKColors.DodgerBlue);
        using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static async Task EventuallyAsync(Func<bool> check)
    {
        for (var attempt = 0; attempt < 160; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (check()) return;
            await Task.Delay(15);
        }
        Assert.Fail("The link interaction did not update the window.");
    }

    private sealed class RecordingFileOperations : ILinkFileOperations
    {
        public TaskCompletionSource<string> Revealed { get; } = new();
        public TaskCompletionSource<string> RevealedDirectory { get; } = new();
        public TaskCompletionSource<string> EnteredDirectory { get; } = new();

        public Task<ChatLinkOperationStatus> RevealItemAsync(string path, bool isDirectory, TopLevel owner, CancellationToken cancellationToken)
        {
            (isDirectory ? RevealedDirectory : Revealed).TrySetResult(path);
            return Task.FromResult(ChatLinkOperationStatus.Accepted);
        }

        public Task<ChatLinkOperationStatus> OpenDirectoryAsync(string path, TopLevel owner, CancellationToken cancellationToken)
        {
            EnteredDirectory.TrySetResult(path);
            return Task.FromResult(ChatLinkOperationStatus.Accepted);
        }

        public Task<ChatLinkOperationStatus> OpenFileWithAsync(string path, TopLevel owner, CancellationToken cancellationToken) =>
            throw new AssertionException("Left click should not open the file.");
    }

    private sealed class PreviewHandler : HttpMessageHandler, IHttpClientFactory
    {
        public int RequestCount => Requests.Count;
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public TaskCompletionSource PageReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? SiteIcon { get; set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri ?? throw new AssertionException("Missing preview request URI."));
            await PageReady.Task.WaitAsync(cancellationToken);
            if (request.RequestUri.AbsolutePath == "/favicon.ico" && SiteIcon is { } icon)
            {
                var content = new ByteArrayContent(icon);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
            }

            return request.RequestUri?.AbsolutePath == "/article"
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("<head><title>Preview article</title><meta name='description' content='A short description for the chat preview.'></head>",
                        System.Text.Encoding.UTF8,
                        "text/html")
                }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }
}
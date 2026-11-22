namespace Everywhere.Mods.Themes;

/// <summary>
/// Thread-safe public registry where built-in presets, JSON theme files, and third-party mods
/// register <see cref="ThemeDefinition"/> entries.
/// </summary>
public static class ThemeRegistry
{
    private readonly record struct RegisteredEntry(
        ThemeDefinition Theme,
        long Order,
        int RegistrationToken);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, RegisteredEntry> Entries = new(StringComparer.OrdinalIgnoreCase);

    private static long _nextOrder;
    private static int _nextToken = 1;

    /// <summary>
    /// Raised after one or more themes are added or removed.
    /// </summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Registers a single custom theme and returns a handle that removes it on disposal.
    /// </summary>
    /// <remarks>
    /// Mods consuming <c>everywhere.themes</c> should pass the returned handle to
    /// <see cref="Everywhere.Modding.Abstractions.IModContext.Register(object)"/> so hot unload removes their themes cleanly.
    /// </remarks>
    public static IDisposable RegisterTheme(ThemeDefinition theme) => RegisterThemes([theme]);

    /// <summary>
    /// Registers a batch of themes atomically and returns a handle that removes all of them on disposal.
    /// </summary>
    public static IDisposable RegisterThemes(IEnumerable<ThemeDefinition> themes)
    {
        ArgumentNullException.ThrowIfNull(themes);

        var items = themes.ToArray();
        foreach (var theme in items)
        {
            ValidateTheme(theme);
        }

        if (items.Length == 0)
        {
            return EmptyDisposable.Shared;
        }

        int token;
        lock (Gate)
        {
            token = _nextToken++;
            var addedIds = new List<string>(items.Length);

            try
            {
                foreach (var theme in items)
                {
                    if (Entries.ContainsKey(theme.Id))
                    {
                        throw new ArgumentException($"A theme with ID '{theme.Id}' is already registered.", nameof(themes));
                    }

                    Entries.Add(theme.Id, new RegisteredEntry(theme, _nextOrder++, token));
                    addedIds.Add(theme.Id);
                }
            }
            catch
            {
                foreach (var id in addedIds)
                {
                    Entries.Remove(id);
                }

                throw;
            }
        }

        Changed?.Invoke(null, EventArgs.Empty);
        return new RegistrationHandle(token);
    }

    /// <summary>
    /// Returns all currently registered themes, optionally filtered by polarity variant.
    /// </summary>
    public static IReadOnlyList<ThemeDefinition> GetThemes(ThemeBaseVariant? variant = null)
    {
        lock (Gate)
        {
            return
            [
                .. Entries.Values
                    .Where(entry => variant is null || entry.Theme.Variant == variant.Value)
                    .OrderBy(entry => entry.Theme.IsHostDefault ? 0 : entry.Theme.IsCustomJson ? 2 : 1)
                    .ThenBy(entry => entry.Order)
                    .Select(entry => entry.Theme)
            ];
        }
    }

    /// <summary>
    /// Finds a registered theme by identifier, or returns <see langword="null"/> when absent.
    /// </summary>
    public static ThemeDefinition? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (Gate)
        {
            return Entries.TryGetValue(id.Trim(), out var entry) ? entry.Theme : null;
        }
    }

    private static void ValidateTheme(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        if (string.IsNullOrWhiteSpace(theme.Id))
        {
            throw new ArgumentException("Theme ID cannot be empty.", nameof(theme));
        }

        if (!theme.IsHostDefault &&
            (string.Equals(theme.Id, BuiltInThemes.DefaultLightId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(theme.Id, BuiltInThemes.DefaultDarkId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Theme ID '{theme.Id}' is reserved for the built-in default.", nameof(theme));
        }
    }

    private static void UnregisterByToken(int token)
    {
        var hasRemoved = false;
        lock (Gate)
        {
            var keysToRemove = Entries
                .Where(pair => pair.Value.RegistrationToken == token)
                .Select(pair => pair.Key)
                .ToArray();

            foreach (var key in keysToRemove)
            {
                Entries.Remove(key);
                hasRemoved = true;
            }
        }

        if (hasRemoved)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private sealed class RegistrationHandle(int token) : IDisposable
    {
        private int _isDisposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            UnregisterByToken(token);
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Shared { get; } = new();

        public void Dispose() { }
    }
}

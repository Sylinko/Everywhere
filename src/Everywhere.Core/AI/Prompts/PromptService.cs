using Everywhere.AI.Prompts.Database;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.AI.Prompts;

/// <summary>
/// Manages persisted user prompts and exposes the virtual built-in default prompt.
/// </summary>
/// <remarks>
/// The service intentionally keeps Prompt Manager storage independent from assistants. Assistant
/// references and settings migration are separate phases; this service only owns prompt CRUD and the
/// default prompt projection.
/// </remarks>
public interface IPromptService
{
    /// <summary>
    /// Virtual built-in default prompt. It is returned by lookups for <see cref="Guid.Empty"/>.
    /// </summary>
    PromptDefinition DefaultPrompt { get; }

    /// <summary>
    /// Lists the virtual default prompt followed by persisted user prompts.
    /// </summary>
    Task<IReadOnlyList<PromptDefinition>> ListPromptsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists only prompts stored in <c>prompt.db</c>.
    /// </summary>
    Task<IReadOnlyList<PromptDefinition>> ListUserPromptsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a prompt by ID, resolving <see cref="Guid.Empty"/> to the virtual default prompt.
    /// </summary>
    Task<PromptDefinition?> GetPromptAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a persisted user prompt.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the template is empty or the requested ID is <see cref="Guid.Empty"/>.
    /// </exception>
    Task<PromptDefinition> CreatePromptAsync(PromptCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a persisted user prompt, or returns null when the prompt does not exist or is the virtual default prompt.
    /// </summary>
    Task<PromptDefinition?> UpdatePromptAsync(Guid id, PromptUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a persisted user prompt. The virtual default prompt is never deleted.
    /// </summary>
    Task<bool> DeletePromptAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// SQLite-backed Prompt Manager service.
/// </summary>
/// <remarks>
/// The implementation never persists <see cref="Guid.Empty"/>. That invariant keeps the empty GUID
/// available as a stable reference to the built-in default prompt and prevents "blank prompt" from
/// becoming ambiguous with "use default prompt".
/// </remarks>
public sealed class PromptService(
    IDbContextFactory<PromptDbContext> dbFactory,
    IDefaultPromptProvider defaultPromptProvider
) : IPromptService
{
    public PromptDefinition DefaultPrompt => defaultPromptProvider.DefaultPrompt;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptDefinition>> ListPromptsAsync(CancellationToken cancellationToken = default)
    {
        var prompts = new List<PromptDefinition> { DefaultPrompt };
        prompts.AddRange(await ListUserPromptsAsync(cancellationToken));
        return prompts;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptDefinition>> ListUserPromptsAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entities = await dbContext.Prompts
            .AsNoTracking()
            .OrderBy(static prompt => prompt.Name)
            .ThenBy(static prompt => prompt.Id)
            .ToListAsync(cancellationToken);

        return [.. entities.Select(ToDefinition)];
    }

    /// <inheritdoc />
    public async Task<PromptDefinition?> GetPromptAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) return DefaultPrompt;

        await using var dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await dbContext.Prompts
            .AsNoTracking()
            .FirstOrDefaultAsync(prompt => prompt.Id == id, cancellationToken);

        return entity is null ? null : ToDefinition(entity);
    }

    /// <inheritdoc />
    public async Task<PromptDefinition> CreatePromptAsync(PromptCreateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateTemplate(request.Template);

        var id = request.Id ?? Guid.CreateVersion7();
        if (id == Guid.Empty)
        {
            throw new ArgumentException("User prompts must use a non-empty GUID.", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new PromptEntity
        {
            Id = id,
            Name = NormalizeName(request.Name),
            Template = request.Template,
            CreatedAt = now,
            UpdatedAt = now,
            Source = request.Source,
            MetadataPayload = ClonePayload(request.MetadataPayload)
        };

        await using var dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);
        dbContext.Prompts.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDefinition(entity);
    }

    /// <inheritdoc />
    public async Task<PromptDefinition?> UpdatePromptAsync(Guid id, PromptUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) return null;

        ValidateTemplate(request.Template);

        await using var dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await dbContext.Prompts.FirstOrDefaultAsync(prompt => prompt.Id == id, cancellationToken);
        if (entity is null) return null;

        entity.Name = NormalizeName(request.Name);
        entity.Template = request.Template;
        entity.MetadataPayload = ClonePayload(request.MetadataPayload);
        if (request.Source is { } source)
        {
            entity.Source = source;
        }

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDefinition(entity);
    }

    /// <inheritdoc />
    public async Task<bool> DeletePromptAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) return false;

        await using var dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await dbContext.Prompts
            .Where(prompt => prompt.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted > 0;
    }

    private static PromptDefinition ToDefinition(PromptEntity entity) =>
        new(
            entity.Id,
            entity.Name,
            entity.Template,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.Source,
            ClonePayload(entity.MetadataPayload));

    private static string? NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static byte[]? ClonePayload(byte[]? payload) =>
        payload?.ToArray();

    /// <summary>
    /// Enforces the v1 rule that empty prompt content is not a persisted prompt.
    /// </summary>
    /// <remarks>
    /// Missing or empty assistant prompt references resolve to the virtual default prompt instead of
    /// creating empty user prompts.
    /// </remarks>
    private static void ValidateTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new ArgumentException("Prompt template cannot be empty.", nameof(template));
        }
    }
}
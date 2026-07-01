// Copyright (c) Microsoft. All rights reserved.

using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

/// <summary>
///     Web search engine connector interface.
/// </summary>
public interface IWebSearchEngineConnector : IDisposable
{
    /// <summary>
    ///     Execute a web search engine search.
    /// </summary>
    /// <param name="query">Query to search.</param>
    /// <param name="count">Number of results.</param>
    /// <param name="cancellationToken">
    ///     The <see cref="CancellationToken" /> to monitor for cancellation requests. The default
    ///     is <see cref="CancellationToken.None" />.
    /// </param>
    /// <returns>First snippet returned from search.</returns>
    Task<IEnumerable<TextSearchResult>> SearchAsync(string query, int count, CancellationToken cancellationToken = default);
}
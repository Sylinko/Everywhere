namespace Everywhere.Automation;

/// <summary>
/// Configures the native provider timeout policy used by platform Automation services.
/// </summary>
public readonly record struct VisualContextPlatformOptions
{
    /// <summary>
    /// Gets the maximum wait for establishing communication with an accessibility provider.
    /// </summary>
    /// <remarks>
    /// On Windows this maps to UI Automation's connection timeout. It does not bound a property or
    /// navigation request after the provider connection has been established.
    /// </remarks>
    public TimeSpan ConnectionTimeout { get; }

    /// <summary>
    /// Gets the maximum wait for an individual provider transaction.
    /// </summary>
    /// <remarks>
    /// On Windows this maps to UI Automation's transaction timeout and limits one provider request.
    /// Snapshot traversal applies a separate aggregate elapsed-time budget across requests.
    /// </remarks>
    public TimeSpan TransactionTimeout { get; }

    /// <summary>
    /// Initializes explicit native provider timeouts for one platform policy.
    /// </summary>
    /// <param name="connectionTimeout">The maximum wait for establishing provider communication.</param>
    /// <param name="transactionTimeout">The maximum wait for one provider transaction.</param>
    public VisualContextPlatformOptions(TimeSpan connectionTimeout, TimeSpan transactionTimeout)
    {
        ValidateTimeout(connectionTimeout, nameof(connectionTimeout));
        ValidateTimeout(transactionTimeout, nameof(transactionTimeout));

        ConnectionTimeout = connectionTimeout;
        TransactionTimeout = transactionTimeout;
    }

    /// <summary>
    /// Validates that both platform timeout boundaries are positive and finite.
    /// </summary>
    public void Validate()
    {
        ValidateTimeout(ConnectionTimeout, nameof(ConnectionTimeout));
        ValidateTimeout(TransactionTimeout, nameof(TransactionTimeout));
    }

    private static void ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The timeout must be positive and finite.");
        }
    }
}
namespace ExpressionBinding.Avalonia;

/// <summary>
/// Represents a syntax or type-binding error in an expression binding.
/// </summary>
public sealed class ExpressionBindingException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ExpressionBindingException"/> class.
    /// </summary>
    public ExpressionBindingException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExpressionBindingException"/> class.
    /// </summary>
    public ExpressionBindingException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
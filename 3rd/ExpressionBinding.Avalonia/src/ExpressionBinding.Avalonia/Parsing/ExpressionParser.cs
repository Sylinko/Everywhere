namespace ExpressionBinding.Avalonia.Parsing;

internal static partial class ExpressionParser
{
    private static partial bool TryParse(string input, out ExpressionSyntax value);

    internal static ExpressionSyntax Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ExpressionBindingException("The expression cannot be empty.");
        }

        if (!TryParse(input, out var expression))
        {
            throw new ExpressionBindingException($"Invalid expression: '{input}'.");
        }

        return expression;
    }
}
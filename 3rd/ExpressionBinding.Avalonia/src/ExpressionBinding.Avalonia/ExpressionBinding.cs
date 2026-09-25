using System.Diagnostics.CodeAnalysis;
using Avalonia.Collections;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Metadata;
using ExpressionBinding.Avalonia.Binding;
using ExpressionBinding.Avalonia.Parsing;

namespace ExpressionBinding.Avalonia;

/// <summary>
/// Creates a one-way multi-binding whose value is computed from an expression.
/// </summary>
[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicConstructors |
    DynamicallyAccessedMemberTypes.PublicFields |
    DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed class ExpressionBinding : MarkupExtension
{
    /// <summary>
    /// Gets or sets the expression to evaluate.
    /// </summary>
    public required string Expression { get; set; }

    /// <summary>
    /// Gets the expression arguments. <c>A</c> addresses the first item, <c>B</c> the second, and so on.
    /// </summary>
    [Content, AssignBinding]
    public AvaloniaList<object?> Arguments { get; set; } = [];

    /// <summary>
    /// Gets or sets the registry used to resolve function calls.
    /// </summary>
    public ExpressionRegistry Registry { get; set; } = ExpressionRegistry.Default;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var syntax = ExpressionParser.Parse(Expression);
        var bindings = new List<BindingBase>(Arguments.Count);

        foreach (var argument in Arguments)
        {
            bindings.Add(
                argument as BindingBase ?? new CompiledBinding
                {
                    Source = argument,
                    Mode = BindingMode.OneWay
                });
        }

        return new MultiBinding
        {
            Bindings = bindings,
            Mode = BindingMode.OneWay,
            Converter = new ExpressionConverter(Expression, syntax, Registry)
        };
    }
}
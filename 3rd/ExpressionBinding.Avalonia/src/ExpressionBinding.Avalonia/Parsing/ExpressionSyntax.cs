namespace ExpressionBinding.Avalonia.Parsing;

internal abstract record ExpressionSyntax;

internal sealed record NumberSyntax(decimal Value) : ExpressionSyntax;

internal sealed record StringSyntax(string Value) : ExpressionSyntax;

internal sealed record LiteralSyntax(LiteralKind Kind) : ExpressionSyntax;

internal sealed record IdentifierSyntax(string Name) : ExpressionSyntax;

internal sealed record CallSyntax(string Name, IReadOnlyList<ExpressionSyntax> Arguments) : ExpressionSyntax;

internal sealed record UnarySyntax(UnaryOperator Operator, ExpressionSyntax Operand) : ExpressionSyntax;

internal sealed record BinarySyntax(BinaryOperator Operator, ExpressionSyntax Left, ExpressionSyntax Right) : ExpressionSyntax;

internal sealed record ConditionalSyntax(ExpressionSyntax Condition, ExpressionSyntax WhenTrue, ExpressionSyntax WhenFalse) : ExpressionSyntax;

internal sealed record SwitchSyntax(ExpressionSyntax Input, IReadOnlyList<SwitchArmSyntax> Arms) : ExpressionSyntax;

internal sealed record SwitchArmSyntax(IReadOnlyList<SwitchPatternSyntax> Patterns, ExpressionSyntax? Guard, ExpressionSyntax Result);

internal abstract record SwitchPatternSyntax;

internal sealed record ConstantPatternSyntax(ExpressionSyntax Value) : SwitchPatternSyntax;

internal sealed record RelationalPatternSyntax(BinaryOperator Operator, ExpressionSyntax Value) : SwitchPatternSyntax;

internal sealed record DiscardPatternSyntax : SwitchPatternSyntax;

internal enum LiteralKind
{
    False,
    True,
    Null,
    Unset
}

internal enum UnaryOperator
{
    Plus,
    Negate,
    LogicalNot,
    OnesComplement
}

internal enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    LeftShift,
    RightShift,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Equal,
    NotEqual,
    BitwiseAnd,
    ExclusiveOr,
    BitwiseOr,
    LogicalAnd,
    LogicalOr
}
using Parlot.Fluent;
using Parlot.SourceGenerator;
using static Parlot.Fluent.Parsers;

namespace ExpressionBinding.Avalonia.Parsing;

internal static partial class ExpressionParser
{
    [GenerateParser(nameof(TryParse))]
    [IncludeUsings("System.Collections.Generic", "ExpressionBinding.Avalonia.Parsing")]
    private static Parser<ExpressionSyntax> Build()
    {
        var expression = Deferred<ExpressionSyntax>();

        var openParen = Terms.Char('(');
        var closeParen = Terms.Char(')');
        var comma = Terms.Char(',');
        var identifierText = Terms.Identifier().Then(static value => value.ToString());

        var number = Terms.Decimal()
            .Then<ExpressionSyntax>(static value => new NumberSyntax(value));

        var @string = Terms.String(StringLiteralQuotes.SingleOrDouble)
            .Then<ExpressionSyntax>(static value => new StringSyntax(value.ToString()));

        var arguments = OneOf(
            Separated(comma, expression),
            Always().Then<IReadOnlyList<ExpressionSyntax>>(
                static _ => Array.Empty<ExpressionSyntax>()));

        var call = identifierText
            .AndSkip(openParen)
            .And(arguments)
            .AndSkip(closeParen)
            .Then<ExpressionSyntax>(static value => new CallSyntax(value.Item1, value.Item2));

        var identifier = identifierText
            .Then<ExpressionSyntax>(static value => value switch
            {
                "false" => new LiteralSyntax(LiteralKind.False),
                "true" => new LiteralSyntax(LiteralKind.True),
                "null" => new LiteralSyntax(LiteralKind.Null),
                "unset" => new LiteralSyntax(LiteralKind.Unset),
                _ => new IdentifierSyntax(value)
            });

        var group = Between(openParen, expression, closeParen);
        var primary = OneOf(number, @string, call, identifier, group);

        var unary = primary.Unary(
            (Terms.Char('+'), static value => new UnarySyntax(UnaryOperator.Plus, value)),
            (Terms.Char('-'), static value => new UnarySyntax(UnaryOperator.Negate, value)),
            (Terms.Char('!'), static value => new UnarySyntax(UnaryOperator.LogicalNot, value)),
            (Terms.Char('~'), static value => new UnarySyntax(UnaryOperator.OnesComplement, value)));

        var multiplicative = unary.LeftAssociative(
            (Terms.Char('*'), static (left, right) => new BinarySyntax(BinaryOperator.Multiply, left, right)),
            (Terms.Char('/'), static (left, right) => new BinarySyntax(BinaryOperator.Divide, left, right)),
            (Terms.Char('%'), static (left, right) => new BinarySyntax(BinaryOperator.Modulo, left, right)));

        var additive = multiplicative.LeftAssociative(
            (Terms.Char('+'), static (left, right) => new BinarySyntax(BinaryOperator.Add, left, right)),
            (Terms.Char('-'), static (left, right) => new BinarySyntax(BinaryOperator.Subtract, left, right)));

        var shift = additive.LeftAssociative(
            (Terms.Text("<<"), static (left, right) => new BinarySyntax(BinaryOperator.LeftShift, left, right)),
            (Terms.Text(">>"), static (left, right) => new BinarySyntax(BinaryOperator.RightShift, left, right)));

        var relational = shift.LeftAssociative(
            (Terms.Text("<="), static (left, right) => new BinarySyntax(BinaryOperator.LessThanOrEqual, left, right)),
            (Terms.Text(">="), static (left, right) => new BinarySyntax(BinaryOperator.GreaterThanOrEqual, left, right)),
            (Terms.Text("<"), static (left, right) => new BinarySyntax(BinaryOperator.LessThan, left, right)),
            (Terms.Text(">"), static (left, right) => new BinarySyntax(BinaryOperator.GreaterThan, left, right)));

        var equality = relational.LeftAssociative(
            (Terms.Text("=="), static (left, right) => new BinarySyntax(BinaryOperator.Equal, left, right)),
            (Terms.Text("!="), static (left, right) => new BinarySyntax(BinaryOperator.NotEqual, left, right)));

        var bitwiseAnd = equality.LeftAssociative(
            (Terms.Char('&'), static (left, right) => new BinarySyntax(BinaryOperator.BitwiseAnd, left, right)));

        var exclusiveOr = bitwiseAnd.LeftAssociative(
            (Terms.Char('^'), static (left, right) => new BinarySyntax(BinaryOperator.ExclusiveOr, left, right)));

        var bitwiseOr = exclusiveOr.LeftAssociative(
            (Terms.Char('|'), static (left, right) => new BinarySyntax(BinaryOperator.BitwiseOr, left, right)));

        var logicalAnd = bitwiseOr.LeftAssociative(
            (Terms.Text("&&"), static (left, right) => new BinarySyntax(BinaryOperator.LogicalAnd, left, right)));

        var logicalOr = logicalAnd.LeftAssociative(
            (Terms.Text("||"), static (left, right) => new BinarySyntax(BinaryOperator.LogicalOr, left, right)));

        var switchKeyword = identifierText.When(static (_, value) => value == "switch");
        var whenKeyword = identifierText.When(static (_, value) => value == "when");
        var orKeyword = identifierText.When(static (_, value) => value == "or");
        var patternKeyword = identifierText
            .When(static (_, value) => value is "false" or "true" or "null" or "unset")
            .Then<ExpressionSyntax>(static value => value switch
            {
                "false" => new LiteralSyntax(LiteralKind.False),
                "true" => new LiteralSyntax(LiteralKind.True),
                "null" => new LiteralSyntax(LiteralKind.Null),
                "unset" => new LiteralSyntax(LiteralKind.Unset),
                _ => throw new InvalidOperationException("Unsupported pattern literal.")
            });
        var patternValue = OneOf(number, @string, patternKeyword);
        var relationalOperator = OneOf(
            Terms.Text("<=").Then(static _ => BinaryOperator.LessThanOrEqual),
            Terms.Text(">=").Then(static _ => BinaryOperator.GreaterThanOrEqual),
            Terms.Text("<").Then(static _ => BinaryOperator.LessThan),
            Terms.Text(">").Then(static _ => BinaryOperator.GreaterThan));
        var relationalPattern = relationalOperator
            .And(patternValue)
            .Then<SwitchPatternSyntax>(static value => new RelationalPatternSyntax(value.Item1, value.Item2));
        var constantPattern = patternValue
            .Then<SwitchPatternSyntax>(static value => new ConstantPatternSyntax(value));
        var discardPattern = Terms.Char('_')
            .Then<SwitchPatternSyntax>(static _ => new DiscardPatternSyntax());
        var atomicPattern = OneOf(discardPattern, relationalPattern, constantPattern);
        var patterns = Separated(orKeyword, atomicPattern);
        var arrow = Terms.Text("=>");
        var guardedArm = patterns
            .AndSkip(whenKeyword)
            .And(expression)
            .AndSkip(arrow)
            .And(expression)
            .Then(static value => new SwitchArmSyntax(value.Item1, value.Item2, value.Item3));
        var simpleArm = patterns
            .AndSkip(arrow)
            .And(expression)
            .Then(static value => new SwitchArmSyntax(value.Item1, null, value.Item2));
        var arms = Separated(comma, OneOf(guardedArm, simpleArm));
        var switchExpression = logicalOr
            .AndSkip(switchKeyword)
            .AndSkip(Terms.Char('{'))
            .And(arms)
            .AndSkip(Terms.Char('}'))
            .Then<ExpressionSyntax>(static value => new SwitchSyntax(value.Item1, value.Item2));

        var conditionalOperand = OneOf(switchExpression, logicalOr);
        var conditional = conditionalOperand
            .AndSkip(Terms.Char('?'))
            .And(expression)
            .AndSkip(Terms.Char(':'))
            .And(expression)
            .Then<ExpressionSyntax>(static value => new ConditionalSyntax(value.Item1, value.Item2, value.Item3));

        expression.Parser = OneOf(conditional, conditionalOperand);
        return expression.Eof();
    }
}

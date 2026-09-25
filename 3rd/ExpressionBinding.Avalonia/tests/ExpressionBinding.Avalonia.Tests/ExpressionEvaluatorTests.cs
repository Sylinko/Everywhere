using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Data;
using ExpressionBinding.Avalonia.Binding;
using ExpressionBinding.Avalonia.Parsing;

namespace ExpressionBinding.Avalonia.Tests;

public sealed class ExpressionEvaluatorTests
{
    [Test]
    public void Convert_WithArithmeticAndFunction_RespectsPrecedence()
    {
        var result = Evaluate("2 * A - min(A, B)", 10d, 4d);

        Assert.That(result, Is.EqualTo(16d));
    }

    [Test]
    public void Convert_WithIntegralInputs_UsesIntegralDivision()
    {
        var result = Evaluate("A / B", 7, 2);

        Assert.That(result, Is.EqualTo(3));
    }

    [Test]
    public void Convert_WithUnaryOperators_RespectsGrouping()
    {
        var result = Evaluate("-(A + 2) * -B", 3d, 4d);

        Assert.That(result, Is.EqualTo(20d));
    }

    [Test]
    public void Convert_WithRegisteredOverloads_SelectsByRuntimeInputTypes()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction("kind", (Func<int, string>)(static _ => "int"));
        registry.RegisterFunction("kind", (Func<double, string>)(static _ => "double"));
        var converter = CreateConverter("kind(A)", registry);

        var integerResult = Convert(converter, 3);
        var doubleResult = Convert(converter, 3d);

        Assert.Multiple(() =>
        {
            Assert.That(integerResult, Is.EqualTo("int"));
            Assert.That(doubleResult, Is.EqualTo("double"));
        });
    }

    [Test]
    public void Convert_AfterRegistryChanges_RebindsExistingInputSignature()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction("adjust", (Func<double, double>)(static value => value * 2));
        var converter = CreateConverter("adjust(A)", registry);

        var before = Convert(converter, 3);
        registry.RegisterFunction("adjust", (Func<int, int>)(static value => value + 1));
        var after = Convert(converter, 3);

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.EqualTo(6d));
            Assert.That(after, Is.EqualTo(4));
        });
    }

    [Test]
    public void Convert_WithConstructorStyleFunction_ReturnsNonNumericValue()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "testThickness",
            (Func<double, TestThickness>)(static uniform => new TestThickness(uniform, uniform, uniform, uniform)));
        registry.RegisterFunction(
            "testThickness",
            (Func<double, double, double, double, TestThickness>)(
                static (left, top, right, bottom) => new TestThickness(left, top, right, bottom)));

        var result = Evaluate(registry, "testThickness(A * 2, 25, B, 25)", 4d, 6d);

        Assert.That(result, Is.EqualTo(new TestThickness(8, 25, 6, 25)));
    }

    [Test]
    public void Convert_WithVariadicBuiltIn_ExpandsArguments()
    {
        var result = Evaluate("min(A, B, 8, C)", 12d, 6d, 10d);

        Assert.That(result, Is.EqualTo(6d));
    }

    [Test]
    public void Convert_WithArrayArgument_UsesParamsNormalForm()
    {
        var result = Evaluate("max(A)", new[] { 3d, 9d, 4d });

        Assert.That(result, Is.EqualTo(9d));
    }

    [Test]
    public void Convert_WithParamsDelegateType_ExpandsArguments()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "sum",
            (SumDelegate)(static values => values.Sum()));

        var result = Evaluate(registry, "sum(A, B, 3)", 1d, 2d);

        Assert.That(result, Is.EqualTo(6d));
    }

    [Test]
    public void Convert_WithFixedAndParamsOverloads_PrefersNormalForm()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "select",
            typeof(OverloadFunctions),
            nameof(OverloadFunctions.Select));

        var result = Evaluate(registry, "select(A, B)", 1d, 2d);

        Assert.That(result, Is.EqualTo("fixed"));
    }

    [Test]
    public void Convert_WithFixedPrefixAndParams_ExpandsOnlyRemainingArguments()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "sumAfter",
            typeof(OverloadFunctions),
            nameof(OverloadFunctions.SumAfter));

        var result = Evaluate(registry, "sumAfter(A, B, 3)", 10d, 2d);

        Assert.That(result, Is.EqualTo(15d));
    }

    [Test]
    public void Convert_WithEmptyParamsCall_CreatesEmptyArray()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "count",
            typeof(OverloadFunctions),
            nameof(OverloadFunctions.Count));

        var result = Evaluate(registry, "count()");

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void Convert_WithNumericOverloads_SelectsBetterConversionRegardlessOfRegistrationOrder()
    {
        var firstRegistry = new ExpressionRegistry();
        firstRegistry.RegisterFunction("kind", (Func<double, string>)(static _ => "double"));
        firstRegistry.RegisterFunction("kind", (Func<long, string>)(static _ => "long"));
        var secondRegistry = new ExpressionRegistry();
        secondRegistry.RegisterFunction("kind", (Func<long, string>)(static _ => "long"));
        secondRegistry.RegisterFunction("kind", (Func<double, string>)(static _ => "double"));

        Assert.Multiple(() =>
        {
            Assert.That(Evaluate(firstRegistry, "kind(A)", 1), Is.EqualTo("long"));
            Assert.That(Evaluate(secondRegistry, "kind(A)", 1), Is.EqualTo("long"));
        });
    }

    [Test]
    public void Convert_WithGenericParamsMethod_InfersCommonNumericType()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "genericMin",
            typeof(GenericFunctions),
            nameof(GenericFunctions.Min));

        var result = Evaluate(registry, "genericMin(A, B, 4)", 8, 2d);

        Assert.That(result, Is.EqualTo(2d));
    }

    [Test]
    public void Convert_WithGenericCollectionMethod_InfersImplementedInterfaceArgument()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "first",
            typeof(GenericFunctions),
            nameof(GenericFunctions.First));

        var result = Evaluate(registry, "first(A)", new List<double> { 2.5, 3.5 });

        Assert.That(result, Is.EqualTo(2.5));
    }

    [Test]
    public void Convert_WhenGenericConstraintIsNotSatisfied_ReturnsBindingError()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "genericMin",
            typeof(GenericFunctions),
            nameof(GenericFunctions.Min));

        var result = Evaluate(registry, "genericMin(A)", "not a number");

        Assert.That(result, Is.TypeOf<BindingNotification>());
        Assert.That(((BindingNotification)result!).Error, Is.TypeOf<ExpressionBindingException>());
    }

    [Test]
    public void Convert_WithGenericAndNonGenericOverloads_PrefersNonGenericEquivalent()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "describe",
            typeof(OverloadFunctions),
            nameof(OverloadFunctions.Describe));

        Assert.Multiple(() =>
        {
            Assert.That(Evaluate(registry, "describe(A)", 1), Is.EqualTo("int"));
            Assert.That(Evaluate(registry, "describe(A)", 1d), Is.EqualTo("generic"));
        });
    }

    [Test]
    public void Configure_WithDuplicateSignature_ThrowsArgumentException()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction("duplicate", (Func<int, int>)(static value => value));

        Assert.That(
            () => registry.RegisterFunction(
                "duplicate",
                (Func<int, string>)(static value => value.ToString(CultureInfo.InvariantCulture))),
            Throws.ArgumentException);
    }

    [Test]
    public void Configure_WithEquivalentGenericSignatures_ThrowsArgumentException()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "genericMin",
            typeof(GenericFunctions),
            nameof(GenericFunctions.Min));

        Assert.That(
            () => registry.RegisterFunction(
                "genericMin",
                typeof(OtherGenericFunctions),
                nameof(OtherGenericFunctions.Min)),
            Throws.ArgumentException);
    }

    [Test]
    public void Convert_WithBuiltInGeometryFunction_ReturnsAvaloniaValue()
    {
        var result = Evaluate("thickness(A, 12)", 8d);

        Assert.That(result, Is.EqualTo(new Thickness(8, 12)));
    }

    [Test]
    public void Convert_WithUserDefinedOperator_BindsOperatorMethod()
    {
        var result = Evaluate("A * 2", new Scalable(3));

        Assert.That(result, Is.EqualTo(new Scalable(6)));
    }

    [Test]
    public void Convert_WithUserDefinedOperatorOverloads_SelectsBetterConversion()
    {
        var result = Evaluate("A + B", new OperatorProbe("initial"), 2);

        Assert.That(result, Is.EqualTo(new OperatorProbe("long")));
    }

    [Test]
    public void Convert_WithUnsetArgument_PropagatesUnsetValue()
    {
        var converter = CreateConverter("A + 1");

        var result = converter.Convert(
            new object?[] { AvaloniaProperty.UnsetValue },
            typeof(object),
            null,
            CultureInfo.InvariantCulture);

        Assert.That(result, Is.SameAs(AvaloniaProperty.UnsetValue));
    }

    [Test]
    public void Convert_WithConditional_DoesNotEvaluateUnselectedUnsetBranch()
    {
        var converter = CreateConverter("A ? B : C");

        var whenTrue = Convert(converter, true, 12, AvaloniaProperty.UnsetValue);
        var whenFalse = Convert(converter, false, AvaloniaProperty.UnsetValue, "selected");

        Assert.Multiple(() =>
        {
            Assert.That(whenTrue, Is.EqualTo(12));
            Assert.That(whenFalse, Is.EqualTo("selected"));
        });
    }

    [Test]
    public void Convert_WithDifferentDynamicSites_DoesNotShareUnrelatedBinders()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction("increment", (Func<int, int>)(static value => value + 1));
        registry.RegisterFunction("timesTen", (Func<int, int>)(static value => value * 10));

        var result = Evaluate(registry, "increment(A) + timesTen(B) + 2 * C", 2, 3, 4);

        Assert.That(result, Is.EqualTo(41));
    }

    [Test]
    public void Convert_WithDifferentNumericLiteralSites_PreservesEachLiteral()
    {
        var result = Evaluate("A + 1 + (B + 2)", 10, 20);

        Assert.That(result, Is.EqualTo(33));
    }

    [Test]
    public void Convert_WithConditionalResultChangingType_RebindsOnlyOuterCallSite()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction("kind", (Func<int, string>)(static _ => "int"));
        registry.RegisterFunction("kind", (Func<double, string>)(static _ => "double"));
        var converter = CreateConverter("kind(A ? B : C)", registry);

        var integerResult = Convert(converter, true, 1, AvaloniaProperty.UnsetValue);
        var doubleResult = Convert(converter, false, AvaloniaProperty.UnsetValue, 2d);
        var cachedIntegerResult = Convert(converter, true, 3, AvaloniaProperty.UnsetValue);

        Assert.Multiple(() =>
        {
            Assert.That(integerResult, Is.EqualTo("int"));
            Assert.That(doubleResult, Is.EqualTo("double"));
            Assert.That(cachedIntegerResult, Is.EqualTo("int"));
        });
    }

    [Test]
    public void Convert_WithUnsetAwareFunction_PassesAvaloniaSingletonToUserCode()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "fallback",
            (Func<object?, object?, object?>)(static (value, alternative) =>
                ReferenceEquals(value, AvaloniaProperty.UnsetValue) ? alternative : value));

        var result = Evaluate(registry, "fallback(A, B)", AvaloniaProperty.UnsetValue, 42);

        Assert.That(result, Is.EqualTo(42));
    }

    [Test]
    public void Convert_WithUnsetAndInvalidFunctionArgument_ReportsInvalidArgument()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction("sum", (Func<double, double, double>)(static (left, right) => left + right));

        var waiting = Evaluate(registry, "sum(A, B)", AvaloniaProperty.UnsetValue, 2d);
        var invalid = Evaluate(registry, "sum(A, B)", AvaloniaProperty.UnsetValue, "invalid");

        Assert.Multiple(() =>
        {
            Assert.That(waiting, Is.SameAs(AvaloniaProperty.UnsetValue));
            Assert.That(invalid, Is.TypeOf<BindingNotification>());
            Assert.That(((BindingNotification)invalid!).Error, Is.TypeOf<ExpressionBindingException>());
        });
    }

    [Test]
    public void Convert_WithUnsetLiteral_PassesAvaloniaSingletonToUserCode()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction(
            "isUnset",
            (Func<object?, bool>)(static value => ReferenceEquals(value, AvaloniaProperty.UnsetValue)));

        var result = Evaluate(registry, "isUnset(unset)");

        Assert.That(result, Is.EqualTo(true));
    }

    [Test]
    public void Convert_WithUnsetEquality_UsesUnsetIdentity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Evaluate("unset == unset"), Is.EqualTo(true));
            Assert.That(Evaluate("A != unset", 1), Is.EqualTo(true));
            Assert.That(Evaluate("A == unset", AvaloniaProperty.UnsetValue), Is.EqualTo(true));
        });
    }

    [Test]
    public void Convert_WithBooleanComparisonAndBitwiseOperators_UsesCSharpPrecedence()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Evaluate("A > B && B != 0", 8, 2), Is.EqualTo(true));
            Assert.That(Evaluate("A << 2 | B", 3, 1), Is.EqualTo(13));
            Assert.That(Evaluate("~A", 3), Is.EqualTo(~3));
            Assert.That(Evaluate("bool(A) ? B : C", 0, "yes", "no"), Is.EqualTo("no"));
        });
    }

    [Test]
    public void Convert_WithShortCircuit_DoesNotInvokeRightOperand()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction("fail", (Func<bool>)(static () => throw new InvalidOperationException("Not reached.")));

        Assert.Multiple(() =>
        {
            Assert.That(Evaluate(registry, "false && fail()"), Is.EqualTo(false));
            Assert.That(Evaluate(registry, "true || fail()"), Is.EqualTo(true));
        });
    }

    [Test]
    public void Convert_WithImplicitBooleanConversion_UsesSameRuleForFunctionsAndControlFlow()
    {
        var registry = new ExpressionRegistry();
        registry.RegisterFunction("describeBool", (Func<bool, string>)(static value => value ? "true" : "false"));
        var converter = CreateConverter("A ? B : C", registry);

        var directFunction = Evaluate(registry, "describeBool(A)", new TruthValue(true));
        var builtInBoolean = Convert(converter, true, "yes", "no");
        var customBoolean = Convert(converter, new TruthValue(false), "yes", "no");
        var invalid = Convert(converter, 1, "yes", "no");
        var cachedBuiltInBoolean = Convert(converter, false, "yes", "no");

        Assert.Multiple(() =>
        {
            Assert.That(directFunction, Is.EqualTo("true"));
            Assert.That(builtInBoolean, Is.EqualTo("yes"));
            Assert.That(customBoolean, Is.EqualTo("no"));
            Assert.That(invalid, Is.TypeOf<BindingNotification>());
            Assert.That(cachedBuiltInBoolean, Is.EqualTo("no"));
        });
    }

    [Test]
    public void Convert_WhenConditionalConditionIsUnset_PropagatesUnset()
    {
        var result = Evaluate("A ? B : C", AvaloniaProperty.UnsetValue, 1, 2);

        Assert.That(result, Is.SameAs(AvaloniaProperty.UnsetValue));
    }

    [Test]
    public void Convert_WithSwitchExpression_MatchesPatternsInSourceOrder()
    {
        var converter = CreateConverter("A switch { 1 or 2 => B, > 10 when C => D, _ => E }");

        var combinedPattern = Convert(converter, 2, "combined", false, "guarded", "fallback");
        var guardedPattern = Convert(converter, 12, "combined", true, "guarded", "fallback");
        var fallbackPattern = Convert(converter, 12, "combined", false, "guarded", "fallback");

        Assert.Multiple(() =>
        {
            Assert.That(combinedPattern, Is.EqualTo("combined"));
            Assert.That(guardedPattern, Is.EqualTo("guarded"));
            Assert.That(fallbackPattern, Is.EqualTo("fallback"));
        });
    }

    [Test]
    public void Convert_WhenSwitchGuardIsUnset_PropagatesUnsetInsteadOfSelectingLaterArm()
    {
        var result = Evaluate(
            "A switch { 1 when B => 'guarded', _ => 'fallback' }",
            1,
            AvaloniaProperty.UnsetValue);

        Assert.That(result, Is.SameAs(AvaloniaProperty.UnsetValue));
    }

    [Test]
    public void Convert_WithSwitchExpression_AllowsDifferentArmResultTypes()
    {
        var converter = CreateConverter("A switch { true => B, false => C }");

        var integerResult = Convert(converter, true, 4, AvaloniaProperty.UnsetValue);
        var stringResult = Convert(converter, false, AvaloniaProperty.UnsetValue, "four");

        Assert.Multiple(() =>
        {
            Assert.That(integerResult, Is.EqualTo(4));
            Assert.That(stringResult, Is.EqualTo("four"));
        });
    }

    [Test]
    public void Convert_WithSwitchUnsetPattern_MatchesUnsetIdentity()
    {
        var result = Evaluate("A switch { unset => 1, _ => 2 }", AvaloniaProperty.UnsetValue);

        Assert.That(result, Is.EqualTo(1));
    }

    [Test]
    public void Convert_WithCoveredSwitchArm_UsesFirstMatchWithoutCoverageAnalysis()
    {
        var result = Evaluate("A switch { _ => 1, 2 => 2 }", 2);

        Assert.That(result, Is.EqualTo(1));
    }

    [Test]
    public void Convert_WhenSwitchHasNoMatchingArm_ReturnsBindingError()
    {
        var result = Evaluate("A switch { 1 => 1 }", 2);

        Assert.That(result, Is.TypeOf<BindingNotification>());
        Assert.That(((BindingNotification)result!).Error, Is.TypeOf<ExpressionBindingException>());
    }

    [Test]
    public void Convert_WithStringLiterals_SupportsBothQuoteStylesAndConcatenation()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Evaluate("'left' + A", 4), Is.EqualTo("left4"));
            Assert.That(Evaluate("A + \"right\"", "left"), Is.EqualTo("leftright"));
            Assert.That(Evaluate("'it\\'s'"), Is.EqualTo("it's"));
        });
    }

    [Test]
    public void Convert_WithStringSwitchPattern_MatchesByValue()
    {
        var result = Evaluate("A switch { 'compact' => 1, \"full\" => 2, _ => 3 }", "full");

        Assert.That(result, Is.EqualTo(2));
    }

    [Test]
    public void Convert_WithUnsupportedOperands_ReturnsBindingError()
    {
        var result = Evaluate("A + 1", new object());

        Assert.That(result, Is.TypeOf<BindingNotification>());
        Assert.That(((BindingNotification)result!).Error, Is.TypeOf<ExpressionBindingException>());
    }

    [Test]
    public void Parse_WithTrailingInput_ThrowsExpressionBindingException()
    {
        Assert.That(
            () => ExpressionParser.Parse("A + 1 unexpected"),
            Throws.TypeOf<ExpressionBindingException>());
    }

    private static object? Evaluate(string expression, params object?[] values) =>
        Evaluate(new ExpressionRegistry(), expression, values);

    private static object? Evaluate(ExpressionRegistry registry, string expression, params object?[] values) =>
        Convert(CreateConverter(expression, registry), values);

    private static ExpressionConverter CreateConverter(
        string expression,
        ExpressionRegistry? registry = null) =>
        new(expression, ExpressionParser.Parse(expression), registry ?? new ExpressionRegistry());

    private static object? Convert(ExpressionConverter converter, params object?[] values) =>
        converter.Convert(values, typeof(object), null, CultureInfo.InvariantCulture);

    private sealed record TestThickness(double Left, double Top, double Right, double Bottom);

    private delegate double SumDelegate(params double[] values);

    private readonly record struct Scalable(double Value)
    {
        public static Scalable operator *(Scalable value, double scale) => new(value.Value * scale);
    }

    private readonly record struct OperatorProbe(string SelectedOverload)
    {
        public static OperatorProbe operator +(OperatorProbe value, long amount) => new("long");

        public static OperatorProbe operator +(OperatorProbe value, double amount) => new("double");
    }

    private readonly record struct TruthValue(bool Value)
    {
        public static implicit operator bool(TruthValue value) => value.Value;
    }

    private static class GenericFunctions
    {
        public static T Min<T>(params T[] values)
            where T : INumber<T>
        {
            var result = values[0];
            foreach (var value in values.AsSpan(1))
            {
                result = T.Min(result, value);
            }

            return result;
        }

        public static T First<T>(IReadOnlyList<T> values) => values[0];
    }

    private static class OverloadFunctions
    {
        public static string Select(double first, double second) => "fixed";

        public static string Select(params double[] values) => "params";

        public static double SumAfter(double offset, params double[] values) => offset + values.Sum();

        public static int Count(params double[] values) => values.Length;

        public static string Describe(int value) => "int";

        public static string Describe<T>(T value) => "generic";
    }

    private static class OtherGenericFunctions
    {
        public static T Min<T>(params T[] values)
            where T : INumber<T> => values[0];
    }
}

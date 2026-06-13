using System.Text.RegularExpressions;
using Everywhere.StrategyEngine.ConditionExpression.PathBinding;
using Everywhere.StrategyEngine.ConditionExpression.Syntax;

namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Binds canonical Condition DSL syntax to a typed, executable bound tree.
/// </summary>
/// <remarks>
/// This is the only phase that maps author-written operator strings to concrete bound operator classes. After
/// binding, evaluation, analysis, and explain operate on polymorphic nodes and operators rather than string
/// switches.
/// </remarks>
internal sealed class ConditionExpressionBinder
{
    private readonly IReadOnlyDictionary<string, IStrategyPathRootProvider> _roots;
    private readonly IStrategyPathAccessorProvider _accessors;
    private readonly IStrategyConditionOperatorBinder _operators;

    public ConditionExpressionBinder(
        IEnumerable<IStrategyPathRootProvider> roots,
        IStrategyPathAccessorProvider accessors,
        IStrategyConditionOperatorBinder? operators = null)
    {
        _roots = roots.ToDictionary(root => root.RootName, StringComparer.Ordinal);
        _accessors = accessors;
        _operators = operators ?? new ConditionOperatorBinder();
    }

    /// <summary>
    /// Public roots that may appear at the start of a DSL path.
    /// </summary>
    /// <example>
    /// <c>attachments.files.any.extension.in</c> starts from the <c>attachments</c> root; collection predicates
    /// later introduce an internal <c>$item</c> scope rather than another public root.
    /// </example>
    public IReadOnlyDictionary<string, IStrategyPathRootProvider> Roots => _roots;

    /// <summary>
    /// Performs semantic binding for a normalized syntax tree.
    /// </summary>
    /// <remarks>
    /// The return value is null when this invocation produced an error diagnostic. Diagnostics from earlier
    /// compiler phases are left untouched so callers can aggregate the whole condition pipeline.
    /// </remarks>
    public ConditionNode? Bind(
        ConditionSyntaxNode syntax,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics)
    {
        var before = diagnostics.Count;
        var bound = BindCondition(syntax, null, source, diagnostics);
        return diagnostics.Skip(before).Any(diagnostic => diagnostic.Severity == StrategyDiagnosticSeverity.Error) ? null : bound;
    }

    private ConditionNode? BindCondition(
        ConditionSyntaxNode syntax,
        BindingScope? scope,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics)
    {
        return syntax switch
        {
            ConditionScalarSyntaxNode scalar => new ConditionScalarNode(
                scalar.Path,
                scalar.Value,
                scalar.Value is null ? ConditionValueType.Null : ConditionValueType.FromClrType(scalar.Value.GetType())),
            ConditionSequenceSyntaxNode sequence => new ConditionImplicitAllNode(
                sequence.Path,
                sequence.Items.Select(item => BindCondition(item, scope, source, diagnostics)).OfType<ConditionNode>().ToArray()),
            ConditionMappingSyntaxNode mapping => BindMapping(mapping, scope, source, diagnostics),
            _ => null
        };
    }

    private ConditionNode? BindMapping(
        ConditionMappingSyntaxNode mapping,
        BindingScope? scope,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics)
    {
        if (TryBindLogicalMapping(mapping, scope, source, diagnostics, out var logical))
        {
            return logical;
        }

        var children = new List<ConditionNode>();
        foreach (var (key, child) in mapping.Children)
        {
            var bound = scope is null ?
                BindRootPath(key, child, source, diagnostics, mapping.Path) :
                BindScopedPath(scope, key, child, source, diagnostics, mapping.Path);
            if (bound is not null)
            {
                children.Add(bound);
            }
        }

        return children.Count == 1 ? children[0] : new ConditionImplicitAllNode(mapping.Path, children);
    }

    private bool TryBindLogicalMapping(
        ConditionMappingSyntaxNode mapping,
        BindingScope? scope,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        out ConditionNode? node)
    {
        node = null;
        if (mapping.Children.Count != 1)
        {
            return false;
        }

        var (key, child) = mapping.Children.Single();
        if (key is "all" or "any" or "none")
        {
            if (child is not ConditionSequenceSyntaxNode sequence)
            {
                AddDiagnostic(diagnostics, source, "condition.type_mismatch", $"Logical operator '{key}' expects a sequence.", child.Path);
                node = key switch
                {
                    "all" => new ConditionAllNode(child.Path, []),
                    "any" => new ConditionAnyNode(child.Path, []),
                    _ => new ConditionNoneNode(child.Path, [])
                };
                return true;
            }

            var boundChildren = sequence.Items
                .Select(item => BindCondition(item, scope, source, diagnostics))
                .OfType<ConditionNode>()
                .ToArray();
            node = key switch
            {
                "all" => new ConditionAllNode(mapping.Path, boundChildren),
                "any" => new ConditionAnyNode(mapping.Path, boundChildren),
                _ => new ConditionNoneNode(mapping.Path, boundChildren)
            };
            return true;
        }

        if (key == "not")
        {
            var inner = BindCondition(child, scope, source, diagnostics);
            node = inner is null ? null : new ConditionNotNode(mapping.Path, inner);
            return true;
        }

        return false;
    }

    private ConditionNode? BindRootPath(
        string rootName,
        ConditionSyntaxNode child,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        string parentPath)
    {
        if (!_roots.TryGetValue(rootName, out var root))
        {
            AddDiagnostic(
                diagnostics,
                source,
                "condition.unknown_root",
                $"Unknown condition root '{rootName}'.{FormatSuggestion(rootName, _roots.Keys)}",
                $"{parentPath}.{rootName}");
            return null;
        }

        return BindPathTail(
            new BindingScope(root.RootName, root.IsDeferred ? ConditionValueType.Unknown : ConditionValueType.FromClrType(root.ValueType), []),
            child,
            source,
            diagnostics);
    }

    private ConditionNode? BindScopedPath(
        BindingScope scope,
        string segment,
        ConditionSyntaxNode child,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        string parentPath)
    {
        var nextScope = BindSegment(scope, segment, source, diagnostics, $"{parentPath}.{segment}");
        return nextScope is null ? null : BindPathTail(nextScope, child, source, diagnostics);
    }

    private ConditionNode? BindPathTail(
        BindingScope scope,
        ConditionSyntaxNode child,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics)
    {
        if (child is not ConditionMappingSyntaxNode mapping)
        {
            AddDiagnostic(diagnostics, source, "condition.invalid_yaml_shape", "Condition path must contain an operator mapping.", child.Path);
            return null;
        }

        var operators = new List<ConditionOperator>();
        var pathChildren = new List<ConditionNode>();
        foreach (var (key, value) in mapping.Children)
        {
            if (_operators.IsOperator(key))
            {
                var op = _operators.BindOperator(key, scope, value, this, source, diagnostics);
                if (op is not null)
                {
                    operators.Add(op);
                }
            }
            else
            {
                var childScope = BindSegment(scope, key, source, diagnostics, value.Path);
                if (childScope is null)
                {
                    continue;
                }

                var bound = BindPathTail(childScope, value, source, diagnostics);
                if (bound is not null)
                {
                    pathChildren.Add(bound);
                }
            }
        }

        if (pathChildren.Count > 0)
        {
            var children = new List<ConditionNode>(pathChildren.Count + 1);
            if (operators.Count > 0)
            {
                children.Add(new ConditionPathNode(mapping.Path, scope.PublicPath, scope.ValueType, scope.Steps, operators));
            }

            children.AddRange(pathChildren);
            return children.Count == 1 ? children[0] : new ConditionImplicitAllNode(mapping.Path, children);
        }

        return new ConditionPathNode(mapping.Path, scope.PublicPath, scope.ValueType, scope.Steps, operators);
    }

    private BindingScope? BindSegment(
        BindingScope scope,
        string segment,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        string diagnosticPath)
    {
        var tokenization = ConditionPathTokenizer.Tokenize(segment, diagnosticPath, source, diagnostics);
        if (tokenization is null || tokenization.CanonicalSegments.Count != 1)
        {
            return null;
        }

        var property = tokenization.Tokens.First(token => token.Kind == ConditionPathTokenKind.Property).Name!;
        var currentType = scope.ValueType;
        if (currentType.Kind == ConditionValueKind.Unknown)
        {
            return scope.Append(property, ConditionValueType.Unknown, null, tokenization.Tokens.Skip(1).ToArray());
        }

        if (currentType.Kind != ConditionValueKind.Object)
        {
            AddDiagnostic(
                diagnostics,
                source,
                currentType.Kind == ConditionValueKind.Collection ? "condition.collection_required" : "condition.scalar_required",
                $"Path segment '{property}' cannot be selected from {currentType}.",
                diagnosticPath);
            return null;
        }

        if (!_accessors.TryGetAccessor(currentType.ClrType, property, out var accessor))
        {
            AddDiagnostic(
                diagnostics,
                source,
                "condition.segment_missing",
                $"Type '{currentType.ClrType.Name}' has no DSL property '{property}'.{FormatSuggestion(property, _accessors.GetKnownNames(currentType.ClrType))}",
                diagnosticPath);
            return null;
        }

        var valueType = ConditionValueType.FromClrType(accessor.ValueType);
        var indexTokens = tokenization.Tokens.Skip(1).ToArray();
        foreach (var token in indexTokens)
        {
            if (valueType.Kind != ConditionValueKind.Collection)
            {
                AddDiagnostic(
                    diagnostics,
                    source,
                    "condition.collection_required",
                    $"Index or range '{token.Text}' requires a collection.",
                    diagnosticPath);
                return null;
            }

            valueType = token.Kind == ConditionPathTokenKind.Range ? valueType : valueType.ElementType ?? ConditionValueType.Unknown;
        }

        return scope.Append(accessor.PublicName, valueType, accessor, indexTokens);
    }

    private static string FormatSuggestion(string name, IEnumerable<string> candidates)
    {
        var suggestion = candidates
            .Select(candidate => new { Candidate = candidate, Distance = Levenshtein(name, candidate) })
            .Where(candidate => candidate.Distance <= 3)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Candidate, StringComparer.Ordinal)
            .FirstOrDefault();
        return suggestion is null ? string.Empty : $" Did you mean '{suggestion.Candidate}'?";
    }

    private static int Levenshtein(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static void AddDiagnostic(List<StrategyDiagnostic> diagnostics, StrategySource source, string code, string message, string path) =>
        diagnostics.Add(
            new StrategyDiagnostic
            {
                Severity = code is "condition.constant_expression" or "condition.redundant_expression" ?
                    StrategyDiagnosticSeverity.Warning :
                    StrategyDiagnosticSeverity.Error,
                Code = code,
                MessageKey = new DirectResourceKey(message),
                Path = path,
                ProviderId = source.ProviderId
            });

    /// <summary>
    /// Static path scope used while binding nested path segments and collection predicates.
    /// </summary>
    /// <remarks>
    /// Public roots use paths like <c>attachments.files</c>. Predicate scopes use the synthetic <c>$item</c> path
    /// so <c>attachments.files.any.extension.in</c> binds <c>extension</c> relative to each file element.
    /// </remarks>
    internal sealed record BindingScope(
        string PublicPath,
        ConditionValueType ValueType,
        IReadOnlyList<BoundConditionPathStep> Steps
    )
    {
        /// <summary>
        /// Returns a new scope with one additional path step.
        /// </summary>
        public BindingScope Append(
            string publicName,
            ConditionValueType valueType,
            StrategyPathAccessor? accessor,
            IReadOnlyList<ConditionPathToken> indexOrRangeTokens)
        {
            var step = new BoundConditionPathStep(publicName, valueType, accessor, indexOrRangeTokens);
            return new BindingScope($"{PublicPath}.{publicName}", valueType, Steps.Concat([step]).ToArray());
        }
    }

    /// <summary>
    /// Default operator binder for the built-in Condition DSL operator set.
    /// </summary>
    /// <remarks>
    /// Runtime operators are concrete classes, but author syntax is still text. This binder is the narrow adapter
    /// that validates target/operand types and creates those concrete operator instances.
    /// </remarks>
    private sealed class ConditionOperatorBinder : IStrategyConditionOperatorBinder
    {
        private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
        {
            "equals",
            "in",
            "contains",
            "containsAny",
            "containsAll",
            "startsWith",
            "endsWith",
            "regex",
            "glob",
            "caseSensitive",
            "length",
            "min",
            "max",
            "count",
            "any",
            "all",
            "none",
            "not"
        };

        public bool IsOperator(string name) => Operators.Contains(name);

        public ConditionOperator? BindOperator(
            string op,
            BindingScope scope,
            ConditionSyntaxNode operand,
            ConditionExpressionBinder binder,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            return op switch
            {
                "equals" or "in" => BindScalarOperator(op, scope, operand, source, diagnostics),
                "contains" or "containsAny" or "containsAll" => BindContainsOperator(op, scope, operand, source, diagnostics),
                "startsWith" or "endsWith" or "regex" or "glob" => BindStringOperator(op, scope, operand, source, diagnostics),
                "caseSensitive" => BindCaseSensitive(scope, operand, source, diagnostics),
                "length" => BindLength(scope, operand, source, diagnostics),
                "min" or "max" => BindNumberBoundary(op, scope, operand, source, diagnostics),
                "count" => BindCount(scope, operand, source, diagnostics),
                "any" or "all" or "none" => BindCollectionPredicate(op, scope, operand, binder, source, diagnostics),
                "not" => BindNot(scope, operand, binder, source, diagnostics),
                _ => Unknown(op, operand, source, diagnostics)
            };
        }

        private static ConditionOperator? BindScalarOperator(
            string op,
            BindingScope scope,
            ConditionSyntaxNode operand,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            if (!scope.ValueType.IsScalar && scope.ValueType.Kind != ConditionValueKind.Unknown)
            {
                AddDiagnostic(diagnostics, source, "condition.scalar_required", $"Operator '{op}' requires a scalar value.", operand.Path);
                return null;
            }

            return Create(op, scope, operand);
        }

        private static ConditionOperator? BindContainsOperator(
            string op,
            BindingScope scope,
            ConditionSyntaxNode operand,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            if (scope.ValueType.Kind == ConditionValueKind.String && op == "contains")
            {
                return Create(op, scope, operand);
            }

            if (scope.ValueType.Kind == ConditionValueKind.Unknown)
            {
                return Create(op, scope, operand);
            }

            if (scope.ValueType.Kind != ConditionValueKind.Collection)
            {
                AddDiagnostic(diagnostics, source, "condition.collection_required", $"Operator '{op}' requires a collection value.", operand.Path);
                return null;
            }

            if (scope.ValueType.ElementType?.IsScalar is false)
            {
                AddDiagnostic(
                    diagnostics,
                    source,
                    "condition.type_mismatch",
                    $"Operator '{op}' only supports scalar collection elements.",
                    operand.Path);
                return null;
            }

            return Create(op, scope, operand);
        }

        private static ConditionOperator? BindStringOperator(
            string op,
            BindingScope scope,
            ConditionSyntaxNode operand,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            if (scope.ValueType.Kind != ConditionValueKind.String && scope.ValueType.Kind != ConditionValueKind.Unknown)
            {
                AddDiagnostic(diagnostics, source, "condition.type_mismatch", $"Operator '{op}' requires a string value.", operand.Path);
                return null;
            }

            if (op == "regex" && operand is ConditionScalarSyntaxNode { Value: string pattern })
            {
                try
                {
                    _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(50));
                }
                catch (ArgumentException ex)
                {
                    AddDiagnostic(diagnostics, source, "regex.invalid", $"Regex pattern is invalid: {ex.Message}", operand.Path);
                    return null;
                }
            }

            return Create(op, scope, operand);
        }

        private static ConditionOperator? BindCaseSensitive(
            BindingScope scope,
            ConditionSyntaxNode operand,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            if (operand is not ConditionScalarSyntaxNode { Value: bool })
            {
                AddDiagnostic(diagnostics, source, "condition.type_mismatch", "Operator 'caseSensitive' expects a bool operand.", operand.Path);
                return null;
            }

            return new CaseSensitiveConditionOperator(
                operand.Path,
                scope.ValueType,
                GetOperandType(operand),
                operand is ConditionScalarSyntaxNode { Value: true });
        }

        private static ConditionOperator? BindLength(
            BindingScope scope,
            ConditionSyntaxNode operand,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            if (scope.ValueType.Kind != ConditionValueKind.String && scope.ValueType.Kind != ConditionValueKind.Unknown)
            {
                AddDiagnostic(diagnostics, source, "condition.type_mismatch", "Operator 'length' requires a string value.", operand.Path);
                return null;
            }

            ValidateMinMaxMap("length", operand, source, diagnostics);
            return Create("length", scope, operand);
        }

        private static ConditionOperator? BindNumberBoundary(
            string op,
            BindingScope scope,
            ConditionSyntaxNode operand,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            if (scope.ValueType.Kind != ConditionValueKind.Number && scope.ValueType.Kind != ConditionValueKind.Unknown)
            {
                AddDiagnostic(diagnostics, source, "condition.type_mismatch", $"Operator '{op}' requires a number value.", operand.Path);
                return null;
            }

            return Create(op, scope, operand);
        }

        private static ConditionOperator? BindCount(
            BindingScope scope,
            ConditionSyntaxNode operand,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            if (scope.ValueType.Kind != ConditionValueKind.Collection && scope.ValueType.Kind != ConditionValueKind.Unknown)
            {
                AddDiagnostic(diagnostics, source, "condition.collection_required", "Operator 'count' requires a collection value.", operand.Path);
                return null;
            }

            ValidateMinMaxMap("count", operand, source, diagnostics);
            return Create("count", scope, operand);
        }

        private static ConditionOperator? BindCollectionPredicate(
            string op,
            BindingScope scope,
            ConditionSyntaxNode operand,
            ConditionExpressionBinder binder,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            if (scope.ValueType.Kind != ConditionValueKind.Collection && scope.ValueType.Kind != ConditionValueKind.Unknown)
            {
                AddDiagnostic(diagnostics, source, "condition.collection_required", $"Operator '{op}' requires a collection value.", operand.Path);
                return null;
            }

            if (operand is not ConditionMappingSyntaxNode mapping)
            {
                AddDiagnostic(
                    diagnostics,
                    source,
                    "condition.type_mismatch",
                    $"Collection operator '{op}' expects a predicate mapping.",
                    operand.Path);
                return null;
            }

            var elementType = scope.ValueType.ElementType ?? ConditionValueType.Unknown;
            var predicateScope = new BindingScope("$item", elementType, []);
            var predicate = binder.BindMapping(mapping, predicateScope, source, diagnostics);
            var predicates = predicate is null ? [] : new[] { predicate };
            return op switch
            {
                "any" => new AnyCollectionConditionOperator(operand.Path, scope.ValueType, elementType, predicates),
                "all" => new AllCollectionConditionOperator(operand.Path, scope.ValueType, elementType, predicates),
                _ => new NoneCollectionConditionOperator(operand.Path, scope.ValueType, elementType, predicates)
            };
        }

        private static NotConditionOperator BindNot(
            BindingScope scope,
            ConditionSyntaxNode operand,
            ConditionExpressionBinder binder,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            var predicate = binder.BindCondition(operand, scope, source, diagnostics);
            return new NotConditionOperator(
                operand.Path,
                scope.ValueType,
                ConditionValueType.Unknown,
                predicate is null ? [] : [predicate]);
        }

        private static ConditionOperator? Unknown(
            string op,
            ConditionSyntaxNode operand,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            AddDiagnostic(diagnostics, source, "condition.operator_unknown", $"Unknown operator '{op}'.", operand.Path);
            return null;
        }

        private static void ValidateMinMaxMap(
            string op,
            ConditionSyntaxNode operand,
            StrategySource source,
            List<StrategyDiagnostic> diagnostics)
        {
            if (operand is not ConditionMappingSyntaxNode mapping)
            {
                AddDiagnostic(diagnostics, source, "condition.type_mismatch", $"Operator '{op}' expects a mapping.", operand.Path);
                return;
            }

            foreach (var key in mapping.Children.Keys.AsValueEnumerable())
            {
                if (key is not ("min" or "max"))
                {
                    AddDiagnostic(
                        diagnostics,
                        source,
                        "condition.operator_unknown",
                        $"Unknown '{op}' operator field '{key}'.",
                        operand.Path + "." + key);
                }
            }
        }

        private static ConditionOperator Create(string op, BindingScope scope, ConditionSyntaxNode operand)
        {
            var operandType = GetOperandType(operand);
            var operandValue = GetOperandValue(operand);
            return op switch
            {
                "equals" => new EqualsConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "in" => new InConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "contains" => new ContainsConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "containsAny" => new ContainsAnyConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "containsAll" => new ContainsAllConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "startsWith" => new StartsWithConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "endsWith" => new EndsWithConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "regex" => new RegexConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "glob" => new GlobConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "length" => new LengthConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "min" => new MinConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "max" => new MaxConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                "count" => new CountConditionOperator(operand.Path, scope.ValueType, operandType, operandValue),
                _ => throw new InvalidOperationException($"Unknown bound operator '{op}'.")
            };
        }

        private static ConditionValueType GetOperandType(ConditionSyntaxNode operand) =>
            operand switch
            {
                ConditionScalarSyntaxNode scalar => scalar.Value is null ?
                    ConditionValueType.Null :
                    ConditionValueType.FromClrType(scalar.Value.GetType()),
                ConditionSequenceSyntaxNode => new ConditionValueType(
                    ConditionValueKind.Collection,
                    typeof(IReadOnlyList<object>),
                    ConditionValueType.Unknown),
                _ => ConditionValueType.FromClrType(typeof(object))
            };

        private static object? GetOperandValue(ConditionSyntaxNode operand) =>
            operand switch
            {
                ConditionScalarSyntaxNode scalar => scalar.Value,
                ConditionSequenceSyntaxNode sequence => sequence.Items.AsValueEnumerable().Select(GetOperandValue).ToList(),
                ConditionMappingSyntaxNode mapping => mapping.Children.AsValueEnumerable().ToDictionary(
                    child => child.Key,
                    child => GetOperandValue(child.Value),
                    StringComparer.Ordinal),
                _ => null
            };
    }
}
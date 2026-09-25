<div align="center">

# ExpressionBinding.Avalonia

**Runtime-bound, strongly executed expressions for Avalonia bindings**

<p>
  <img alt=".NET 8 and 10" src="https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4">
  <img alt="Avalonia 12" src="https://img.shields.io/badge/Avalonia-12-8B44AC">
  <img alt="Status: early preview" src="https://img.shields.io/badge/status-early%20preview-orange">
  <a href="https://github.com/Sylinko/ExpressionBinding.Avalonia/blob/main/LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

</div>

ExpressionBinding.Avalonia adds expression-backed `MultiBinding` values without requiring a generated expression for
each AXAML use site. A source-generated Parlot parser creates an immutable syntax tree, then the runtime binder selects
operators, conversions, and registered function overloads from the actual input types. A fixed LINQ expression controls
evaluation and DLR call sites cache the type-specific rules used by its dynamic operations.

## Quick start

```xml
<TextBlock.FontSize>
  <expression:ExpressionBinding Expression="min(3 * A, B) + 24">
    <DynamicResource ResourceKey="FontSize3Xl"/>
    <ReflectionBinding>
      <ReflectionBinding.Source>
        <system:Double>64</system:Double>
      </ReflectionBinding.Source>
    </ReflectionBinding>
  </expression:ExpressionBinding>
</TextBlock.FontSize>
```

Declare the namespace on the root AXAML element:

```xml
xmlns:expression="using:ExpressionBinding.Avalonia"
xmlns:system="clr-namespace:System;assembly=System.Runtime"
```

Arguments are positional: `A` is the first child, `B` is the second, through `Z`. A child may be any Avalonia
`BindingBase` or a constant value. Constants are wrapped in a one-way `CompiledBinding`, so they participate in the same
ordered `MultiBinding` input list.

## Register functions

Configure the default registry during application startup:

```csharp
ExpressionRegistry.Default.RegisterFunction("scale", static (double value, double factor) => value * factor);
ExpressionRegistry.Default.RegisterFunction("numericMin", typeof(NumericFunctions), nameof(NumericFunctions.Min));
```

The expression can then return a non-numeric value:

```xml
<expression:ExpressionBinding Expression="thickness(A * 2, 25, B, 25)">
  <ReflectionBinding Path="LeftInset"/>
  <ReflectionBinding Path="RightInset"/>
</expression:ExpressionBinding>
```

The delegate overload registers one closed signature and may capture state. The `Type` and method-name overload registers
the public static method group declared by that type, including non-generic and open generic overloads. Multiple
registrations with the same expression name form an overload set; registering the same signature twice is an error.
Delegate invocation is emitted into the cached LINQ expression and does not use `DynamicInvoke`.

Methods whose final parameter is a one-dimensional `params T[]` may be called in normal or expanded form. Open generic
methods use runtime argument types to infer type parameters before overload selection. Inference supports direct type
parameters, arrays, and constructed interfaces or base types such as `IReadOnlyList<T>`; it deliberately does not infer
from the result type.

Create and assign a separate `ExpressionRegistry` when a view or component needs an isolated function set. Every new
registry includes:

- `min` and `max`, including variadic calls, for `int`, `long`, `float`, `double`, and `decimal`;
- `clamp` and `abs` for the same numeric types;
- `round`, `floor`, `ceil`, and `truncate` for `float`, `double`, and `decimal`;
- `pow` and `log` for `float` and `double`, and `sign` for the standard numeric types;
- `bool` conversions for the standard numeric types;
- `thickness`, `cornerRadius`, `size`, `point`, and `vector` constructors for Avalonia value types.

## Current expression language

See the [Language Specification](docs/LanguageSpecification.md) for the complete EBNF-style grammar, lexical rules,
types, keywords, conversions, overload resolution, unset semantics, execution model, and known implementation deviations.

The current version supports:

- arguments `A` through `Z`;
- fractional and integral numeric literals, strings with single or double quotes, `true`, `false`, `null`, and `unset`;
- parentheses and unary `+`, `-`, `!`, and `~`;
- arithmetic, shifts, comparisons, equality, bitwise operators, and short-circuit `&&` / `||` with C# precedence;
- the conditional operator `condition ? whenTrue : whenFalse`;
- switch expressions with constant, relational, discard, and `or` patterns plus optional `when` guards;
- registered function calls, nesting, `params` expansion, generic inference, and runtime overload selection;
- standard implicit numeric conversions, implicit user conversions, and user-defined binary operators;
- branch and function results whose runtime types can change, including Avalonia value types such as `Thickness`.

Switch arms are tested in source order; the parser does not perform C# exhaustiveness or unreachable-arm analysis.
The language does not yet include member access, pattern variables, object construction syntax, or string interpolation.
Constructor-like operations should be registered as named functions. Overload binding and generic inference implement a
documented subset of C#; candidates that have no unique best member produce an ambiguity error instead of depending on
registration order.

## Execution model

```text
Expression text
      │
      ▼
Parlot source-generated parser ──► immutable syntax tree
                                      │
                                      ▼
                         fixed LINQ control-flow plan
                                      │
Binding values ───────────────────────┼──► DLR call sites
Function registry ────────────────────┘          │
                                                ▼
                                  cached type-specific rules
                                                │
                                                ▼
                                  expression result
```

Each markup extension parses and builds its outer evaluation plan once. Conditional and switch nodes remain ordinary
LINQ expression-tree control flow, so unselected branches are not evaluated. Functions, operators, and implicit
conversions use DLR call sites: a site rebinds when its runtime argument types change and otherwise executes its cached
rule. Binder instances are shared within a registry, allowing compatible sites to reuse second-level DLR rules. Function
rules also carry the registry version, so adding an overload invalidates rules created from an earlier version.

`BindingOperations.DoNothing` is handled by the converter before evaluation. `unset` is the exact
`AvaloniaProperty.UnsetValue` singleton. Ordinary operations propagate it;
conditional tests and switch `when` guards also propagate it without evaluating a later branch. Equality and an explicit
`unset` switch pattern observe its identity.

The [language specification](docs/LanguageSpecification.md) now requires uniform function-call propagation: a direct unset
argument prevents invocation, including for `object` parameters. There is no passthrough option; use a `switch` expression
to recover explicitly. This is a pending implementation change (UNSET-01): the current binder still permits compatible
user functions to receive the singleton. Conversion-function extensions are likewise specified targets, not all currently
available built-ins.

On runtimes that support dynamic code, LINQ expressions compile normally. When dynamic code is unavailable, including
Native AOT environments, the library requests the LINQ expression interpreter. Boolean conversion uses an explicitly
declared typed DLR call site so its closed delegate shape is visible to NativeAOT. The AOT smoke project publishes and
runs conditional, short-circuit, overload rebinding, registry invalidation, and `unset` scenarios. Open generic
registrations are not closed at runtime in that environment; register the required closed delegates instead. Built-in
functions already use closed delegates. The parser itself is generated into this assembly and does not introduce a
Parlot runtime dependency for package consumers.

## Build

The parser generator currently comes from Parlot's official preview feed, pinned in `Directory.Packages.props`. The
repository's `NuGet.config` contains that feed and NuGet.org. Building requires .NET SDK 10.0.400 or newer.

```shell
dotnet restore ExpressionBinding.Avalonia.slnx --configfile NuGet.config
dotnet build ExpressionBinding.Avalonia.slnx --no-restore
dotnet test ExpressionBinding.Avalonia.slnx --no-restore
```

## License

ExpressionBinding.Avalonia is distributed under the [MIT License](LICENSE). The generated parser contains code derived
from Parlot under the BSD 3-Clause License; see [Third-party notices](THIRD-PARTY-NOTICES.md).

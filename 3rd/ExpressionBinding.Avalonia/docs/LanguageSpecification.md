# Expression Binding Language Specification

Status: target specification with an implementation baseline, revised on 2026-09-20.

This document specifies the expression language hosted by `ExpressionBinding.Avalonia`. It is a small expression language, not a C# compiler, JavaScript engine, or general-purpose scripting environment. Its syntax is primarily C#-inspired; its operands and results are runtime CLR values. Dynamic dispatch does not imply JavaScript coercion.

The target for supported operators is C# semantics: predefined operators and operators declared on user types, including the applicable conversion and overload-resolution rules. Extension operators are out of scope. The current evaluator does not yet meet this target. Descriptions of current behavior below are an implementation baseline, not permission to preserve incompatible behavior.

Blockquotes distinguish three statuses: **Implementation gap** (an agreed target is not met), **Open decision** (design is not settled), and **Intentional difference** (a deliberate language boundary). Stable issue identifiers connect local notes to the implementation checklist in section 13. Unmarked implementation details do not override these notes. NUM-01 and TYPE-01 are settled contracts with implementation gaps: preserve CLR numeric values, type literals before evaluation, retain valid constant-expression information, and do not reconstruct external static type information.

This revision changes documentation only. Subsequent implementation should address the quoted gaps with conformance tests; open decisions must be resolved before their behavior is changed.

## Contents

- [1. Scope and evaluation model](#1-scope-and-evaluation-model)
- [2. Values and types](#2-values-and-types)
- [3. Lexical rules](#3-lexical-rules)
- [4. Grammar](#4-grammar)
- [5. Conversions](#5-conversions)
- [6. Operators](#6-operators)
- [7. Control flow and patterns](#7-control-flow-and-patterns)
- [8. User-defined operators and registered functions](#8-user-defined-operators-and-registered-functions)
- [9. Built-in functions](#9-built-in-functions)
- [10. Unset, errors, and Avalonia integration](#10-unset-errors-and-avalonia-integration)
- [11. Execution, caching, and AOT](#11-execution-caching-and-aot)
- [12. Examples and implementation checks](#12-examples-and-implementation-checks)
- [13. Known deviations and unresolved decisions](#13-known-deviations-and-unresolved-decisions)
- [14. Implementation references](#14-implementation-references)

## 1. Scope and evaluation model

An expression computes one value from an ordered list of binding inputs. `A` denotes input zero, `B` input one, and so on through `Z`. Argument identifiers are case-sensitive. There are no local declarations, assignments, statements, loops, or implicit property lookups.

The markup extension is not generic. Neither its result type nor all intermediate types must be fixed before evaluation. A conditional may return a number on one update and a `Thickness` on another. Operations and function calls bind using the operand-information contract in section 2: runtime values use their actual CLR types, while language constants retain their intrinsic types and constant identity.

The expression structure is fixed after parsing. Runtime type changes may require a new rule at an affected dynamic call site, not recompilation of the whole expression plan. A property's target type is not an inference context for the expression.

Unless a construct explicitly specifies short-circuit behavior, operands and function arguments are evaluated eagerly, from left to right. No constant-folding or caching contract permits applications to assume a registered function executes only once.

### 1.1 C# compatibility boundary

> **Intentional difference LANG-01 — Dynamic value flow, C# operators, explicit sentinels.** Values flow dynamically between operations. For the operand types and semantic information established by TYPE-01, supported operators follow C# semantics. Runtime branches do not require a common result type, and Avalonia unset follows its separately specified rules. This boundary is deliberate, not a catch-all exception for incomplete operator binding.

The detailed contracts have distinct owners:

- Section 2 (TYPE-01) defines runtime operands, constants, null, and information lost at external/result boundaries. LANG-01 does not introduce a second type-inference policy.
- Section 7 defines dynamic conditional/switch evaluation. No cross-branch result-type unification is required; downstream operations bind to the selected runtime result. Whole expressions independently qualifying as C# constants still follow section 2.4.
- Section 10 defines unset and the Avalonia adapter. Unset does not make null interchangeable with it, grant string/numeric coercions, or turn ordinary binding errors into successful unset results.
- Sections 6 and 8 define operator conformance. Runtime dispatch and heterogeneous branches do not justify non-C# operator applicability, conversion, overload selection, or execution for established CLR operand types.

Extension operators are independently excluded by EXT-01. The registered generic-function inference subset is independently described by FUNC-02. Other quoted open decisions remain unresolved on their own terms; LANG-01 neither settles them nor excuses their implementation gaps.

### 1.2 Binding supplies values; expressions compute results

> **Intentional difference PATH-01 — No Binding Path language.** Arguments delegate source navigation and change observation to Avalonia bindings. The expression evaluator consumes the resulting values and computes a result; it does not implement a second property-path parser, object-graph subscription system, or binding lifetime manager.

Property chains, attached-property paths, path-specific casts/null handling, and path stream-observation syntax belong in the child bindings supplied through `Arguments`. Expressions such as `((MyControl)A)?.(MyAttach.Property).Property^` are outside this language. Explicit conversion functions under CAST-01 do not authorize any of these path features. The existing binary `^` remains exclusive-or; it is not a postfix observation operator.

Ordinary registered functions may read application objects, but such reads do not create automatic dependencies. If a nested property must trigger recomputation, supply an appropriate binding for that property as an argument rather than expecting a function call to discover and subscribe to it. Expression execution does not make effectful functions pure or observable.

## 2. Values and types

The language distinguishes the following value categories. These names describe the value model; they are not type-name keywords or a requirement to allocate a custom value wrapper.

```text
Value = Number | Boolean | String | Null | Unset | HostValue
```

`Boolean` is CLR `bool`; `String` is CLR `string`; `Null` is null; `Unset` is the exact Avalonia singleton. `HostValue` covers other application/CLR values. `Number` denotes a family of CLR numeric types, not a JavaScript-like unified runtime type. Binding inputs and registered-function results retain their CLR types and precision; the language does not automatically normalize them to `double` or `decimal`.

> **Implementation gap NUM-01 — Typed numeric values.** The representation decision is settled: preserve external CLR numeric types and determine each numeric literal's intrinsic type before execution, following C# spelling, suffix, and range rules. Replace the current decimal-backed literal treatment throughout parsing, binding, and cache metadata. C# promotion and conversions act on established types; they must not reinterpret a literal merely to make an overload applicable.

### 2.1 Value categories and available type information

| Category | Representation | Information available to binding |
| --- | --- | --- |
| Number | CLR numeric value; no global normalization | Runtime type, or intrinsic type and value for a language constant |
| Boolean | CLR `bool`; not a numeric subtype | Runtime value, or Boolean constant |
| String | CLR `string`; both quote forms produce it | Runtime value, or string constant |
| Null | Null, with no recoverable source CLR type | Null identity; source syntax can independently identify a null literal |
| Unset | The exact `AvaloniaProperty.UnsetValue` singleton | Sentinel identity, separate from CLR null and C# constants |
| Host value | Other CLR values | Actual runtime type and value |

Host values include Avalonia structures, application objects, arrays, and collections. This classification does not grant member access, indexing, enumeration, or arbitrary operators. Operations must be supported by the binder or exposed through functions.

The current built-in numeric promotion table covers `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `char`, `float`, `double`, and `decimal`. Enums, `Half`, native-sized integers, and `BigInteger` are not handled by that table; user operators or functions may provide operations on them. Full operator conformance remains OP-01/OP-02 rather than a consequence of this value classification.

### 2.2 Numeric-literal typing — target

A numeric literal has an intrinsic CLR type and a constant value before expression evaluation. Fractional spelling is significant even when the mathematical value is integral. Numeric type suffixes are case-insensitive.

| Form | Intrinsic type |
| --- | --- |
| Unsuffixed integer, such as `2` | First representable type in `int`, `uint`, `long`, `ulong` |
| Integer with `u` | First representable type in `uint`, `ulong` |
| Integer with `l` | First representable type in `long`, `ulong` |
| Integer with `ul` or `lu` | `ulong` |
| Unsuffixed real, such as `2.0`, `.5`, or `2e0` | `double` |
| Real with `d`, such as `2d` or `2.0d` | `double` |
| Real with `f`, such as `3f` or `3.0f` | `float` |
| Real with `m`, such as `2m` or `2.0m` | `decimal` |

Thus `2`, `2.0`, and `2f` have different intrinsic types even though they denote equal mathematical values. If `A` is `int`, `A / 2` uses integer division and `A / 2.0` uses double division after the applicable numeric conversion. No layout-specific coercion overrides this distinction.

Parse numeric text directly into the selected representation, with invariant culture and C# range/rounding requirements; do not first convert every literal to decimal. In particular, a valid double such as `1e100d` must not fail merely because it exceeds decimal's range. Invalid suffixes and out-of-range literals must fail before evaluation rather than being retyped to another numeric category.

Leading `+` and `-` belong to unary syntax, not the numeric token (an exponent may contain its own sign). Preserve enough syntax information to implement C#'s special unary-minus rules for minimum `int` and `long` constants. Constant patterns containing signed numbers must be updated consistently when the current signed scanner is replaced.

> **Implementation gap NUM-01 — Literal typing.** The current parser erases the distinction between `2` and `2.0` and does not accept type suffixes. Implement the table above and retain constant identity for C# constant-expression conversions. A typed literal is not an untyped value inferred backward from the receiving function parameter.

### 2.3 Runtime values and information boundaries — target

A binding input is a runtime value, not a source-language variable declaration. A non-null input supplies its actual CLR type. An `object` property containing a double therefore supplies a double operand; a base/interface-typed property containing a derived object supplies the derived runtime type. The evaluator must not reflect through binding paths, converters, resource origins, or source properties to reconstruct their declarations.

Boxing a non-null `Nullable<T>` produces boxed `T`; an empty nullable produces null. The evaluator accepts this boundary. A previous non-null value must not establish a hidden nullable type for a later null. Null may participate in a conversion, overload, or lifted operator when current candidate rules permit it, but not because the evaluator guessed its lost source type.

A selected function/operator signature still determines how to construct that invocation, including parameter conversions and its declared result representation. After an ordinary runtime result crosses into the next dynamic operation, binding uses its actual value/type, not a hidden copy of the previous signature's declared return type. For example, a function declared to return `object` but returning an int supplies an int to the next site. A returned nullable follows the same boxing boundary as a binding input.

A runtime conditional or switch supplies the selected branch's result as an ordinary runtime value. It does not forward that branch's constant identity or source declaration to the next site. The exception is an entire expression independently classified as a C# constant expression under section 2.4; selecting a constant branch at runtime is not sufficient.

| Origin | Operand information supplied to the next operation |
| --- | --- |
| Ordinary binding input, including a constant AXAML child | Actual CLR type and value; never a language constant solely because the source is stable |
| Null input or null runtime result | Null identity, no inferred source type or numeric constant privileges |
| Numeric literal | Intrinsic CLR type, constant value, and constant identity |
| Other supported CLR literal | Its literal classification and constant information where C# permits it |
| Valid compound constant expression | Semantically determined type, value, and constant identity |
| Ordinary function, user-operator, or dynamic-operation result | Actual result type/value; no automatic constant identity |
| Runtime conditional/switch result | Current selected result, using the same runtime-value boundary |
| Unset from any source | The Avalonia sentinel identity and the explicit unset rules |

These TYPE-01 information boundaries apply identically under JIT, interpretation, and AOT. Section 1.1 identifies their role in the language's compatibility boundary without adding source-type recovery or another inference policy.

### 2.4 Constants are semantic information — target

Constant identity belongs to an expression's semantics, not just to the shape of a numeric AST node. Classify supported expressions according to the C# constant-expression rules and retain their resulting type and value. Numeric spelling and intrinsic typing come from NUM-01; the applicable conversions come from the C# conversion rules.

Parentheses preserve constant identity. Eligible built-in operations on constants can produce another constant, so `2`, `(2)`, and `1 + 1` can all supply the int constant value two. This does not mean every input-independent expression is a C# constant expression.

Binding arguments are not constants, even when their values never change. Registered functions, including built-ins, are ordinary calls rather than constant-expression intrinsics. Do not execute a function, user-defined operator, or user-defined conversion speculatively to discover whether its result is constant. Unset is a language sentinel, not an additional C# numeric/null constant. A dynamic branch's selected constant is not promoted into whole-expression constant identity.

Consider a registered `setLevel(byte)` with no competing overloads:

| Expression | Classification relevant to the byte parameter |
| --- | --- |
| `setLevel(2)` | Int constant; use the permitted constant conversion |
| `setLevel((2))` | Same constant identity |
| `setLevel(1 + 1)` | Compound int constant; same conversion eligibility |
| `setLevel(A)`, with runtime int `A = 2` | Ordinary int; no implicit narrowing merely because the value fits |
| `setLevel(getTwo())`, returning int | Ordinary int result; no constant privileges |
| `setLevel(A ? 2 : 3)`, with runtime Boolean A | Runtime int result; no selected-branch constant privileges |
| `setLevel(2d)` | Double constant, not an int constant reinterpreted for byte |

Constant classification must be established independently of optimizations. An implementation may fold a valid constant subtree, but omitting that optimization must not change overload applicability. Conversely, specializing a cached runtime rule must not turn its argument into a language constant.

> **Implementation gap TYPE-01 — Semantic constants.** The current plan preserves decimal numeric-literal metadata and folded unary signs but loses constant identity through ordinary binary nodes. Implement constant-expression classification, types, and values before dynamic rule construction. Coordinate constant conversions with CONV-01; do not patch only literal call sites or infer constancy from observed values.

### 2.5 Binding metadata and conformance context — target

The binding layer needs to distinguish ordinary runtime operands, language constants, null, and unset. For a typed constant, retain the intrinsic type and value used by applicability checks; for a runtime operand, obtain the current type without assigning constant privileges. Keep source locations needed for literal diagnostics and special unary-minus rules. These requirements do not prescribe a new public wrapper, mandatory AST inheritance hierarchy, or generic type for the entire expression.

Compare operator behavior with C# using equivalent information: ordinary inputs are runtime operands of the same types, constants retain their expression classification, and null has no fabricated declared source type. Do not validate against a statically typed base-class or nullable variable when that type is not available under this contract. The same semantic metadata must be used by operator binding, registered-function applicability, and generic inference, even where their selection policies differ.

> **Implementation gap TYPE-01 — End-to-end information contract.** Carry constant classification through semantic analysis and plans while preserving the established runtime result boundaries. Audit input handling, null transitions, function/operator results, dynamic branches, conversions, inference, and DLR cache keys/restrictions together. Existing runtime-value behavior should be retained where it already satisfies the contract; this gap is not a request to wrap every result in a typed object.

### 2.6 Implementation baseline to replace

The current parser stores numeric literals as decimal. Integral-valued literals materialize as int if representable, then long, otherwise double; non-integral values materialize as double. Consequently `1.0` currently becomes int. Immediate operator/function binding can reinterpret the decimal literal for a target parameter. Unary sign folding retains that special metadata, but binary operations and control-flow results generally discard it.

Replace literal retyping with NUM-01's intrinsic types and TYPE-01's semantic constant classification. Preserve ordinary runtime result boundaries: a call does not become a constant because its arguments are constant, and a runtime branch does not inherit its selected arm's constant privileges.

## 3. Lexical rules

### 3.1 Source text and whitespace

The parser receives the .NET string in the `Expression` property. For AXAML, XML entity decoding happens first. For example, `A &lt; B &amp;&amp; bool(C)` becomes `A < B && bool(C)` before expression parsing.

Parlot term parsers skip leading whitespace, including line breaks, using the whitespace classification in the pinned Parlot version. Whitespace may separate tokens but not split an identifier, numeric token, or multi-character operator. Comments are not supported.

> **Implementation gap LEX-01 — Trailing whitespace.** The end-of-input parser currently rejects whitespace after the final token. Leading and trailing insignificant whitespace should be treated consistently; this rejection is not an intended language rule.

### 3.2 Identifiers and names

```ebnf
identifier = identifierStart identifierPart*
identifierStart = ASCII_LETTER | "_" | "$"
identifierPart = identifierStart | DIGIT
ASCII_LETTER = "A" ... "Z" | "a" ... "z"
DIGIT = "0" ... "9"
```

Identifiers use the ASCII rules above. Unicode letters, identifier Unicode escapes, C# verbatim identifiers such as `@switch`, dotted qualification, and generic argument lists are not supported. These exclusions do not prohibit Unicode content in string literals.

Names use ordinal, case-sensitive comparison, independently of culture. `min`, `Min`, and `MIN` are distinct function names; only the registered spelling identifies an overload set. Keyword recognition also uses exact spelling. Numeric suffix case rules are separate and remain as specified in section 2.2.

A syntactically valid bare identifier is not necessarily a valid value expression. Currently only `A` through `Z` and the literal words `true`, `false`, `null`, and `unset` have value meanings. Thus `True` may be registered as a function name but is not a Boolean literal or a supported bare value.

Parameter references and named function calls are distinguished by syntax: `A` reads input zero, while `A(B)` calls the registered function named `A`. Registering that function does not shadow or change the input reference.

> **Open decision ARG-01 — Input naming beyond A–Z.** Discuss named inputs and addressing more than 26 inputs separately from keyword classification. The current positional names remain in force until a replacement or extension is agreed. Supporting additional input names does not imply local declarations, assignment, statements, or Binding Path lookup.

### 3.3 Keywords, callable names, and registration

#### Fixed name classification

Name classification belongs to the language and does not depend on the selected registry or which functions have been registered. Registration can change call resolution, never source parsing.

| Classification | Exact spellings | Contract |
| --- | --- | --- |
| Reserved literal words | `true`, `false`, `null`, `unset` | Denote their literal values; cannot be registered or used as function names |
| Reserved structural keyword | `switch` | Introduces switch arms; cannot be registered or used as a function name |
| Contextual keywords | `when`, `or` | Special in their grammar positions; permitted as function names in call position |
| Contextual special name | `_` | Discard in pattern position; not permitted as a function name in the current scope |
| Ordinary names | Every other identifier, including `bool`, `int`, `double`, and `string` | Permitted as function names; no internal-only registration privilege |

The language does not inherit C#'s entire reserved-keyword list. Names such as `new`, `if`, `else`, `match`, and `var` are ordinary names, not implemented constructs or implicitly reserved future syntax. Introducing conflicting syntax later requires an explicit compatibility decision.

Keywords are recognized as whole identifiers, not prefixes: `switchValue` and `trueValue` are ordinary names. Contextual recognition is determined by grammar position, not registration availability. For example:

```csharp
when(A)
A switch { _ when when(B) => 1, _ => 0 }
```

Both `when(...)` occurrences are calls. The `when` following the discard pattern introduces a guard. Likewise, `or(A)` is a call in expression position, while `or` between patterns combines alternatives.

#### Function registration and diagnostics

A callable name must satisfy section 3.2's identifier grammar and must not be a reserved word or the exact name `_`. Apply the same name validation to every registration form and to built-in registrations. Invalid names are rejected during registration. Calls such as `true()`, `switch(A)`, and `_(A)` are invalid syntax regardless of registry contents, including in unselected branches.

An ordinary or contextual name followed by call syntax is parsed as a call even when no matching function exists. Resolution occurs when that call is reached. Registering a function must not be necessary to parse its call. Bare identifiers continue to follow the value restrictions in section 3.2.

Expression aliases are independent of CLR method names supplied through reflection registration. A method with a conflicting name can be exposed through a different valid expression alias; `@` escaping is not introduced for this purpose. CLR method discovery retains exact-name matching.

`bool` denotes an ordinary overload set, not a compiler-intrinsic conversion. Both the library and users may register additional non-conflicting signatures. Existing signatures cannot be replaced, and normal applicability/ambiguity rules apply. For example, a user may register `bool(ExternalStatus)` for an external type that cannot be modified, enabling `bool(A) ? B : C`. This does not make `A ? B : C` valid through an implicit conversion, alter `operator !`, or introduce cast syntax.

> **Implementation gap LEX-02 — Enforce the agreed name contract.** The current function registry uses case-insensitive lookup, and the parser recognizes calls before literal identifiers, allowing `true()` as a call. Align Parlot-generated parsing, registration validation, lookup, duplicate detection, and function/binder cache identity with sections 3.2–3.3. Preserve contextual calls, user-extensible conversion names, ASCII identifier rules, and the absence of verbatim identifiers. The case change is intentional: do not retain implicit case-insensitive aliases. AST parsing must remain independent of registry state.

### 3.4 Numeric tokens

> **Implementation gap NUM-01 — Numeric grammar.** The grammar below is the old scanner baseline. Replace it with Parlot-generated rules for C# decimal integer and real forms, including the type suffixes in section 2.2. A suffix must be adjacent to its number (`2d`, not `2 d`). Distinguish integer spelling from fractional/exponent spelling, validate the complete token, and do not accept a malformed literal as a shorter valid prefix. The current acceptance of `1.` is not a target C# real-literal rule. Hexadecimal/binary forms and digit separators are not added by this decision; their support remains outside this initial decimal-literal migration.

The following describes the current Parlot decimal scanner. `digits` contains one or more ASCII decimal digits; no whitespace occurs inside these productions.

```ebnf
number = sign? unsignedNumber
unsignedNumber = digits "." digits exponent?
               | digits "."
               | digits exponent?
               | "." digits exponent?
digits = DIGIT DIGIT*
sign = "+" | "-"
exponent = ("e" | "E") sign? digits
```

Examples: `12`, `12.5`, `.5`, `1.`, `1e2`, `1e-2`. The current scanner does not accept `1.e2`. The sign may also be parsed as a unary operator in expression position. In a constant pattern, a sign belongs to the numeric token: `-1` is accepted but `- 1` is not.

Numbers are initially parsed as invariant-culture `System.Decimal` values. Their range and precision are bounded by that representation, including its parsing/rounding behavior. Literal spelling does not select a CLR type: `1`, `1.0`, and `1e0` represent the same numeric literal value.

Hexadecimal/binary literals, digit separators, type suffixes, and named `NaN`/infinity literals are unsupported. Host-provided floating-point values may nevertheless contain NaN or infinity.

### 3.5 Strings

Single and double quotes both introduce strings. The closing delimiter must match the opening delimiter. Single quotes do not introduce a CLR `char` literal.

```ebnf
string = "'" singleQuotedCharacter* "'"
       | '"' doubleQuotedCharacter* '"'
```

A quoted character is an unescaped character other than the active delimiter or backslash, or a supported escape sequence. The scanner supports `\'`, `\"`, `\\`, `\0`, `\a`, `\b`, `\f`, `\n`, `\r`, `\t`, `\v`, `\uHHHH`, and `\xH` with one through four hexadecimal digits. Unknown escape sequences are rejected. Physical line breaks within quoted strings are currently accepted.

There are no verbatim, raw, or interpolated strings. A `$` identifier prefix does not enable `$"..."` interpolation. XML escaping and expression-string escaping are distinct layers.

## 4. Grammar

### 4.1 Notation

The grammar uses EBNF-style notation: `=` defines a production; quoted text is a terminal; `|` means alternative; juxtaposition means sequence; `*` means zero or more; `?` means optional; parentheses group grammar elements. `EOF` means end of input. Operators inside quotes are literal tokens, not EBNF operators. A Markdown `ebnf` fence is used for presentation; `makefile` is not the grammar's format.

Lexical terms in section 3 skip leading whitespace as described there. The grammar is accompanied by semantic rules: for example, an `identifier` must resolve to a supported argument or literal when used as a value.

### 4.2 Complete syntactic grammar

This grammar incorporates the agreed SYN-01 switch placement and LEX-02 callable-name rules. It is not a claim of current parser conformance: quoted implementation gaps identify required migrations, and the lexical productions remain subject to section 3's numeric-literal migration notes.

```ebnf
start = expression EOF

expression = logicalOr ("?" expression ":" expression)?

switchSuffix = "switch" "{" arms "}"
arms = arm ("," arm)*
arm = pattern ("when" expression)? "=>" expression
pattern = atomicPattern ("or" atomicPattern)*
atomicPattern = "_"
              | relationalOperator patternValue
              | patternValue
patternValue = number | string | "false" | "true" | "null" | "unset"

logicalOr = logicalAnd ("||" logicalAnd)*
logicalAnd = bitwiseOr ("&&" bitwiseOr)*
bitwiseOr = exclusiveOr ("|" exclusiveOr)*
exclusiveOr = bitwiseAnd ("^" bitwiseAnd)*
bitwiseAnd = equality ("&" equality)*
equality = relational (("==" | "!=") relational)*
relational = shift (relationalOperator shift)*
relationalOperator = "<=" | ">=" | "<" | ">"
shift = additive (("<<" | ">>") additive)*
additive = multiplicative (("+" | "-") multiplicative)*
multiplicative = switchExpression (("*" | "/" | "%") switchExpression)*
switchExpression = unary switchSuffix*
unary = ("+" | "-" | "!" | "~")* primary
primary = number | string | call | identifier | "(" expression ")"
call = callableName "(" (expression ("," expression)*)? ")"
callableName = identifier (* excluding reserved words and the exact name "_"; section 3.3 *)
```

There must be at least one switch arm. Neither function argument lists nor arm lists allow a trailing comma. Repeated switch suffixes compose from left to right: `A switch { ... } switch { ... }` means `(A switch { ... }) switch { ... }`. Arm results and guards remain full expressions. This decision does not add empty arm lists, trailing commas, or new pattern forms.

`or` combines patterns; `|` remains an expression-level bitwise operator. Patterns cannot contain arbitrary expressions, argument references, nested parenthesized patterns, type tests, variable declarations, or `and`/`not` patterns.

### 4.3 Precedence and associativity

From highest to lowest:

| Level | Constructs | Associativity |
| --- | --- | --- |
| 1 | Literals, arguments, calls, parentheses | Primary expressions |
| 2 | Unary `+ - ! ~` | Right to left |
| 3 | `switch { ... }` | Left to right |
| 4 | `* / %` | Left to right |
| 5 | `+ -` | Left to right |
| 6 | `<< >>` | Left to right |
| 7 | `< <= > >=` | Left to right |
| 8 | `== !=` | Left to right |
| 9 | `&` | Left to right |
| 10 | `^` | Left to right |
| 11 | `|` | Left to right |
| 12 | `&&` | Left to right |
| 13 | `||` | Left to right |
| 14 | `? :` | Right to left |

Switch follows C# precedence within the supported grammar: below unary operators and above multiplicative operators. Thus `A + B switch { _ => 2 }` means `A + (B switch { _ => 2 })`, while `-A switch { ... }` means `(-A) switch { ... }`. To match a complete lower-precedence calculation or conditional, write `(A + B) switch { ... }` or `(A ? B : C) switch { ... }`. These are AST grouping rules, not type-dependent decisions. See the [C# switch-expression grammar](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/expressions#1212-switch-expression).

> **Implementation gap SYN-01 — Switch precedence and composition.** The current parser attaches at most one switch suffix to an entire `logicalOr`, so it groups `A + B switch { _ => 2 }` as `(A + B) switch { _ => 2 }`. Move the suffix layer between unary and multiplicative parsing and fold repeated suffixes left to right, using Parlot's source-generated combinators. Verify generation with the pinned version; if unsupported, stop and discuss instead of introducing a handwritten parser. Until migration, use explicit parentheses to obtain the intended grouping. Preserve single evaluation of each governing expression, lazy arm selection, dynamic result types, and the absence of coverage analysis; this migration does not change CLR operator resolution or pattern semantics.

Syntactic acceptance does not guarantee type correctness. For example, `A < B < C` parses as `(A < B) < C`, not a chained mathematical comparison.

## 5. Conversions

Operator applicability and best-member selection must use the corresponding C# conversion rules applied to section 2's operand information. Constant conversions require a semantically classified constant, not merely a runtime value that fits the target. Null conversions must not use inferred source declarations or cache history. The evaluator currently implements its own conversion helper; sections 5.1–5.3 record that helper's baseline for ordinary values. It does not delegate general binding to the C# runtime binder, `TypeConverter`, `Convert.ChangeType`, or JavaScript-style coercion.

> **Implementation gap CONV-01 — Conversion resolution.** Exact `op_Implicit` lookup and numeric conversion scores are not a substitute for C# conversion resolution. Audit applicable user conversions, surrounding standard conversions, and ambiguity. A shared helper must not force the registered-function ranking policy onto C# operator binding.

> **Implementation gap NUM-01 — Constant conversions.** Current decimal-backed literal narrowing is broader than C# in some cases. Use the intrinsic type and constant value established under section 2.2, then apply the allowed C# conversions. Remove contextual retyping through decimal; do not let a receiving parameter change `2d` into an int literal. Broader conversion resolution remains CONV-01.

### 5.1 Supported implicit conversions

In order of consideration:

1. Contextual numeric-literal conversion.
2. Null to a reference type or nullable value type.
3. Exact runtime-type identity.
4. Assignability, including base/interface conversions and boxing to `object`.
5. Conversion to a nullable target through its underlying type.
6. Built-in implicit numeric widening.
7. A matching public static user-defined `op_Implicit` on the source or destination type.

Unset is intercepted by the evaluation rules in section 10, not converted to a function parameter type. The current helper's sentinel-assignability behavior is obsolete under UNSET-01.

User conversion lookup currently requires an exact source parameter type and exact return type. It is not the full C# user-conversion algorithm; it does not search arbitrary conversion chains. When multiple exact user conversions exist, current reflection selection does not provide full C# ambiguity analysis.

Explicit casts are not currently implemented; section 5.4 tracks their separate design. Numeric values do not implicitly convert to `bool`; call the ordinary registered `bool` function when that is desired. Registering additional overloads of that function does not create implicit conversions. Strings do not implicitly parse into numbers.

### 5.2 Numeric widening table

Identity conversions are omitted.

| Source | Implicit numeric destinations |
| --- | --- |
| `sbyte` | `short`, `int`, `long`, `float`, `double`, `decimal` |
| `byte` | `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal` |
| `short` | `int`, `long`, `float`, `double`, `decimal` |
| `ushort` | `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal` |
| `int` | `long`, `float`, `double`, `decimal` |
| `uint` | `long`, `ulong`, `float`, `double`, `decimal` |
| `long`, `ulong` | `float`, `double`, `decimal` |
| `char` | `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal` |
| `float` | `double` |
| `double`, `decimal` | None |

### 5.3 Literal conversions and overload ranking

The current decimal-literal helper can convert a representable value to integral numeric types or to `float`, `double`, or `decimal`, but not `char`. This is a baseline to replace, not the target constant-conversion matrix. Under NUM-01/TYPE-01, conversions operate on typed constants, including eligible compound expressions; a runtime int whose value fits byte does not receive constant-conversion privileges.

Current conversion scores, used by overload comparison, are:

| Conversion | Score |
| --- | --- |
| Runtime identity; literal's default type | 0 |
| Runtime assignability except to `object`; numeric widening; null conversion | 1 |
| User implicit conversion | 2 |
| Runtime assignability to `object` | 3 |
| Literal widening from its default type | 1 |
| Other representable literal numeric conversion | 2 |
| Literal fallback to an assignable type | 2, or 3 for `object` |

Wrapping a conversion in a nullable target adds one to the underlying conversion score. Scores are compared per argument, not summed across a call. Equal target types compare equally regardless of score. Section 8 describes tie-breaking.

### 5.4 Explicit conversion functions

> **Intentional difference CAST-01 — Functions instead of cast syntax.** The current language scope uses explicit function calls such as `double(A)` and `int(A)`, not C# `(T)expression` casts. It introduces no CLR type-name resolution, namespace imports, or AXAML type lookup. Reconsidering casts later requires a separate design decision.

Conversion functions are ordinary registered overload sets. Their bodies define the explicit conversion behavior; the implicit conversion helper only determines whether arguments fit their parameters. Numeric conversion functions follow the corresponding `Convert` operations, which are not interchangeable with C# casts. Section 9.2 defines the built-in contract.

Users can register functions for external types, including additional `bool` overloads, or encapsulate C# casts and statically selected calls inside delegates. Registration does not add implicit conversions or user-defined cast rules to the expression language.

A conversion function's result still crosses TYPE-01's runtime-value boundary. A function declared to return a base class but returning a derived instance does not force the next call site to use the declared base type. If static overload selection is required, encapsulate that selection inside the registered function; do not introduce hidden typed-value wrappers.

PATH-01 remains independent: conversion functions neither import Binding Path syntax nor create property-change subscriptions.

## 6. Operators

### 6.1 Target contract and semantic ownership

For supported unary and binary operator syntax, the target is C# semantics on the established operand types, including predefined operators and user-defined operators. The language specification determines applicability, conversions, result types, and evaluation behavior. LINQ Expression factories are execution mechanisms, not the semantic authority.

Predefined operators need not exist as reflected `op_XXX` methods on framework types. The binder must know their language-defined signatures and behavior. User-defined operators require the C# candidate-discovery and overload-resolution process described in section 8, not an unconditional reflected method call.

> **Implementation gap OP-01 — Operator resolution.** Replace the current sequence of special cases with specification-driven resolution. Applicable user-defined operators are considered before predefined operators; ambiguity must be reported rather than falling back. Audit unary and binary discovery, inheritance, conversions, better-member rules, result types, lifted forms, enums, delegates, and reference/string equality. This is a conformance checklist, not a claim that those cases already work.

Apply the compatibility boundary in section 1.1: TYPE-01 establishes operand information, and section 10 handles unset. Neither changes the C# operator-resolution target for ordinary CLR operands. Extension-operator lookup remains independently excluded under EXT-01.

The following subsections describe current operator execution where the migration is incomplete.

### 6.2 Numeric operations

Built-in arithmetic uses LINQ expression-tree operators on selected CLR numeric types. Small integral operands (`sbyte`, `byte`, `short`, `ushort`, `char`) are promoted to `int` before common-type selection.

The current common-type selection then prefers:

1. `decimal`, unless combined with `float` or `double`;
2. `double`;
3. `float`;
4. `ulong`, only when the other promoted operand is `uint` or `ulong`;
5. `long`;
6. `long` for the `uint`/`int` combination, or `uint` for two `uint` operands;
7. `int`.

Both operands must subsequently convert to the selected type. A numeric literal combined with a runtime decimal receives special decimal contextual treatment. The promotion procedure is not a complete reproduction of C#'s constant-sensitive promotion rules.

> **Implementation gap OP-02 — Numeric promotion.** Current promotion can reject runtime `ulong + byte`. For established CLR operand types, implement C# promotion rather than preserving this algorithm. Integrate the typed-literal implementation from NUM-01; its representation policy is no longer an open decision.

Integral division truncates toward zero. Ordinary integral arithmetic uses unchecked expression-tree operations; division and decimal arithmetic retain their CLR exception behavior. There is no `checked`/`unchecked` syntax. Floating-point operations retain IEEE/CLR behavior, including NaN and infinity.

### 6.3 Unary, bitwise, and shifts

The target distinguishes logical negation `!` from one's complement `~`. Each has its own predefined and user-defined operator resolution. A selected user-defined `operator !` need not return Boolean; it must not be replaced by unconditional conversion to Boolean followed by negation. Unary `+` and `-` likewise require their own C# applicability and promotion rules.

> **Implementation gap OP-03 — Unary binding.** Currently `!1` reaches `Expression.Not(int)` and produces `-2`. The target rejects this expression for an int operand while allowing applicable `op_LogicalNot` overloads. Do not fix it with an int-only rejection or a Boolean-only guard. Resolve the semantic operator first, then emit the selected built-in operation or method call.

`&`, `|`, and `^` support integral numeric operands and the separate Boolean/Boolean case. They are eager, not short-circuit operators.

`<<` and `>>` require an integral left operand, subject to promotion, and a right operand implicitly convertible to `int`. Shift-count masking follows the selected CLR operation. Unsigned right shift `>>>` is not supported.

### 6.4 Addition, comparisons, and user operators

> **Implementation gap OP-01 — Binary dispatch order and equality.** The current numeric/string fast paths can run before user-operator discovery, and same-type equality is not the full C# reference/delegate/enum rule set. The baseline below must be audited against the target resolution order; do not preserve it merely because Expression factories accept it.

If either non-unset operand of `+` is a string, addition uses `string.Concat(object, object)`. Null contributes an empty string; other values use their string representation. This does not establish a culture-aware formatting facility, and the converter's culture argument does not control it.

Numeric comparison uses numeric binding. User-defined binary operators currently participate through reflected CLR methods, but discovery and selection are incomplete (OP-01). See section 8 for the target rather than treating this subset as the final contract.

Equality observes unset identity first. Otherwise it attempts supported numeric/user-defined operations, null handling, and compatible same-type equality. It does not universally fall back to `object.Equals`, stringify operands, or convert between arbitrary unrelated types. Incompatible operand types can produce an error rather than `false`.

Except for equality/inequality's unset identity handling, ordinary unary/binary operators propagate unset. Both operands of an ordinary binary operator have already been evaluated when that propagation occurs.

## 7. Control flow and patterns

### 7.1 Conditions and conditional expressions

> **Implementation gap BOOL-01 — Boolean contexts.** The baseline below only supports implicit conversion to bool. Audit C# Boolean-expression rules, including applicable `operator true`, without conflating a condition with `operator !`. Preserve unset interception and single evaluation. Registered `bool(...)` remains an ordinary function.

`condition ? consequence : alternative` evaluates its condition once. If the condition is unset, the result is unset and neither branch is evaluated. Otherwise the condition must implicitly convert to `bool`. A runtime integer, string, or null is not a Boolean condition by default.

Only the chosen branch is evaluated. The two branches do not need a common static result type. For a runtime conditional, a subsequent operation binds to the selected runtime result without inheriting the branch's constant identity. An entire conditional that independently qualifies as a C# constant expression follows section 2.4 instead; reaching a constant branch at runtime is not sufficient.

This deliberately makes branch-result types observable in later operations. Under the NUM-01 target literal rules, consider `(A ? 2 : 2.0) / 3`, where A is a runtime Boolean input:

| A | Selected runtime value | Subsequent division |
| --- | --- | --- |
| `true` | Int `2` | Integer division, producing int `0` |
| `false` | Double `2.0` | Double division, producing approximately `0.6667` |

This difference follows from dynamic branch results, not cache history or value-dependent numeric coercion. To request double division for either branch, write `(A ? 2 : 2.0) / 3d`, or make both branch values double explicitly. This is target behavior after NUM-01, not a claim that the current parser already preserves `2.0` or accepts `3d`.

Do not inspect or execute an unselected runtime branch to choose a common numeric/result type. All syntax must still be valid; constant classification and diagnostics retain the section 2.4 rules independently of runtime branch selection.

```text
bool(A) ? thickness(4) : 'disabled'
```

Here `bool` is an ordinary function call; it is not a syntactic conversion special case. A custom type with a supported implicit conversion to `bool` may be used directly as a condition.

### 7.2 Short-circuit logical operators

`&&` and `||` evaluate their left operand once and apply the same unset/implicit-Boolean rules as a conditional. False on the left of `&&`, or true on the left of `||`, determines the result without evaluating the right operand. Otherwise the right operand is evaluated once and converted to Boolean, or propagates unset.

The current implementation returns Boolean values or unset, not a selected arbitrary operand as JavaScript operators can do.

> **Implementation gap BOOL-02 — User-defined conditional logic.** The target also includes C# user-defined `&&` and `||`: resolve the associated `&` or `|` and required `operator false` or `operator true`, including their signature restrictions, result type, and short-circuit behavior. These cases can return a user-defined type. Do not universally convert both operands to bool or simply look for an `op_LogicalAnd` method.

Unset propagation does not implement nullable three-valued logic: `unset && false` is unset because the right operand is never reached.

### 7.3 Switch evaluation

```text
A switch {
    unset => 0,
    < 0 => 0,
    0 or 1 when bool(B) => 8,
    _ => A * 2
}
```

The governing expression is evaluated once. Arms are considered in source order:

1. Test the arm's pattern. `or` alternatives short-circuit left to right.
2. If the pattern matches, evaluate the optional guard.
3. An unset guard produces unset for the entire switch; it does not continue to another arm.
4. A false guard continues to the next arm. A true guard, or no guard, selects the arm.
5. Evaluate only the selected arm's result.

An arm guard must implicitly convert to Boolean. Arm results need no common static type, and a switch result crosses the runtime-value boundary in section 2.3 rather than forwarding a selected arm's constant identity. If no arm is selected, evaluation raises `ExpressionBindingException`. There is no automatic unset result for a non-exhaustive switch.

### 7.4 Pattern semantics and limits

`_` matches every value, including null and unset. `unset` tests the actual Avalonia singleton. Constant patterns use the library's dynamic equality operation; relational patterns use its dynamic comparison operation. A comparison that produces unset is treated as a failed pattern test.

> **Open decision PAT-01 — Pattern matching is not operator equality.** Current patterns reuse dynamic equality/comparison sites. With integer `A`, `A switch { 'x' => 1, _ => 2 }` errors before the fallback. Define pattern compatibility and non-match behavior separately; fixing `==` alone does not define C# constant-pattern semantics. Until decided, use compatible pattern types and handle unset explicitly.

There is no reachability, coverage, contradiction, or exhaustiveness analysis. `_ => ...` before another arm is allowed and shadows it. Repeated patterns are allowed. Errors in an unselected arm's dynamic operations are not evaluated; invalid syntax and invalid bare identifiers are still rejected while constructing the plan.

## 8. User-defined operators and registered functions

### 8.1 User-defined operator binding target

Operators declared on CLR types participate without function-name registration. They are distinct from registered functions and cannot add new parser tokens. The binding sequence is:

1. Discover candidate user-defined operators from the operand types using the C# rules, including relevant base types and applicable lifted forms.
2. Determine applicability through the required implicit conversions.
3. If applicable user-defined operators exist, select the unique best operator or report ambiguity. Do not recover from ambiguity by trying predefined operators.
4. Otherwise consider the predefined operators and apply their resolution rules.
5. Emit the selected operation and conversions into the cached expression-tree rule.

Representative metadata names are `op_LogicalNot`, `op_OnesComplement`, `op_Addition`, `op_BitwiseAnd`, `op_Equality`, `op_True`, and `op_False`. Metadata spelling is only discovery input; it does not establish applicability or best-member selection.

> **Implementation gap OP-01 / CONV-01 — Shared machinery, separate policies.** Reuse conversion/discovery infrastructure where correct, but do not treat operators as ordinary registered functions ranked by the current score table. Conformance must cover competing operators, implicit conversions, ambiguous candidates, and predefined fallback.

> **Intentional difference EXT-01 — Extension operators excluded.** Only predefined operators and operators declared on operand-related CLR types are in scope. No extension-block scope/import lookup or extension-operator registration is specified.

### 8.2 Registration surface

Register functions on `ExpressionRegistry.Default`, or assign a separate registry to a markup extension's `Registry` property. Every registry starts with built-in functions.

```csharp
ExpressionRegistry.Default.RegisterFunction(
    "scale", static (double value, double factor) => value * factor);

ExpressionRegistry.Default.RegisterFunction(
    "numericMin", typeof(NumericFunctions), nameof(NumericFunctions.Min));
```

The public registration forms accept a `Delegate`, a `Type` plus method name, or a `MethodInfo`. Registration methods are not a fluent builder API in the current implementation.

Parameter types control applicability and invocation conversions, but the return declaration does not impose a hidden static type on downstream dynamic sites. Registered calls remain ordinary runtime expressions even with constant arguments. Section 2.3 defines the result boundary; section 2.4 defines constant eligibility.

A delegate registers a closed signature and may capture state. Invocation uses the delegate's signature and does not use `DynamicInvoke`. The type/name form discovers public static methods declared directly by that type, including generic method definitions; inherited methods are not included. A supplied `MethodInfo` must identify a static method.

Supported signatures return a value and use ordinary value parameters, optionally ending in a one-dimensional `params T[]`. Open declaring types, optional parameters, by-reference parameters, pointer parameters, byref-like parameters, and by-reference/byref-like returns are rejected. Other exotic CLR signatures are not a supported portability contract.

Duplicate signatures are rejected. Signature identity considers generic arity, parameter shapes, and `params` status; return types and generic constraints do not distinguish overloads. Adding a differently shaped overload to a built-in name is possible; replacing an existing identical signature is not.

This includes the agreed `bool` overload set. Users may register, for example, `RegisterFunction("bool", static (ExternalStatus value) => value.IsAvailable)` without modifying the external type. The call uses the same registry, conversions, and dispatch pipeline as other registered functions. No special implicit-conversion registration or internal privilege is required. Section 3.3 defines the shared name-validation contract; LEX-02 tracks its implementation.

### 8.3 Call evaluation and applicability

Function arguments are evaluated eagerly, left to right, before overload binding. A registered function cannot introduce custom lazy syntax.

The binder considers fixed signatures and both applicable forms of `params` signatures:

- Normal form passes an array value to the array parameter.
- Expanded form converts trailing arguments to the element type and constructs a new array, including an empty array when there are zero trailing arguments.

Optional-argument insertion, named arguments, extension-method syntax, and explicit generic type arguments are unsupported.

### 8.4 Selecting the best overload

> **Open decision FUNC-01 — Registered-function compatibility.** The score-based algorithm below documents current function dispatch. Full C# operator conformance does not by itself approve or replace this separate function policy; review it explicitly using NUM-01's established literal types. It must not be used as the normative operator-resolution algorithm.

After generic construction where applicable, each candidate must accept every argument through the supported conversions. To compare two candidates:

1. Compare their conversions separately at each argument. Equal target types are equivalent; otherwise prefer a lower conversion score.
2. For tied scores with different targets, prefer the target that implicitly converts to the other target but not vice versa.
3. A candidate is better by argument conversion only if it is no worse at every argument and better at least at one. Scores are not added.
4. For otherwise tied candidates, prefer normal form over expanded `params` form.
5. With equivalent effective parameter types, prefer a non-generic method over a generic one.

If there is no unique best applicable candidate, binding reports ambiguity. Registration order is not a general overload tie-breaker. The expected return type does not influence selection.

### 8.5 Generic inference

Runtime inference supports direct type parameters, arrays of matching rank, and constructed generic shapes discovered through matching runtime types, interfaces, or base classes. A shape match must be unambiguous. Under TYPE-01, ordinary arguments supply actual runtime types, and language constants supply their semantically established types (including NUM-01 literal types). Null and unset supply no type evidence. The current implementation still uses decimal literals' default materialized types; migrating that evidence is part of NUM-01/TYPE-01. Constant-conversion eligibility does not by itself infer a generic parameter from a desired result or parameter constraint.

For each type parameter, inference considers collected runtime type evidence and a supported common numeric type where available. An inferred candidate must accept all relevant evidence through supported implicit conversions, and a unique best candidate must exist. The binder then constructs the method and checks CLR generic constraints.

> **Intentional difference FUNC-02 — Generic inference subset.** This is not C#'s complete bound inference algorithm. There is no inference from return types, unexecuted branches, target properties, lambda syntax, or explicit `<T>` arguments. Constraints validate inferred choices rather than defining a general solver for unknown types.

For example, a registered `Min<T>(params T[] items) where T : INumberBase<T>` can receive runtime numeric evidence, but the constraint alone cannot choose `T` for a zero-argument call. Under Native AOT, open generic definitions are not closed at runtime; register required closed delegates or closed methods instead.

### 8.6 Functions and unset

Function invocation has one uniform unset rule for built-ins and user registrations: an evaluated argument that is exactly `AvaloniaProperty.UnsetValue` prevents invocation and propagates unset. An `object` parameter does not opt out. There is no passthrough mode, per-function policy, or per-overload registration flag.

Propagation belongs to call evaluation, not to the registered delegate's body or return type. A normal `int`- or `bool`-returning function needs neither sentinel checks nor an object-returning wrapper. Null is an ordinary argument and follows normal applicability and function semantics.

Arguments remain eager and left-to-right; propagation does not skip later argument expressions. Use a `switch` expression to handle unset explicitly before calling a function. Section 10 specifies the sentinel and error boundaries.

> **Implementation gap UNSET-01 — Uniform call-layer propagation.** The current binder may invoke an applicable object-accepting overload with the singleton and otherwise uses potential applicability to decide propagation. Replace this contract: no registered function may receive unset as a direct argument. Do not preserve passthrough or expose a policy option. The former generic potential-applicability heuristic is not the target contract; settle UNSET-02 before defining validation order.

## 9. Built-in functions

### 9.1 Current registrations

In this table, `N` means separate overloads for `int`, `long`, `float`, `double`, and `decimal`; `F` means `float`, `double`, and `decimal`; `R` means `float` and `double`. These are documentation abbreviations, not expression-language types or open generic registrations.

| Name | Signatures | Meaning |
| --- | --- | --- |
| `bool` | `(bool) -> bool`; `(N) -> bool` | Identity or numeric nonzero test |
| `abs` | `(N) -> N` | Absolute value |
| `min`, `max` | `(N, N) -> N`; `(params N[]) -> N` | Numeric minimum/maximum |
| `clamp` | `(N value, N min, N max) -> N` | Clamp to bounds |
| `round` | `(F) -> F` | .NET default rounding |
| `floor`, `ceil`, `truncate` | `(F) -> F` | Corresponding numeric rounding operation |
| `pow` | `(R value, R power) -> R` | Exponentiation |
| `log` | `(R) -> R` | Natural logarithm |
| `sign` | `(N) -> int` | Numeric sign |
| `thickness` | `(double uniform) -> Thickness` | Uniform edges |
| `thickness` | `(double horizontal, double vertical) -> Thickness` | Paired edges |
| `thickness` | `(double left, double top, double right, double bottom) -> Thickness` | Individual edges |
| `cornerRadius` | `(double uniform) -> CornerRadius` | Uniform corners |
| `cornerRadius` | `(double topLeft, double topRight, double bottomRight, double bottomLeft) -> CornerRadius` | Individual corners |
| `size` | `(double width, double height) -> Size` | Avalonia size |
| `point` | `(double x, double y) -> Point` | Avalonia point |
| `vector` | `(double x, double y) -> Vector` | Avalonia vector |

These are ordinary registered functions, subject to the same conversions, overload selection, eager argument evaluation, and errors as user functions. Numeric `bool` is a nonzero test, not general truthiness: NaN is nonzero, while positive and negative zero are false. The current implementation has no built-in string/object truthiness overload; section 9.2 defines the agreed extension.

`min()` and `max()` do not identify one numeric element type and are ambiguous across built-in expanded overloads. Passing an already typed empty array selects its matching overload but fails the nonempty-input requirement. Underlying .NET numeric exceptions, such as invalid clamp bounds or overflowing absolute value, are not silently normalized.

### 9.2 Explicit conversion contract

Conversion names are ordinary callable names, including names such as `bool` and `int` that are C# keywords. They are not cast syntax or privileged compiler intrinsics. Users may add non-conflicting overloads, including `bool(ExternalStatus)`; normal overload selection and duplicate-signature rules apply. Sections 3.2–3.3 define the agreed name, case, and escaping policy; LEX-02 tracks implementation.

| Function family | Built-in behavior for ordinary values |
| --- | --- |
| Numeric conversions, such as `int`, `long`, `double`, and `decimal` | Corresponding `Convert.ToXxx` behavior, not C# cast behavior |
| `bool` with Boolean input | Identity |
| `bool` with numeric input | Zero is false; nonzero is true |
| `bool` with string input | Every non-null string is true, including empty string, `"false"`, and `"true"`; null is false |
| `bool` with other object input | Null is false; a non-null object is true unless a more specific registered overload supplies its behavior |
| `string` | Null yields null; otherwise invoke the value's `ToString()` |

These rules describe explicit calls only. They do not make integers, strings, or null implicitly valid conditions, change logical negation, or register CLR conversions.

Numeric conversion follows the selected `Convert` operation's rounding, range, and failure behavior. For example, `int(2.9)` yields 3, `int(2.5)` yields 2, and `int(3.5)` yields 4; numeric conversion of null yields zero. Unsupported conversion, invalid text, and overflow are errors, not successful zero, null, or unset results. This explicit null-to-zero behavior is not an implicit numeric conversion.

Boolean conversion returns a Boolean, not integer 0/1. Numeric positive and negative zero are false; floating-point NaN is nonzero. Binding uses the actual runtime value type: a boxed numeric zero must retain numeric behavior rather than become true through the object-existence fallback. Strings are never parsed as Boolean text and have no culture-dependent Boolean spelling.

String conversion means `value?.ToString()`, not `Convert.ToString(value)`: `string(null)` is null, not empty string. It does not enumerate collections, serialize JSON, or impose a format provider on arbitrary `ToString()` implementations. Exceptions from user `ToString()` implementations remain execution errors.

Every conversion function is subject to uniform call-layer unset propagation: `bool(unset)`, `int(unset)`, and `string(unset)` yield unset without entering the function. No special per-name propagation code or widened delegate return type is required.

> **Implementation gap CONVERT-01 — Conversion function surface.** Add numeric/string conversion functions and complete the Boolean object/string behavior through ordinary registrations. Preserve user overloads, runtime-type dispatch, typed returns, error behavior, and UNSET-01. The current registrations in section 9.1 do not yet implement this full contract.

> **Open decision CONVERT-02 — Numeric conversion coverage.** Specify the complete built-in numeric overload surface before implementation. Deferring culture integration under CULTURE-01 does not select a provider for future numeric string conversions: document the exact overload/provider behavior when those functions are introduced. Invariant culture, current culture, and Avalonia's converter culture are not interchangeable.

### 9.3 Culture boundary and deferred enhancement

> **Warning CULTURE-01 — Binding culture is not supported by expression evaluation.** The converter receives Avalonia's `culture` argument but does not forward it to the evaluation plan or registered functions. Do not assume that expression parsing, conversion calls, or string output honor a binding's converter culture. This limitation is deliberate for the current scope; culture integration is a future enhancement, not a requirement to add parameter injection now.

Source syntax remains culture-independent. Numeric literals use section 2.2's fixed syntax and invariant parsing; their intrinsic types and AST values must not change with the user's culture. The decimal point in `1.5` and the argument separator in `min(1, 5)` retain their grammatical meanings. A string literal such as `'1,5'` remains text; parsing it numerically is a runtime function operation, not numeric-literal parsing.

No culture-parameter attributes, implicit parameter injection, or context-aware registration protocol are introduced. Registered functions continue to receive their explicit expression arguments and may use ordinary delegate captures. An application can handle localized input in a child binding's converter, register a function with an explicit culture/provider argument, or register a delegate with an application-chosen provider. These use existing function capabilities, not implicit access to Avalonia's converter culture.

Not supporting binding culture does **not** guarantee culture-invariant execution. A registered delegate, a provider-less .NET conversion, or an object's `ToString()` may depend on ambient culture or application state. In particular, `string(A)` retains `A?.ToString()` semantics and does not promise formatting with either invariant culture or the binding's culture. The evaluator does not change the thread's culture to influence these calls. A culture change alone does not establish a subscription or guarantee reevaluation.

> **Future enhancement CULTURE-01 — Explicit culture integration design.** Revisit binding-level culture only with a coherent contract for built-in and user-registered functions. Decide the default/provider precedence, how functions explicitly opt into receiving culture, the separation of parsing and formatting, and whether culture changes trigger reevaluation. Define execution-time propagation and cache behavior so a rule cannot accidentally retain the first evaluation's culture. Preserve culture-independent source syntax, ordinary registration behavior, Boolean string handling, and the existing string contract unless an explicit revision is agreed. No attribute or context API is selected by this plan.

## 10. Unset, errors, and Avalonia integration

### 10.1 Sentinel distinctions

`unset`, null, and `BindingOperations.DoNothing` are distinct. Unset is the exact `AvaloniaProperty.UnsetValue` singleton and is observable through the language's sentinel-aware operations, not a private replacement object. This does not imply that registered functions receive it. Null retains CLR null semantics. `DoNothing` is an Avalonia adapter instruction with no expression literal.

| Situation | Result or action |
| --- | --- |
| `unset == unset` | `true` |
| `unset == null` | `false` |
| Ordinary arithmetic with unset | Unset, after operand evaluation |
| `unset ? B : C` | Unset; neither branch evaluated |
| `false && B` | `false`; `B` not evaluated |
| `true || B` | `true`; `B` not evaluated |
| `unset && B`, `unset || B` | Unset; `B` not evaluated |
| Function call with a direct unset argument | Unset; function is not invoked, regardless of parameter type; see UNSET-02 for invalid calls |
| Function returns Avalonia's unset singleton | Result is unset and follows the same downstream rules |
| Failed switch pattern comparison yielding unset | Pattern does not match |
| Matched arm with unset guard | Entire switch yields unset |
| Switch with no selected arm | Binding error |
| Any input is `BindingOperations.DoNothing` | Converter returns `DoNothing` before expression evaluation |

For example, `unset + fail()` still invokes `fail()` and may fail: ordinary binary propagation is not short-circuiting. Similarly, a function's other arguments are evaluated left-to-right even when one argument is unset. In `f(unset, fail())`, `fail()` still runs; if it throws, evaluation fails rather than successfully returning unset. The body of `f` is never invoked with the singleton.

Explicit recovery belongs in control flow:

```csharp
A switch { unset => 0, _ => int(A) }
```

The unset arm returns the fallback without invoking `int`. Null does not match this arm and reaches `int(null)` under the conversion contract. Functions may deliberately return the original singleton when their CLR return type permits it; this is distinct from accepting unset inputs. The no-passthrough rule applies to evaluated argument values, not recursive inspection of arbitrary object graphs.

The `DoNothing` input check scans the entire supplied input list, including values referenced only in unselected branches or not referenced at all. A `DoNothing` value created inside an expression is not subject to this input precheck; there is no separate language-wide propagation rule for it.

### 10.2 Markup extension and result handling

`ProvideValue` parses the expression and creates a one-way `MultiBinding`. Children that are `BindingBase` instances are used directly. Other children are wrapped as one-way `CompiledBinding` sources. The converter holds one evaluation plan for that binding.

Under PATH-01, child bindings own the source paths and their change observation. The evaluator adds no nested-property, attached-property, or stream subscriptions. Pass the leaf values needed by a calculation through `Arguments`; changes observed by those bindings then provide updated inputs. Passing an object and inspecting its properties inside a registered function does not by itself make those property changes observable to the expression.

The converter does not require or recover binding source declarations. Constant AXAML children are still runtime inputs, not language constants. It does not use its `targetType`, `parameter`, or `culture` to perform expression inference or final result conversion. In particular, receiving `culture` through `IMultiValueConverter` does not grant culture support to the expression; see the warning and deferred enhancement in section 9.3. Any subsequent Avalonia property conversion is outside this language specification. There is no reverse evaluation or `ConvertBack` contract.

Accessing an argument not present in the input list fails when that access executes. Extra supplied values are otherwise unused, except for the global `DoNothing` check.

### 10.3 Error phases

> **Open decision UNSET-02 — Validation versus propagation precedence.** For a reached call containing unset, decide which errors must be reported before propagation: unknown function names, invalid arity, incompatible remaining arguments, and generic inference/constraint failures. The agreed rule prohibits invocation with unset; it does not yet choose an algorithm for diagnosing invalid calls. Do not infer that every invalid call is suppressed, or retain the old potential-applicability heuristic as normative behavior. Argument evaluation errors are not suppressed.

| Phase | Examples | Exposure |
| --- | --- | --- |
| Parse / plan construction | Invalid syntax, empty expression, unsupported bare identifier | Exception during `ProvideValue` |
| Dynamic binding at a reached site | Unknown function, no applicable overload, ambiguity, invalid condition type | Converter error notification |
| Execution | User-function exception, division error, missing input, unmatched switch | Converter error notification |

Evaluation exceptions become `BindingNotification` with `BindingErrorType.Error`. An existing `ExpressionBindingException` is retained; another exception is wrapped with expression context. Errors are not converted to unset. The converter does not itself unwrap arbitrary input `BindingNotification` objects into expression values.

Lazy evaluation applies to execution, not syntax validation. An unreachable branch must still parse and contain valid bare argument identifiers. A function name is resolved when its call site is reached, so an unreachable unknown function need not fail the current evaluation.

## 11. Execution, caching, and AOT

```text
Expression string
    -> source-generated Parlot parser
    -> immutable syntax tree
    -> fixed LINQ expression control-flow plan
    -> per-operation DLR call sites
    -> cached CLR-type-specific rules
    -> result or Avalonia binding error
```

### 11.1 Parser and plan construction

The parser definition uses Parlot combinators. Parlot's source generator emits the parser and required support into the library assembly; consumers do not need a Parlot runtime dependency. This generates parsing code, not a separately compiled expression for every AXAML string.

The target pipeline then performs semantic constant classification for the supported C# expression forms before building dynamic-site metadata. It may fold eligible constant subtrees, but must not invoke registered/user code to discover constants or specialize input values into language constants. This analysis does not require assigning one static type to the whole expression.

The plan is conceptually `Func<IList<object?>, object?>`. Branches, temporaries, ordered evaluation, and short-circuiting are LINQ expression-tree control flow. Functions and operators use dynamic operation sites. Function-call unset propagation is a shared evaluation responsibility, not a convention implemented in each registered delegate; typed delegate returns remain unchanged. Boolean contexts use an explicitly declared Boolean-returning call-site delegate, allowing typed conversion without a separate ad hoc Boolean guard algorithm.

#### Typed numeric AST and semantic metadata

A non-generic numeric-literal base with closed generic subclasses such as `NumericLiteral<int>` and `NumericLiteral<double>` is a verified feasible direction. Store the value as `T`; expose a numeric-kind discriminator and, when required, boxed-value access through the base. Derive the kind from `T` rather than accepting an independently assignable, potentially contradictory kind. Preserve literal identity and source information needed for diagnostics and unary boundary rules. Fixing a literal's type does not require fixing every surrounding expression's type before runtime. Compound constant information belongs to semantic analysis or the bound plan and must not depend on having a NumericLiteral node; a numeric-node type discriminator alone does not satisfy TYPE-01.

Generic fields avoid boxing the stored numeric value, but the reference-type AST node still allocates. Retrieving a value through `object` boxes it, and expression-tree/object-valued execution boundaries can also require boxing. Avoid repeatedly requesting boxed values on the update path; this design is not an end-to-end zero-boxing guarantee.

An isolated probe against the pinned `Parlot.SourceGenerator` version `2.0.0-preview-783` successfully combined `Terms.Number<float/double>`, adjacent `Literals.Char` suffixes, and `Then<NumericSyntax>` callbacks constructing generic subclasses. Generated code parsed `2d`, `3.0f`, and `1e100d` with the expected CLR value types and rejected `2 d` and `3.0 f`. This verifies generic AST construction and direct numeric parsing, not complete C# lexical compatibility. Even where its scanner method is named `ReadDecimal`, generated conversion can parse the captured text directly into float/double rather than materializing a CLR decimal.

> **Implementation gap NUM-01 — End-to-end literal metadata.** Replace both `NumberSyntax(decimal)` and decimal-based `DynamicArgumentInfo` assumptions. Carry intrinsic type, value, and constant identity through the AST, unary handling, plan construction, conversion/overload binding, and binder cache identity. Distinguish `2`, `2d`, and `2f` in any cache that uses literal metadata. Continue using Parlot's source-generated combinators; if the required grammar cannot be generated, stop and discuss rather than substituting a handwritten parser or runtime fallback.

### 11.2 Cache ownership and correctness

Each operation site has a DLR call site. Its current target is the fast path; the DLR also maintains site-local previous rules and binder-associated rules. Equivalent binders are canonicalized within an `ExpressionRegistry`, allowing compatible sites to reuse binder-level rules. Cache sizes, eviction order, and DLR implementation constants are not language guarantees.

Rules restrict the synthetic dispatch target, runtime argument types, and null/unset identities as appropriate. The target binder identity also distinguishes the operation/function/conversion and semantic constant metadata that affects applicability. A runtime int operand and an int constant with the same value are not interchangeable. Distinct source expressions may share a rule when their semantic metadata is equivalent; source spelling alone is not a mandatory cache discriminator. A rule must not be reused merely because two operations have the same number of arguments.

> **Implementation gap TYPE-01 — Cache semantics.** Replace literal-only metadata with the constant information required by section 2. Do not embed a runtime input value as a constant eligibility assumption. Null rules must be independent of previously seen input types and overload choices. A warmed cache and a fresh site must select equivalent behavior for the same current operands and semantic metadata.

Function rules must distinguish the singleton from ordinary object values so a cached object-accepting invocation cannot admit unset, and an unset propagation rule cannot suppress a later ordinary call. This requires the same behavior on cold and warmed sites without recompiling the outer plan.

Function rules additionally restrict the registry version. Adding an overload makes older function rules inapplicable so overload selection can run again. The cache stores executable binding rules, not function results: captured state, function side effects, and changing values are still observed on each execution.

A type change in an unselected branch does not execute or bind that branch. A type change reaching an active site can select or create another rule without replacing the outer plan. Applications should configure registries during startup; the language does not promise a transactional snapshot across all calls in one evaluation during concurrent registration.

### 11.3 Dynamic code and Native AOT

The outer lambda compiles normally when dynamic code is supported, and requests interpretation otherwise. DLR typed conversion uses a statically declared delegate shape to make its closed form visible to Native AOT. DLR is not inherently restricted to object-returning sites.

Open generic method construction is excluded when dynamic code is unavailable. Closed delegate registrations, including built-ins, are the preferred AOT path. Reflection-discovered user operators/conversions still require appropriate preservation under trimming; a successful smoke test does not guarantee every host type survives trimming automatically.

The AOT smoke project exercises representative conditional, short-circuit, rebinding, registry invalidation, and unset paths.

> **Implementation gap AOT-01 — Portability verification.** Existing trimming/dynamic-code warnings around expression factories, arrays, call sites, and reflection remain unresolved. New operator paths need JIT/interpreter parity checks and AOT coverage; representative smoke success is not universal certification. No performance guarantee has been established.

## 12. Examples and implementation checks

### 12.1 Current implementation examples

These examples record the current baseline, not final conformance outcomes. In particular, `1.0` currently produces int but must produce double under the agreed NUM-01 target. Input types are part of the example, not inferred from their printed values.

| Expression | Inputs / assumptions | Outcome |
| --- | --- | --- |
| `min(3 * A, B) + 24` | `A = 4d`, `B = 20d` | `36d` |
| `thickness(A * 2, 25, B, 25)` | `A = 4d`, `B = 6d` | `Thickness(8, 25, 6, 25)` |
| `bool(A) ? 'yes' : 'no'` | `A = 0` | `"no"` |
| `A ? 1 : 'off'` | `A = true`, then `false` | `int` result, then `string` result |
| `A ? 1 : 2` | `A = 1` | Invalid Boolean condition |
| `A switch { unset => 0, _ => A }` | `A` is Avalonia unset | `0` |
| `A switch { _ => 1, 0 => 2 }` | Any ordinary input | `1`; shadowing is not diagnosed |
| `1 / 2` | No inputs | Integer `0` |
| `1.0` | No inputs | Integer `1`, not necessarily `double` |
| `1e-2` | No inputs | Double `0.01` |
| `min(1,)` | No inputs | Parse error |
| `A switch {}` | Any input | Parse error |
| `A switch { _ => 1, }` | Any input | Parse error |

Additional lexical/semantic probes used while writing this draft confirmed `.5`, `1.`, escaped Unicode, and literal string newlines; rejected hexadecimal literals, numeric suffixes, digit separators, unknown string escapes, and non-ASCII function identifiers; and reproduced the deviations listed below. These probes do not replace permanent conformance tests.

### 12.2 TYPE-01 conformance scenarios

These are target acceptance criteria, not claims about tests already implemented. Use registered functions with unambiguous signatures to isolate operand-information behavior from FUNC-01.

| Scenario | Required behavior |
| --- | --- |
| An object-declared source supplies double `5.0`; evaluate `A / 2` | Bind using double, without inspecting the property declaration |
| A base/interface-declared source supplies a derived instance | Use the actual runtime type as candidate-discovery input |
| Input sequence boxed int, null, double, null | Bind each update from current values; neither null inherits the previous numeric type |
| `setLevel(byte)` receives `2`, `(2)`, or `1 + 1` | Preserve equivalent int-constant conversion eligibility |
| The same function receives runtime int A with value 2 | Reject implicit narrowing; repeating the same value does not make it constant |
| `getTwo()` returns int 2, or a runtime conditional selects literal 2 | Result remains an ordinary runtime int at the next site |
| An object-returning function returns a boxed int or a derived object | Downstream binding sees the actual result type |
| A nullable-returning function produces a value, then null | Same boxed-value/null boundary as binding inputs |
| Constant analysis encounters registered function/user-operator calls | Do not invoke them speculatively or treat their results as C# constants |
| Equivalent expressions run with cold/warm caches, JIT/interpreter execution | Same applicability, selection, and result/error behavior |

For constant expressions, validate semantic classification even if folding is disabled; for runtime calls, validate invocation counts and preservation of lazy branches. Include nullable-parameter and competing-overload cases after their resolution rules are implemented. Diagnose constant-expression failures according to the relevant C# rules during plan analysis, without evaluating unrelated runtime branches or user calls.

### 12.3 Conversion and unset conformance scenarios

These are target acceptance criteria, not claims about existing test coverage. Use known functions with unambiguous ordinary signatures; invalid-call precedence is UNSET-02.

| Scenario | Required behavior |
| --- | --- |
| `bool(A)` with null, boxed zero, nonzero, false, and true | False, false, true, false, and true respectively |
| `bool(A)` with empty string, `"false"`, and `"true"` | True in all three cases |
| `string(null)` | Null, not empty string |
| `int(2.9)`, `int(2.5)`, `int(3.5)` | 3, 2, 4 after NUM-01 literal typing |
| Numeric conversion of null | Numeric zero |
| Numeric conversion with invalid text or overflow | Binding error, not zero or unset |
| `bool(unset)`, `string(unset)`, `int(unset)` | Unset without delegate invocation |
| Known `f(object)` called with unset | No invocation, even though the CLR parameter could accept the singleton |
| Known `f(unset, next())` | Evaluate `next()` once; propagate unset if argument evaluation succeeds; preserve its exception otherwise |
| `A switch { unset => 0, _ => int(A) }` with unset A | Zero; conversion branch is not evaluated |
| Registered function returns the Avalonia singleton | Preserve identity and apply downstream unset rules |
| Site inputs alternate ordinary object, unset, null, ordinary object | Same results and invocation counts with cold/warm caches and compiled/interpreted execution |

User-specific Boolean overloads must affect explicit calls only. Verify that `A ? B : C` and `!A` do not acquire truthiness through those registrations.

### 12.4 LEX-02 conformance scenarios

These are target acceptance criteria, not claims about current implementation coverage.

| Scenario | Required behavior |
| --- | --- |
| Register or call `true`, `false`, `null`, `unset`, `switch`, or `_` as a function | Reject registration; reject call syntax independently of registry contents |
| `false ? true() : 0` | Reject invalid syntax even though the branch is unselected |
| Register `bool(ExternalStatus)` | Accept a non-conflicting signature with ordinary dispatch |
| Parse `bool(A)` before its overload exists | Parse as a call; report missing implementation only if reached |
| Register `when` and `or`; call them in expression/guard positions | Resolve as ordinary calls without altering contextual keyword syntax |
| Register `min` and `Min` with otherwise identical signatures | Distinct overload sets; cold/warm caches must not conflate them |
| Call unregistered `MIN` when only `min` exists | Unknown function when reached; no case-insensitive fallback |
| Register `True`, `switchValue`, `new`, or `if` | Accept ordinary function names; bare `True` is still not a literal |
| Register `A`; evaluate `A` and `A(B)` | Input zero and named call respectively, without shadowing |
| Use `@min(A)`, Unicode identifiers, or identifier Unicode escapes | Reject unsupported identifier syntax |
| Parse the same source under different registries or cultures | Same syntax tree and keyword classification |

### 12.5 SYN-01 conformance scenarios

These are target parser and execution checks, not claims about current test coverage. Assert AST grouping independently of runtime results. In the grouping rows, `{ ... }` abbreviates a valid arm list, not literal source syntax.

| Source | Required grouping or behavior |
| --- | --- |
| `A + B switch { ... }` | `A + (B switch { ... })` |
| `A * B switch { ... }` | `A * (B switch { ... })` |
| `-A switch { ... }` | `(-A) switch { ... }` |
| `A switch { ... } + B` | `(A switch { ... }) + B` |
| `A ? B : C switch { ... }` | `A ? B : (C switch { ... })` |
| `(A + B) switch { ... }` | Match the complete sum |
| `(A ? B : C) switch { ... }` | Match the selected conditional result |
| `A switch { ... } switch { ... }` | `(A switch { ... }) switch { ... }` |
| `24 + A switch { true => 8, false => 0 }` with Boolean A | 32 for true, 24 for false |
| `A switch { _ => 2 } switch { 2 => 3, _ => 4 }` | 3; second switch consumes the first result |
| Governing expression is an effectful call | Evaluate it once per reached switch; do not execute unselected arms |
| Guard or arm result contains arithmetic, a conditional, or another switch | Parse as a full expression with the same precedence rules |

Retain rejection tests for empty arm lists and trailing commas. Repeat representative execution cases under compiled and interpreted plans; changing grammar must not change branch laziness or sentinel behavior.

## 13. Known deviations and unresolved decisions

The following require separate implementation/design decisions. This specification update does not change runtime code.

Each item refers to a quoted note above. Resolve open decisions before turning them into implementation tasks.

- [ ] NUM-01: Preserve CLR numeric inputs/results; implement C# decimal integer/real literal typing and suffixes through Parlot source generation; migrate AST, conversions, and cache metadata without a decimal intermediary. Include range, sign, rounding, token-boundary, generic-node, and overload/cache distinction tests.
- [ ] TYPE-01: Implement section 2's fixed operand-information contract: semantic constant classification, runtime-value/result boundaries, null without historical/source-type inference, consistent applicability/inference metadata, and cache restrictions. Add the conformance cases in section 12.2; retain existing compliant behavior.
- [ ] OP-01: Implement C# predefined/user-defined operator resolution and candidate precedence.
- [ ] OP-02: Correct numeric promotion for established CLR operand types.
- [ ] OP-03: Separate logical negation and complement through proper unary resolution.
- [ ] CONV-01: Bring operator-related user conversion resolution into conformance.
- [ ] BOOL-01: Implement applicable C# Boolean-context rules.
- [ ] BOOL-02: Support user-defined conditional logical operators with correct short-circuiting.
- [ ] LEX-01: Accept insignificant trailing whitespace.
- [ ] LEX-02: Enforce sections 3.2–3.3 across generated parsing, all registration forms, case-sensitive lookup, duplicate detection, and cache identity. Add the conformance cases in section 12.4.
- [ ] ARG-01: Discuss input naming and addressing beyond A–Z separately; retain the current positional-input contract until decided.
- [ ] CONVERT-01: Implement section 9.2 through ordinary conversion-function registrations, including Boolean/string behavior and typed returns.
- [ ] CONVERT-02: Settle numeric conversion overload coverage and document exact provider behavior when adding numeric string conversions; do not infer binding-culture support.
- [ ] CULTURE-01 (future enhancement, deferred): Design culture integration for built-in and user functions, parsing/formatting boundaries, cache correctness, and reevaluation. No culture injection or registration attributes in the current scope; retain section 9.3's warning.
- [ ] SYN-01: Implement C# switch precedence between unary and multiplicative expressions, with left-associated repeated suffixes through Parlot source generation. Add section 12.5's AST and execution checks; preserve existing execution and pattern contracts.
- [ ] PAT-01: Define pattern comparison domains independently of operator equality.
- [ ] FUNC-01: Review registered-function overload policy independently of operators.
- [ ] UNSET-01: Replace function passthrough/potential-applicability behavior with uniform call-layer propagation; no registration policy option.
- [ ] UNSET-02: Settle error-validation precedence for invalid calls containing unset.
- [ ] AOT-01: Validate new rules under compilation, interpretation, trimming, and AOT.

LANG-01 is the settled compatibility boundary in section 1.1, not a repair item. Preserve it while implementing NUM-01, TYPE-01, and operator conformance. Validate both paths of the mixed-numeric branch example in section 7.1, its explicitly double alternative, and cold/warm-cache equivalence. Unset regression coverage belongs to section 10; it must not introduce implicit null/truthiness coercions or convert errors into unset. Explicit conversion functions retain section 9.2's separate contract.

EXT-01 (extension operators) and FUNC-02 (generic-function inference subset) remain separate intentional scope boundaries owned by section 8. They are not additional exceptions hidden inside LANG-01.

PATH-01 is a settled scope boundary: bindings navigate and observe sources; expressions compute from their inputs. CAST-01 is also settled for the current scope: use explicit functions, without cast syntax or CLR type-name resolution. Test that user `bool` overloads affect explicit calls only, and that function registration does not add conversion rules or subscriptions.

Currently unsupported features include member access, indexing, assignment, lambdas, explicit casts, `new`, explicit generic arguments, null-coalescing/null-conditional operators, advanced C# patterns, string interpolation, and custom operator token registration. Explicit casts are excluded from the current scope under CAST-01; reconsidering them requires a separate proposal. Binding Path navigation and observation are deliberately excluded under PATH-01. Supporting registered CLR operators does not allow a registry to change the parser's operator grammar.

Expressions may call effectful application functions and do not impose execution, recursion, allocation, or input-length limits. The evaluator is not a security sandbox for untrusted expressions or untrusted function registrations.

## 14. Implementation references

The source links document the current baseline. The target operator semantics are governed by the [C# expressions specification](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/expressions), particularly operator overload resolution, numeric promotion, lifted operators, unary/binary operators, and conditional logical operators, constant expressions and expression classifications, together with the [C# conversions specification](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/conversions). Pin the normative C# version during implementation; extension operators remain excluded.

When implementation catches up, update the relevant quoted gap and its conformance tests rather than silently deleting the intended contract.

- [Parlot grammar definition](../src/ExpressionBinding.Avalonia/Parsing/ExpressionParser.parlot.cs)
- [Parser entry point](../src/ExpressionBinding.Avalonia/Parsing/ExpressionParser.cs)
- [Syntax node definitions](../src/ExpressionBinding.Avalonia/Parsing/ExpressionSyntax.cs)
- [Conversions, operators, overloads, and generic inference](../src/ExpressionBinding.Avalonia/Binding/ExpressionBinder.cs)
- [Control-flow plan compiler](../src/ExpressionBinding.Avalonia/Binding/ExpressionPlanCompiler.cs)
- [DLR binding and restrictions](../src/ExpressionBinding.Avalonia/Binding/ExpressionDynamicBinder.cs)
- [Registry and registration validation](../src/ExpressionBinding.Avalonia/ExpressionRegistry.cs)
- [Built-in functions](../src/ExpressionBinding.Avalonia/BuiltInFunctions.cs)
- [Avalonia markup extension](../src/ExpressionBinding.Avalonia/ExpressionBinding.cs)
- [Converter and error adapter](../src/ExpressionBinding.Avalonia/Binding/ExpressionConverter.cs)
- [Evaluator tests](../tests/ExpressionBinding.Avalonia.Tests/ExpressionEvaluatorTests.cs)
- [AXAML integration tests](../tests/ExpressionBinding.Avalonia.Tests/ExpressionBindingTests.cs)
- [Native AOT smoke project](../tests/ExpressionBinding.Avalonia.AotSmoke)

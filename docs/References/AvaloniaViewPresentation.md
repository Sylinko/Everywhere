# Avalonia, View, and Presentation Reference

Read this reference before modifying Avalonia controls, Views, Presentation objects, DynamicData-backed UI collections, virtualization, or UI animation. These rules are scoped to the UI and Presentation layers; they do not define the threading or collection model of plugins, network services, or other background components.

## Presentation Objects and View Models

- Treat ViewModels as relatively heavy objects. Do not create many ViewModels for simple projections or a handful of UI-only state values.
- Prefer an existing model, a lightweight Presentation or row object, a custom control, or a View-layer projection when that provides a clear ownership boundary.
- Keep Presentation objects stable where possible. Replacing all Presentation objects after a small source change causes binding churn, lost control state, animation discontinuities, and visible flicker.

## UI Thread Boundary

- State explicitly owned by a View or Presentation object may be UI-thread-affine.
- Marshal genuine background ingress to the UI thread at the boundary. Once inside that boundary, keep the object model single-threaded and simple.
- Do not add locks, concurrent collections, immutable snapshots, pending-operation queues, or defensive reentrancy state to code whose callbacks and timers are all guaranteed to execute on the UI thread.
- Prefer the existing `Dispatcher.UIThread.PostOnDemand` helper when work may arrive on either thread. It executes immediately when already on the UI thread. Avalonia dispatcher invocation also performs its own access check, so avoid redundant layers of dispatching.

## Collections and DynamicData

- For a read-heavy reference collection confined to the UI thread, prefer a plain `List<T>`. Avoid immutable collections or repeated snapshots without a demonstrated need.
- In this project, `SourceList.Items` may be read as a stable snapshot. Do not copy it again merely for defensive safety.
- Use DynamicData change sets to update projections incrementally. Reconcile additions, removals, moves, replacements, and refreshes without rebuilding the entire output collection.
- Preserve object identity when the logical item has not changed. Prefer reference identity when source objects already provide the correct lifetime identity.
- When a parent control can naturally own and render a bounded child collection, prefer that ownership over complicated flattening or multi-list merge machinery.

## Controls, Bindings, and Templates

- Before creating or substantially redesigning AXAML, inspect similar controls in `Views/Chat` and `Views/Controls`, including their AXAML, code-behind, and relevant styles. Follow established resource, class, binding, and template patterns; verify binding ownership before reusing them.
- For fixed presentation snapshots, omit absent items when constructing the data. Use `IsVisible` for optional sections and visibility that can change at runtime.
- Give custom controls strongly typed Styled or Direct properties for their core input instead of relying on an ambiguous `DataContext`.
- Continue to use `DataContext` where it is natural to Avalonia, including templates, `ItemsControl`, and ordinary binding scopes.
- Prefer AXAML for controls with a visual tree, templates, or substantial styling. Pure C# is appropriate for layout algorithms, logic-only controls, or small controls that do not benefit from a template.
- When runtime type determines the presentation structure, prefer strongly typed `DataTemplate`s over a single control containing many type checks and irrelevant properties.
- Move shared styles to an appropriate common scope. Do not copy specialized control styles into multiple Views.
- Keep visible text composable through bindings, localization, templates, and `Inlines`; avoid specialized controls that hard-code a sentence when only periodic refresh or formatting is specialized.

## Lifetime and Lazy Visual Trees

- Behaviors and periodic refresh components must register and unregister during attach/detach and must respond correctly when `IsEnabled` changes.
- Create expensive visual trees only when they become visible. Terminals, detailed Markdown, diffs, and similarly complex content should normally be loaded on demand.
- When hidden content has no state that must survive, allow it to be destroyed rather than keeping the visual tree alive indefinitely.
- Keep the outer structure of a large scrolling list flat, stable, and virtualizable. A bounded, lazy-loaded detail region with its own maximum height and scrolling may intentionally remain non-virtualized when nested virtualization would add more complexity than value.

## Animation and Layout

- Use the simplest panel that directly expresses the layout intent. When multiple controls only need to share a single layout slot, prefer `Panel` over `Grid`.
- When one child should keep its natural size and another should fill the remaining space, prefer `DockPanel`, docking the size-constrained child and leaving the fill child last. Use `HorizontalSpacing` or `VerticalSpacing` for separation.
- Reserve `Grid` for layouts that genuinely require row or column sizing and alignment.
- Use `RenderTransform` and opacity for visual-only movement and fading. These do not participate in measure or arrange and should be preferred over changing layout dimensions for animation.
- Do not rebuild controls merely to animate a state change. Maintain stable control identity and animate the visual properties that changed.
- Keep animation state minimal and tied to the real control lifetime. Avoid speculative fallback state machines unless the product requirement calls for them.

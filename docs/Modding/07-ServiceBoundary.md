# Service Boundary

Status: implemented with Autofac. Mod lifecycle regressions cover generic logging, lazy and keyed
resolution, host service identity, child scopes, initialization failure and confirmed collection.
Desktop acceptance and the other release gates remain listed in [Validation](03-Validation.md).

## Application and activation ownership

The application registers services through `IServiceCollection`. `HostServiceContainer` in
`Everywhere.Modding.Hosting.Services` translates those registrations through
`Autofac.Extensions.DependencyInjection`. The application root uses Autofac 9.3.4 and integration
package 11.0.2. App owns one background startup/activation task and signals its application-lifetime
token on exit. The platform entry point waits for that task after the desktop loop ends, then owns
asynchronous root disposal. Initializer registration stores static ordering metadata and a deferred
invocation rather than eagerly resolving every initializer to read its order. Services decide whether
their own disposal needs to wait for background work.

Each mod activation has one lifetime scope associated with its entry assembly's collectible
`AssemblyLoadContext`. `IModContext.Services` exposes that activation through standard DI
interfaces. Mod code does not need an Autofac reference. There is no public mod
service-registration API.

Host singletons remain application-owned. Direct, collection and constructor-injected resolution
must preserve their identity and must not transfer their disposal to the mod. Scoped and transient
services retain their declared lifetimes. Services closed over collectible arguments, such as
`ILogger<ModType>`, have activation-owned closed registrations and instances. Child scopes inherit
these registrations and preserve their normal lifetimes.

## Native registration sources

`HostRegistrationSources` obtains Autofac's default relationship sources and translates open generic
`ServiceDescriptor` registrations in separate source containers. Neither source container activates
services. The application container excludes automatic default-source installation and installs these
native sources with explicit ownership:

- Default relationship sources implement `IPerScopeRegistrationSource` through a forwarding wrapper.
  Autofac copies them into an isolated activation and skips them when that activation queries its
  parent. Each source retains its native adapter flag and relative order, preserving explicit
  registrations ahead of generated factories and relationships.
- Open generic sources accept noncollectible service types at the application root and collectible
  service types at the activation. Host generics therefore use inherited parent registrations, while
  mod generics bind locally. The source implementation still performs all generic and keyed binding.

The per-scope marker is necessary because Autofac's implicit relationships and any-key fallback can
make nested registration queries without forwarding the outer scope-isolation marker. Running those
queries in the application root can retain a mod type even when no service implementation is
registered there. Resolving `Lazy<ILogger<ModType>>` or a keyed `ILogger<ModType>` must not create
such root queries.

`ModServiceBoundary` provides activation-local provider and scope-factory interfaces. Resolution,
availability checks and keyed behavior delegate to the native Autofac provider. A caller-created child
scope must be disposed before activation cleanup completes. The manually constructed mod entry is
owned by the engine, rather than registered for a second disposal through DI.

## Intended access

`ServiceLocator` is internal application infrastructure. Its friend assembly is the actual platform
assembly name, `Everywhere`, shared by the Windows, macOS and Linux entry projects. Core tests retain
their existing friend access. Host-owned control factories may use this entry point for host-owned
services; they must not pass it to mod factories or resolve mod generic arguments.

Mods use `context.Services`. Bypassing this boundary through reflection or access-check overrides
can invalidate unloading. This is an API and lifetime boundary, not a security sandbox.

Settings control factories choose their dependencies when they are created. The settings presenter
calls `ISettingsControl.CreateControl()` without supplying a provider. A mod factory can capture
its activation provider or already resolved services. See [Settings Specification](05-SettingsSpecification.md).

## Retirement

1. Signal stopping and await the entry's optional disposal. The entry stops and awaits outstanding users.
2. Reverse activation registrations, including UI contributions and their factories.
3. Asynchronously dispose the activation's service scope. Prefer asynchronous service disposal when
   both disposal interfaces are implemented; the engine-owned entry is not disposed again through DI.
4. After all affected consumers stop, dispose the retiring assembly owners' static registrations.
5. Selectively clear Autofac reflection entries for the owners actually being released, following
   constructed generic arguments and element types as well as defining assemblies.
6. Request ALC unloading and verify collection before releasing explicit native loader handles.

The dependency plan determines consumer-before-provider order. Shared assembly owners stay alive
until their last consumer retires. Cache cleanup includes retiring dependency ALCs, not just the entry
ALC. Active owners' registrations and unrelated global cache entries remain available.

Timeouts and disposal failures preserve uncertain ownership and require process restart. Process exit
disposes entries and their service scopes without running hot-unload reversal or collection checks.

The settings migration providers remain short-lived MSDI providers. SK continues to use
`KernelBuilder.Build()`, preserving its generated keyed-service index and execution pipeline. These
independent containers retain their existing construction and disposal paths.

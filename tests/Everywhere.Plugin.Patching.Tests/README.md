# Optional patch integration tests

Run this project explicitly in a fresh test process:

```powershell
dotnet test tests/Everywhere.Plugin.Patching.Tests/Everywhere.Plugin.Patching.Tests.csproj -c Release
```

It is intentionally outside the default solution test set. RuntimeDetour is confined to
this test project and its two collectible fixtures; production Hosting and SDK projects
do not depend on it. The tests require confirmed collection after independent hook
removal and after an initializer throws with an installed hook. They do not claim generic
method support, concurrent callback draining coverage, or compatibility with every host
publish transformation. Never weaken a collection assertion to accept RestartRequired.

The exit-only case uses a separate target and deliberately leaves its hooks installed until
the test process ends. Entry disposal makes those callbacks pass through safely; it does not
dispose the registered hook handles. This case does not claim assembly collection.

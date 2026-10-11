using System.Collections;
using System.Reflection;
using Mono.Cecil;
using MonoMod.InlineRT;

// ReSharper disable once CheckNamespace
namespace MonoMod;

internal static class MonoModRules
{
    static MonoModRules()
    {
        MonoModder? modder = null;
        try
        {
            modder = MonoModRulesManager.Modder;
            if (modder.Module.GetType("Avalonia.Win32.WindowImpl") is not { } windowImpl)
            {
                modder.Log("[Everywhere] Avalonia.Win32.WindowImpl was not found; composition bridge was not injected.");
                return;
            }

            var dependencyCache = modder
                .GetType()
                .GetField("DependencyCache", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(modder) as IDictionary;
            ModuleDefinition? contractsModule = null;
            if (dependencyCache is not null)
            {
                foreach (DictionaryEntry entry in dependencyCache)
                {
                    if (entry.Value is ModuleDefinition { Assembly.Name.Name: "Everywhere.Patches.Contracts" } module)
                    {
                        contractsModule = module;
                        break;
                    }
                }
            }

            if (contractsModule?.Assembly?.Name is not { } contractsAssemblyName)
            {
                modder.Log("[Everywhere] Everywhere.Patches.Contracts could not be resolved; composition bridge was not injected.");
                return;
            }

            AssemblyNameReference? contractsReference = null;
            foreach (var assemblyReference in modder.Module.AssemblyReferences)
            {
                if (assemblyReference.Name == contractsAssemblyName.Name)
                {
                    contractsReference = assemblyReference;
                    break;
                }
            }

            contractsReference ??= new AssemblyNameReference(contractsAssemblyName.Name, contractsAssemblyName.Version);
            if (!modder.Module.AssemblyReferences.Contains(contractsReference))
            {
                modder.Module.AssemblyReferences.Add(contractsReference);
            }

            var feature = new TypeReference(
                "Everywhere.Patches.Contracts.Interop",
                "IWindowCompositionFeature",
                modder.Module,
                contractsReference);

#pragma warning disable CS8602
            var interfaces = windowImpl.Interfaces ??
                throw new InvalidOperationException("Avalonia.Win32.WindowImpl does not expose an interface collection.");
#pragma warning restore CS8602
            foreach (var implementation in interfaces)
            {
                if (implementation.InterfaceType.FullName == feature.FullName)
                {
                    return;
                }
            }

            interfaces.Add(new InterfaceImplementation(feature));
            modder.Log("[Everywhere] Injected IWindowCompositionFeature into Avalonia.Win32.WindowImpl.");
        }
        catch (Exception exception)
        {
            modder?.Log($"[Everywhere] Composition bridge injection failed: {exception}");
            throw;
        }
    }
}
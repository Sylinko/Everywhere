using System.Collections;
using System.Diagnostics;
using System.Security;
using Everywhere.BuildTask.Patcher;
using Microsoft.Build.Framework;
using TaskItem = Microsoft.Build.Utilities.TaskItem;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Everywhere.BuildTask.Patcher.Tests;

[TestFixture]
public sealed class WeaveAssembliesTaskTests
{
    private string _directory = string.Empty;

    [SetUp]
    public void SetUp() => _directory = Directory.CreateTempSubdirectory("everywhere-patcher-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void Execute_WithSeparateReferenceAndRuntimeAssets_PatchesOnlySelectedImplementationAndPreservesMetadata()
    {
        var reference = CreateAssembly("targeting/ref/net10.0", "Example", 0, isReference: true);
        var runtime = CreateAssembly("runtime/runtimes/test/lib/net9.0", "Example", 1);
        var donor = CreateAssembly("donor", "Example.Patch", 42, isDonor: true);
        var original = File.ReadAllBytes(runtime);
        var task = CreateTask(reference, donor, runtime, runtime);
        task.RuntimeAssemblies[0].SetMetadata("NuGetPackageId", "Example.Runtime");
        task.RuntimeAssemblies[0].SetMetadata("DestinationSubPath", "Example.dll");

        Assert.That(task.Execute(), Is.True, GetErrors(task));
        Assert.Multiple(() =>
        {
            Assert.That(ReadValue(task.AddedReferences.Single().ItemSpec), Is.EqualTo(42));
            Assert.That(task.AddedReferences.Single().GetMetadata("NuGetPackageId"), Is.EqualTo("Example.Runtime"));
            Assert.That(task.AddedReferences.Single().GetMetadata("DestinationSubPath"), Is.EqualTo("Example.dll"));
            Assert.That(task.RemovedReferences.Single().ItemSpec, Is.EqualTo(runtime));
            Assert.That(File.ReadAllBytes(runtime), Is.EqualTo(original));
            Assert.That(ReadValue(reference), Is.Zero);
        });
    }

    [Test]
    public void Execute_WithUnchangedInputs_DoesNotRewriteOutputOrStamp()
    {
        var reference = CreateAssembly("ref", "Example", 0, isReference: true);
        var runtime = CreateAssembly("runtime", "Example", 1);
        var donor = CreateAssembly("donor", "Example.Patch", 42, isDonor: true);
        var first = CreateTask(reference, donor, runtime);
        Assert.That(first.Execute(), Is.True, GetErrors(first));
        var output = first.AddedReferences.Single().ItemSpec;
        var timestamp = DateTime.UtcNow.AddDays(-10);
        File.SetLastWriteTimeUtc(output, timestamp);
        File.SetLastWriteTimeUtc(output + ".inputs", timestamp);
        var outputTimestamp = File.GetLastWriteTimeUtc(output);
        var stampTimestamp = File.GetLastWriteTimeUtc(output + ".inputs");
        var second = CreateTask(reference, donor, runtime);

        Assert.That(second.Execute(), Is.True, GetErrors(second));
        Assert.Multiple(() =>
        {
            Assert.That(File.GetLastWriteTimeUtc(output), Is.EqualTo(outputTimestamp));
            Assert.That(File.GetLastWriteTimeUtc(output + ".inputs"), Is.EqualTo(stampTimestamp));
            Assert.That(second.AddedReferences, Has.Length.EqualTo(1));
        });
    }

    [Test]
    public void Execute_WhenSelectedRuntimePathChangesToOlderFile_InvalidatesCachedOutput()
    {
        var reference = CreateAssembly("ref", "Example", 0, isReference: true);
        var runtime = CreateAssembly("runtime-v1", "Example", 1);
        var donor = CreateAssembly("donor", "Example.Patch", 42, isDonor: true);
        var first = CreateTask(reference, donor, runtime);
        Assert.That(first.Execute(), Is.True, GetErrors(first));
        var output = first.AddedReferences.Single().ItemSpec;
        var firstId = ReadModuleId(output);
        var replacement = CreateAssembly("runtime-v2", "Example", 2);
        File.SetLastWriteTimeUtc(replacement, DateTime.UtcNow.AddDays(-20));
        var second = CreateTask(reference, donor, replacement);

        Assert.That(second.Execute(), Is.True, GetErrors(second));
        Assert.Multiple(() =>
        {
            Assert.That(ReadModuleId(output), Is.Not.EqualTo(firstId));
            Assert.That(ReadModuleId(output), Is.EqualTo(ReadModuleId(replacement)));
            Assert.That(ReadValue(output), Is.EqualTo(42));
        });
    }

    [Test]
    public void Execute_WhenDonorChanges_InvalidatesCachedOutput()
    {
        var reference = CreateAssembly("ref", "Example", 0, isReference: true);
        var runtime = CreateAssembly("runtime", "Example", 1);
        var donor = CreateAssembly("donor", "Example.Patch", 42, isDonor: true);
        var first = CreateTask(reference, donor, runtime);
        Assert.That(first.Execute(), Is.True, GetErrors(first));
        CreateAssembly("donor", "Example.Patch", 43, isDonor: true);
        var second = CreateTask(reference, donor, runtime);

        Assert.That(second.Execute(), Is.True, GetErrors(second));
        Assert.That(ReadValue(second.AddedReferences.Single().ItemSpec), Is.EqualTo(43));
    }

    [Test]
    public void Execute_WithAmbiguousRuntimeAssets_FailsWithoutChoosingOne()
    {
        var reference = CreateAssembly("ref", "Example", 0, isReference: true);
        var first = CreateAssembly("runtime-v1", "Example", 1);
        var second = CreateAssembly("runtime-v2", "Example", 2);
        var donor = CreateAssembly("donor", "Example.Patch", 42, isDonor: true);
        var task = CreateTask(reference, donor, first, second);

        Assert.That(task.Execute(), Is.False);
        Assert.That(GetErrors(task), Does.Contain("found 2"));
        Assert.That(task.AddedReferences, Is.Empty);
    }

    [Test]
    public void Execute_WithoutRuntimeImplementation_FailsInsteadOfPatchingReference()
    {
        var reference = CreateAssembly("ref", "Example", 0, isReference: true);
        var donor = CreateAssembly("donor", "Example.Patch", 42, isDonor: true);
        var task = CreateTask(reference, donor);

        Assert.That(task.Execute(), Is.False);
        Assert.That(GetErrors(task), Does.Contain("found 0"));
        Assert.That(task.AddedReferences, Is.Empty);
    }

    [Test]
    public void Execute_WithReferenceAssemblyAsRuntimeInput_RejectsIt()
    {
        var reference = CreateAssembly("ref", "Example", 0, isReference: true);
        var donor = CreateAssembly("donor", "Example.Patch", 42, isDonor: true);
        var task = CreateTask(reference, donor, reference);

        Assert.That(task.Execute(), Is.False);
        Assert.That(GetErrors(task), Does.Contain("reference assembly"));
    }

    [Test]
    public void Execute_WithFrameworkReference_RejectsRewritingNativeRegistrationTokens()
    {
        var reference = CreateAssembly("ref", "Example", 0, isReference: true);
        var runtime = CreateAssembly("runtime", "Example", 1);
        var donor = CreateAssembly("donor", "Example.Patch", 42, isDonor: true);
        var task = CreateTask(reference, donor, runtime);
        task.References[0].SetMetadata("FrameworkReferenceName", "Example.Framework");

        Assert.That(task.Execute(), Is.False);
        Assert.That(GetErrors(task), Does.Contain("native SDK components may depend on stable metadata tokens"));
    }

    [Test]
    public async Task MsBuild_DuringEvaluationAndDesignTimeBuild_DoesNotWeave()
    {
        var project = CreatePipelineProject("RuntimePackAsset");
        var evaluation = await RunMsBuildAsync(project, "-getItem:ProjectReference");
        Assert.That(evaluation, Does.Not.Contain("[Patcher]"));
        Assert.That(Directory.Exists(Path.Combine(_directory, "obj", "Patched")), Is.False);

        var designTime = await RunMsBuildAsync(project, "-t:Build", "-p:DesignTimeBuild=true");
        Assert.That(designTime, Does.Not.Contain("[Patcher]"));
        Assert.That(Directory.Exists(Path.Combine(_directory, "obj", "Patched")), Is.False);
    }

    [TestCase("RuntimeCopyLocalItems")]
    [TestCase("RuntimePackAsset")]
    public async Task MsBuild_WithRuntimeAssets_PreservesCompileReferencesPublishMetadataAndIncrementalOutput(string assetKind)
    {
        var project = CreatePipelineProject(assetKind);
        await RunMsBuildAsync(project, "-t:Build");
        var output = Path.Combine(_directory, "obj", "Patched", "Example.dll");
        Assert.That(ReadValue(output), Is.EqualTo(42));
        var timestamp = File.GetLastWriteTimeUtc(output);
        var second = await RunMsBuildAsync(project, "-t:Build");
        var lines = File.ReadAllLines(Path.Combine(_directory, "items.txt"));
        Assert.Multiple(() =>
        {
            Assert.That(second, Does.Contain("[Patcher] Up-to-date"));
            Assert.That(File.GetLastWriteTimeUtc(output), Is.EqualTo(timestamp));
            Assert.That(lines[0], Is.EqualTo(Path.Combine(_directory, "ref", "Example.dll")));
            Assert.That(lines[1], Is.EqualTo(typeof(object).Assembly.Location));
            Assert.That(lines[2], Is.EqualTo(output));
            Assert.That(lines[3], Is.EqualTo($"{output}|Example.Runtime|Example.dll"));
            Assert.That(lines[4], Is.EqualTo(output));
        });
    }

    private string CreatePipelineProject(string assetKind)
    {
        var reference = CreateAssembly("ref", "Example", 0, isReference: true);
        var runtime = CreateAssembly("runtime", "Example", 1);
        var donor = CreateAssembly("donor", "Example.Patch", 42, isDonor: true);
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (!File.Exists(Path.Combine(repository.FullName, "src", "Build.Patches.targets")))
            repository = repository.Parent ?? throw new InvalidOperationException("Repository root was not found.");
        var configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent?.Name ?? "Debug";
        var targets = SecurityElement.Escape(Path.Combine(repository.FullName, "src", "Build.Patches.targets"));
        var project = Path.Combine(_directory, "pipeline.proj");
        File.WriteAllText(project, $$"""
            <Project>
              <PropertyGroup>
                <Configuration>{{configuration}}</Configuration>
                <IntermediateOutputPath>{{_directory}}/obj/</IntermediateOutputPath>
              </PropertyGroup>
              <Import Project="{{targets}}" />
              <ItemGroup>
                <ProjectReference Remove="@(ProjectReference)" />
                <ReferencePath Include="{{reference}};{{typeof(object).Assembly.Location}}" />
                <{{assetKind}} Include="{{runtime}}" NuGetPackageId="Example.Runtime" DestinationSubPath="Example.dll" AssetType="runtime" />
                <ReferenceCopyLocalPaths Include="@({{assetKind}})" />
                <PatchAssembly Include="{{donor}}" PatchTargets="Example" />
              </ItemGroup>
              <Target Name="ResolveAssemblyReferences" />
              <Target Name="ComputeResolvedFilesToPublishList">
                <ItemGroup>
                  <ResolvedFileToPublish Include="{{runtime}}" NuGetPackageId="Example.Runtime" DestinationSubPath="Example.dll" />
                </ItemGroup>
              </Target>
              <Target Name="Build" DependsOnTargets="ResolveAssemblyReferences;ComputeResolvedFilesToPublishList">
                <WriteLinesToFile File="{{_directory}}/items.txt" Overwrite="true"
                  Lines="@(ReferencePath);@(ReferenceCopyLocalPaths);@(ResolvedFileToPublish -> '%(Identity)|%(NuGetPackageId)|%(DestinationSubPath)');@({{assetKind}})" />
              </Target>
            </Project>
            """);
        return project;
    }

    private static async Task<string> RunMsBuildAsync(string project, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "msbuild", project, "-m:1", "-nr:false", "-v:normal" }
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("MSBuild did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var text = await output + await error;
        Assert.That(process.ExitCode, Is.Zero, text);
        return text;
    }

    private WeaveAssembliesTask CreateTask(string reference, string donor, params string[] runtimePaths)
    {
        var patch = new TaskItem(donor);
        patch.SetMetadata("PatchTargets", "Example");
        return new WeaveAssembliesTask
        {
            BuildEngine = new TestBuildEngine(),
            References = [new TaskItem(reference), new TaskItem(typeof(object).Assembly.Location)],
            RuntimeAssemblies = runtimePaths.Select(path => (ITaskItem)new TaskItem(path)).ToArray(),
            PatchAssemblies = [patch],
            OutputDirectory = Path.Combine(_directory, "patched")
        };
    }

    private string CreateAssembly(string directory, string assemblyName, int value, bool isReference = false, bool isDonor = false)
    {
        var path = Path.Combine(_directory, directory, assemblyName + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _directory);
        using var module = ModuleDefinition.CreateModule(assemblyName, ModuleKind.Dll);
        var coreIdentity = typeof(object).Assembly.GetName();
        var coreReference = (AssemblyNameReference)module.TypeSystem.Object.Scope;
        coreReference.Name = coreIdentity.Name;
        coreReference.Version = coreIdentity.Version;
        coreReference.PublicKeyToken = coreIdentity.GetPublicKeyToken();
        var type = new TypeDefinition("Example", isDonor ? "patch_Value" : "Value", TypeAttributes.Public, module.TypeSystem.Object);
        var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        method.Body.GetILProcessor().Emit(OpCodes.Ldc_I4, value);
        method.Body.GetILProcessor().Emit(OpCodes.Ret);
        type.Methods.Add(method);
        module.Types.Add(type);
        if (isReference)
        {
            var constructor = typeof(System.Runtime.CompilerServices.ReferenceAssemblyAttribute).GetConstructor(Type.EmptyTypes) ??
                throw new InvalidOperationException("ReferenceAssemblyAttribute constructor was not found.");
            module.Assembly.CustomAttributes.Add(new CustomAttribute(module.ImportReference(constructor)));
        }
        module.Write(path);
        return path;
    }

    private static int ReadValue(string path)
    {
        using var module = ModuleDefinition.ReadModule(path);
        return (int)module.GetType("Example.Value").Methods.Single(method => method.Name == "Read").Body.Instructions[0].Operand;
    }

    private static Guid ReadModuleId(string path)
    {
        using var module = ModuleDefinition.ReadModule(path);
        return module.Mvid;
    }

    private static string GetErrors(WeaveAssembliesTask task) => string.Join("\n", ((TestBuildEngine)task.BuildEngine).Errors);

    private sealed class TestBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? string.Empty);
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) =>
            throw new NotSupportedException();
    }
}
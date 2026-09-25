using Avalonia.Data;

namespace ExpressionBinding.Avalonia.Tests;

public sealed class ExpressionBindingTests
{
    [Test]
    public void ProvideValue_WithBindingsAndConstants_CreatesOrderedMultiBinding()
    {
        var sourceBinding = new ReflectionBinding("Value");
        var extension = new global::ExpressionBinding.Avalonia.ExpressionBinding
        {
            Expression = "A + B",
            Arguments =
            {
                sourceBinding,
                4d
            }
        };

        var result = extension.ProvideValue(EmptyServiceProvider.Instance);

        Assert.That(result, Is.TypeOf<MultiBinding>());
        var binding = (MultiBinding)result;
        Assert.Multiple(() =>
        {
            Assert.That(binding.Bindings, Has.Count.EqualTo(2));
            Assert.That(binding.Bindings[0], Is.SameAs(sourceBinding));
            Assert.That(binding.Bindings[1], Is.TypeOf<CompiledBinding>());
            Assert.That(binding.Converter, Is.Not.Null);
        });
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        internal static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}

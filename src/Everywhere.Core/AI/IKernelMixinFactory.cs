namespace Everywhere.AI;

/// <summary>
/// Represents a factory for creating instances of <see cref="KernelMixin"/>.
/// </summary>
public interface IKernelMixinFactory
{
    KernelMixin Create(Assistant assistant);
}
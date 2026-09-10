using Everywhere.Automation;
using Everywhere.Mac.Interop;

namespace Everywhere.Mac.Automation;

internal static class AXFailureExtensions
{
    extension(AXError error)
    {
        public bool IsUnsupported() => error is
            AXError.AttributeUnsupported or
            AXError.ActionUnsupported or
            AXError.NoValue or
            AXError.ParameterizedAttributeUnsupported or
            AXError.NotImplemented;

        public bool IsProviderFailure() => error is not AXError.Success && !error.IsUnsupported();

        public void ThrowIfProviderFailure(string operation)
        {
            if (error.IsProviderFailure())
            {
                throw new AXException(error, $"Failed to {operation}. AX returned {error}.");
            }
        }

        public VisualElementQueryFailureKind GetFailureKind() => error switch
        {
            AXError.InvalidUIElement => VisualElementQueryFailureKind.ElementUnavailable,
            AXError.AttributeUnsupported or
                AXError.ActionUnsupported or
                AXError.NoValue or
                AXError.ParameterizedAttributeUnsupported or
                AXError.NotImplemented => VisualElementQueryFailureKind.Unsupported,
            _ => VisualElementQueryFailureKind.ProviderFailure,
        };
    }

    extension(AXException exception)
    {
        public VisualElementQueryFailure CreateFailure() => new(exception.Error.GetFailureKind(), null, exception);

        public Exception CreateException() => exception.Error switch
        {
            AXError.AttributeUnsupported or AXError.ActionUnsupported or AXError.ParameterizedAttributeUnsupported or AXError.NotImplemented =>
                new NotSupportedException(exception.Message, exception),
            AXError.InvalidUIElement => new InvalidOperationException("The macOS Accessibility element is no longer available.", exception),
            _ => new InvalidOperationException(exception.Message, exception),
        };
    }
}
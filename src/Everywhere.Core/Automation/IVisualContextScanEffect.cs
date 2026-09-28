namespace Everywhere.Automation;

/// <summary>Creates optional presentation scopes for visual-query scan captures.</summary>
public interface IVisualContextScanEffect
{
    /// <summary>Begins a scope that owns every capture passed to it.</summary>
    IVisualContextScanScope Begin(CancellationToken cancellationToken);
}

/// <summary>Receives owned captures from one visual query and controls their presentation lifetime.</summary>
public interface IVisualContextScanScope : IDisposable
{
    /// <summary>Takes ownership of a completed scan capture, including when the scope rejects it.</summary>
    void AddCapture(IVisualElementCapture capture);

    /// <summary>Closes producer input and lets accepted captures drain asynchronously.</summary>
    void Complete();
}
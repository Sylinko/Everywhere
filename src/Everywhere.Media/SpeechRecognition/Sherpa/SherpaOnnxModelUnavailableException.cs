namespace Everywhere.Media.SpeechRecognition.Sherpa;

public sealed class SherpaOnnxModelUnavailableException(
    string modelId,
    SherpaOnnxModelInstallState state) : InvalidOperationException($"sherpa-onnx model is not installed or valid: {modelId}.")
{
    public string ModelId { get; } = modelId;

    public SherpaOnnxModelInstallState State { get; } = state;
}

namespace Everywhere.Media.Audio;

public interface IMicrophoneDeviceManager
{
    IReadOnlyList<MicrophoneDeviceDescriptor> GetInputDevices();

    string? GetDefaultInputDeviceId();

    IMicrophoneCapture CreateCapture(string? deviceId = null);
}

using Microsoft.Extensions.Logging;
using PortAudioSharp;

namespace Everywhere.Media.Audio;

public sealed class PortAudioMicrophoneDeviceManager(ILogger<PortAudioMicrophoneDeviceManager> logger) : IMicrophoneDeviceManager
{
    public IReadOnlyList<MicrophoneDeviceDescriptor> GetInputDevices()
    {
        PortAudioRuntime.EnsureInitialized();
        var defaultDevice = PortAudio.DefaultInputDevice;
        var devices = new List<MicrophoneDeviceDescriptor>();
        for (var index = 0; index < PortAudio.DeviceCount; index++)
        {
            DeviceInfo deviceInfo;
            try
            {
                deviceInfo = PortAudio.GetDeviceInfo(index);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to query PortAudio device {DeviceIndex}.", index);
                continue;
            }

            if (deviceInfo.maxInputChannels <= 0) continue;

            devices.Add(new MicrophoneDeviceDescriptor(
                index.ToString(),
                deviceInfo.name,
                deviceInfo.maxInputChannels,
                (int)Math.Round(deviceInfo.defaultSampleRate),
                index == defaultDevice));
        }

        return devices;
    }

    public string? GetDefaultInputDeviceId()
    {
        PortAudioRuntime.EnsureInitialized();
        var device = PortAudio.DefaultInputDevice;
        return device == PortAudio.NoDevice ? null : device.ToString();
    }

    public IMicrophoneCapture CreateCapture(string? deviceId = null) =>
        new PortAudioMicrophoneCapture(deviceId, logger);
}

internal static class PortAudioRuntime
{
    static PortAudioRuntime()
    {
        PortAudio.LoadNativeLibrary();
        PortAudio.Initialize();
    }

    public static void EnsureInitialized()
    {
        // Just ensure the static constructor has been called.
    }
}

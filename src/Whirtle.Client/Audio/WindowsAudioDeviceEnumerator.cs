// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace Whirtle.Client.Audio;

[SupportedOSPlatform("windows")]
internal sealed class WindowsAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();

        string? defaultInputId  = TryGetDefaultId(enumerator, DataFlow.Capture);
        string? defaultOutputId = TryGetDefaultId(enumerator, DataFlow.Render);

        var result = new List<AudioDeviceInfo>();

        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
        {
            using (device)
            {
                var kind      = device.DataFlow == DataFlow.Capture ? AudioDeviceKind.Input : AudioDeviceKind.Output;
                var isDefault = kind == AudioDeviceKind.Input
                    ? device.ID == defaultInputId
                    : device.ID == defaultOutputId;

                int maxSampleRate = 48_000;
                int maxBitDepth   = 24;
                int maxChannels   = 2;

                try
                {
                    var fmt       = device.AudioClient.MixFormat;
                    maxSampleRate = fmt.SampleRate;
                    maxChannels   = fmt.Channels;
                    maxBitDepth   = fmt.BitsPerSample;
                }
                catch (Exception)
                {
                    // Use defaults when the mix format cannot be queried.
                }

                result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, kind, isDefault,
                    maxSampleRate, maxBitDepth, maxChannels));
            }
        }

        return result;
    }

    private static string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, NAudio.CoreAudioApi.Role.Multimedia);
            return device.ID;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

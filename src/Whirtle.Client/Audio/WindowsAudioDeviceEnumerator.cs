// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

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

                result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, kind, isDefault));
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

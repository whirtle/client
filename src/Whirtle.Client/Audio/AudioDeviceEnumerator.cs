// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Audio;

/// <summary>
/// Creates the appropriate <see cref="IAudioDeviceEnumerator"/> for the
/// current operating system.
/// </summary>
public static class AudioDeviceEnumerator
{
    public static IAudioDeviceEnumerator Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsAudioDeviceEnumerator();

        return new NullAudioDeviceEnumerator();
    }
}

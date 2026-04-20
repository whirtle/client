// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Audio;

public interface IAudioDeviceEnumerator
{
    /// <summary>Returns all currently active audio devices.</summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices();

    /// <summary>Returns all active devices of a specific <paramref name="kind"/>.</summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceKind kind)
        => GetDevices().Where(d => d.Kind == kind).ToList();

    /// <summary>Returns the default device for <paramref name="kind"/>, or <c>null</c> if none.</summary>
    AudioDeviceInfo? GetDefault(AudioDeviceKind kind)
        => GetDevices(kind).FirstOrDefault(d => d.IsDefault)
        ?? GetDevices(kind).FirstOrDefault();
}

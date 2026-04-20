// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Audio;

/// <summary>
/// Fallback enumerator for platforms where audio device enumeration
/// is not yet supported. Always returns empty lists.
/// </summary>
internal sealed class NullAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> GetDevices() => [];
}

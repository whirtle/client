// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.Audio;

/// <summary>Describes a single audio endpoint discovered at enumeration time.</summary>
/// <param name="Id">Platform-assigned unique identifier.</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Kind">Whether this is a capture (input) or render (output) device.</param>
/// <param name="IsDefault">True when this is the system default for its kind.</param>
/// <param name="MaxSampleRate">Highest sample rate supported by the device (Hz).</param>
/// <param name="MaxBitDepth">Highest bit depth supported by the device (bits per sample).</param>
/// <param name="MaxChannels">Maximum channel count supported by the device.</param>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    AudioDeviceKind Kind,
    bool IsDefault,
    int MaxSampleRate = 48_000,
    int MaxBitDepth   = 24,
    int MaxChannels   = 2);

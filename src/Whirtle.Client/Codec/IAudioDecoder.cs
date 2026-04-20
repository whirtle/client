// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec;

/// <summary>Decodes encoded audio packets into <see cref="AudioFrame"/> instances.</summary>
public interface IAudioDecoder : IDisposable
{
    AudioFormat Format    { get; }
    int         SampleRate { get; }
    int         Channels   { get; }

    /// <summary>Decodes one encoded packet into an <see cref="AudioFrame"/>.</summary>
    AudioFrame Decode(ReadOnlyMemory<byte> data);
}

// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.UI.ViewModels;

/// <summary>
/// A manually-added server saved in settings.
/// <see cref="Label"/> is what the user typed and is shown in the server picker.
/// </summary>
public sealed record PersistedServer(
    string Label,
    string Host,
    int    Port = 8928,
    string Path = "/sendspin");

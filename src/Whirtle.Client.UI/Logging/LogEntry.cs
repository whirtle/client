// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.UI.Logging;

/// <summary>A single log line captured for the in-app log viewer.</summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string         Level,
    string         Message);

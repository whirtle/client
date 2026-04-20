// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Serilog.Core;
using Serilog.Events;

namespace Whirtle.Client.UI.Logging;

/// <summary>
/// Serilog sink that forwards log events to the in-app log viewer.
/// Register this with <see cref="AppLogger.Configure"/> before showing the UI.
/// </summary>
internal sealed class InMemorySink : ILogEventSink
{
    /// <summary>
    /// Raised on the thread that emitted the log event whenever a new entry arrives.
    /// Subscribers (typically the UI view-model) should marshal to the UI thread.
    /// </summary>
    public event Action<LogEntry>? NewEntry;

    void ILogEventSink.Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage();
        if (logEvent.Exception is { } ex)
            message += $": {ex.Message}";

        var entry = new LogEntry(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            message);

        NewEntry?.Invoke(entry);
    }
}

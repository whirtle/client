// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Whirtle.Client;

/// <summary>
/// Configures and owns the application-wide Serilog logger.
/// Call <see cref="Configure"/> once at startup before any logging occurs.
/// </summary>
public static class AppLogger
{
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:l}{NewLine}{Exception}";

    /// <summary>
    /// Initialises <see cref="Log.Logger"/> with a rolling file sink and an
    /// optional extra sink (e.g. an in-memory sink for the UI log viewer).
    /// </summary>
    /// <param name="extraSink">
    /// Additional sink to attach — typically the UI <c>InMemorySink</c>.
    /// Pass <c>null</c> for CLI / headless scenarios.
    /// </param>
    public static void Configure(ILogEventSink? extraSink = null)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Whirtle", "logs", "whirtle-.log");

        var config = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: OutputTemplate);

        if (extraSink is not null)
            config = config.WriteTo.Sink(extraSink, restrictedToMinimumLevel: LogEventLevel.Debug);

        Log.Logger = config.CreateLogger();
    }

    /// <summary>Flushes buffered writes and closes the log.</summary>
    public static void CloseAndFlush() => Log.CloseAndFlush();
}

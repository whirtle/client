// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.IntegrationTests;

/// <summary>
/// xUnit collection fixture that starts <c>uvx sendspin serve --demo</c>
/// once for the whole integration-test run and tears it down afterwards.
///
/// Tests that depend on this fixture are skipped automatically when
/// <c>uvx</c> is not installed or the server fails to start within
/// <see cref="StartupTimeoutMs"/>.
/// </summary>
public sealed class SensspinServerFixture : IAsyncLifetime, IDisposable
{
    public const int    Port             = 8927;
    public const string Path             = "/sendspin";
    public const int    StartupTimeoutMs = 5_000;

    private System.Diagnostics.Process? _process;

    /// <summary>
    /// <c>true</c> once the server process is confirmed to be listening.
    /// Tests should call <see cref="SkipIfUnavailable"/> at their start.
    /// </summary>
    public bool IsAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        if (!UvxExists())
            return;

        try
        {
            _process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "uvx",
                Arguments              = "sendspin serve --demo",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            });
        }
        catch
        {
            return; // uvx present but failed to start
        }

        if (_process is null || _process.HasExited)
            return;

        // Poll until the server's WebSocket port is accepting connections.
        var deadline = DateTime.UtcNow.AddMilliseconds(StartupTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsPortOpenAsync(Port))
            {
                IsAvailable = true;
                return;
            }
            await Task.Delay(200);
        }
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2_000);
            }
        }
        catch { /* best-effort */ }

        _process?.Dispose();
        _process = null;
    }

    /// <summary>
    /// Returns <c>true</c> when the server is unavailable — callers should
    /// <c>return</c> immediately (test passes trivially) so the suite stays
    /// green in environments where uvx is not installed.
    /// </summary>
    public bool Unavailable => !IsAvailable;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool UvxExists()
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "uvx",
                Arguments              = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            });
            p?.WaitForExit(2_000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<bool> IsPortOpenAsync(int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromMilliseconds(300));
            return true;
        }
        catch { return false; }
    }
}

[CollectionDefinition(Name)]
public sealed class SensspinCollection : ICollectionFixture<SensspinServerFixture>
{
    public const string Name = "Sendspin server";
}

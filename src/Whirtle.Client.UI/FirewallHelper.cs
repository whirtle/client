// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Whirtle.Client.UI;

internal static class FirewallHelper
{
    private const string RuleName = "Whirtle";

    /// <summary>
    /// Returns true if any inbound firewall rule named "Whirtle" exists —
    /// this covers both the Windows-auto-created rule (from the Security Alert)
    /// and any rule we created ourselves.
    /// </summary>
    internal static bool IsRulePresent()
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "netsh",
            Arguments              = $"advfirewall firewall show rule name=\"{RuleName}\" dir=in",
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;

            process.StandardOutput.ReadToEnd(); // drain
            process.WaitForExit();

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Spawns an elevated netsh process to add an inbound TCP allow rule
    /// scoped to this executable. Fire-and-forget; swallows UAC cancellation.
    /// </summary>
    internal static void AddRule(int port)
    {
        var exePath   = Environment.ProcessPath;
        var programArg = exePath is not null ? $" program=\"{exePath}\"" : "";

        var psi = new ProcessStartInfo
        {
            FileName        = "netsh",
            Arguments       = $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port}{programArg}",
            Verb            = "runas",
            UseShellExecute = true,
        };

        try
        {
            Process.Start(psi);
        }
        catch
        {
            // User cancelled UAC or elevation failed — silently ignore.
        }
    }
}

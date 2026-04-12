// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Whirtle.Client.UI;

internal static class FirewallHelper
{
    private static string RuleName(int port) => $"Whirtle {port}";

    internal static bool IsRulePresent(int port)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "netsh",
            Arguments              = $"advfirewall firewall show rule name=\"{RuleName(port)}\" dir=in",
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0 && output.Contains(port.ToString());
        }
        catch
        {
            return false;
        }
    }

    internal static void AddRule(int port)
    {
        var psi = new ProcessStartInfo
        {
            FileName        = "netsh",
            Arguments       = $"advfirewall firewall add rule name=\"{RuleName(port)}\" dir=in action=allow protocol=TCP localport={port}",
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

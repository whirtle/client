// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;

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
    /// Spawns an elevated PowerShell process that removes any existing rule with the
    /// same name before adding a fresh inbound TCP allow rule scoped to this executable.
    /// Using delete-then-add makes the call idempotent. Fire-and-forget; swallows UAC
    /// cancellation.
    /// </summary>
    internal static void AddRule(int port)
    {
        var exePath    = Environment.ProcessPath;
        var programArg = exePath is not null ? $" -Program \"{exePath}\"" : "";

        // Build a two-statement script: remove any pre-existing rule (idempotency),
        // then add a fresh one. The script is Base64-encoded to avoid shell-quoting issues.
        var script = $"Remove-NetFirewallRule -Name '{RuleName}' -ErrorAction SilentlyContinue; " +
                     $"New-NetFirewallRule -Name '{RuleName}' -DisplayName '{RuleName}' " +
                     $"-Direction Inbound -Action Allow -Protocol TCP -LocalPort {port}{programArg}";

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
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

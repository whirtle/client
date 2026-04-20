// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Whirtle.Client.UI.Logging;

/// <summary>Collects machine diagnostics for inclusion in exported log files.</summary>
internal static class SystemInfo
{
    public static string BuildHeader()
    {
        var osLine   = BuildOsLine();
        var arch     = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.X86   => "x86",
            Architecture.Arm64 => "arm64",
            var other          => other.ToString(),
        };
        var ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);

        const string Bar = "===================================";
        var sb = new StringBuilder();
        sb.AppendLine(Bar);
        sb.AppendLine("  Whirtle Log Export");
        sb.AppendLine(Bar);
        sb.AppendLine($"  Date:         {DateTime.Now:f}");
        sb.AppendLine($"  Machine:      {Environment.MachineName}");
        sb.AppendLine($"  OS:           {osLine}");
        sb.AppendLine($"  Architecture: {arch}");
        sb.AppendLine($"  .NET:         {Environment.Version}");
        sb.AppendLine($"  Processors:   {Environment.ProcessorCount}");
        sb.AppendLine($"  RAM:          {ramGb:0.#} GB");
        sb.AppendLine(Bar);
        return sb.ToString();
    }

    private static string BuildOsLine()
    {
        var product        = RegistryString("ProductName")    ?? "Windows";
        var buildNumber    = RegistryString("CurrentBuildNumber");
        var displayVersion = RegistryString("DisplayVersion"); // e.g. "24H2"

        if (buildNumber is null)
            return RuntimeInformation.OSDescription;

        var suffix = displayVersion is not null
            ? $" {displayVersion} (Build {buildNumber})"
            : $" (Build {buildNumber})";
        return product + suffix;
    }

    private static string? RegistryString(string valueName)
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                valueName, null) as string;
        }
        catch
        {
            return null;
        }
    }
}

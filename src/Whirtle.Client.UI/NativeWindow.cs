// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Whirtle.Client.UI;

internal static class NativeWindow
{
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    internal const int SW_HIDE    = 0;
    internal const int SW_RESTORE = 9;
}

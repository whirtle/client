// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace Whirtle.Client.UI;

internal static class NativeWindow
{
    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    internal const int SW_HIDE    = 0;
    internal const int SW_RESTORE = 9;
}

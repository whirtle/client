// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

// Members bound from XAML (x:Bind expressions and event handler attributes)
// must remain instance members even when they do not touch instance state.
// x:Bind looks them up on the data context / page instance; making them
// static would require rewriting every XAML reference.
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static",
    Scope = "member", Target = "~P:Whirtle.Client.UI.MainWindow.NowPlayingViewModel")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static",
    Scope = "member", Target = "~P:Whirtle.Client.UI.MainWindow.UiStateService")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static",
    Scope = "member", Target = "~M:Whirtle.Client.UI.MainWindow.StatusBarVisibility(Whirtle.Client.State.AppUiState)~Microsoft.UI.Xaml.Visibility")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static",
    Scope = "member", Target = "~M:Whirtle.Client.UI.MainWindow.FreScrimVisibility(Whirtle.Client.State.AppUiState)~Microsoft.UI.Xaml.Visibility")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static",
    Scope = "member", Target = "~M:Whirtle.Client.UI.MainWindow.StatsAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator,Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs)")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static",
    Scope = "member", Target = "~P:Whirtle.Client.UI.Pages.NowPlayingPage.ViewModel")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static",
    Scope = "member", Target = "~M:Whirtle.Client.UI.Pages.NowPlayingPage.WaitingVisibility(System.Boolean)~Microsoft.UI.Xaml.Visibility")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static",
    Scope = "member", Target = "~P:Whirtle.Client.UI.Pages.LogsPage.ViewModel")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static",
    Scope = "member", Target = "~P:Whirtle.Client.UI.Pages.StatsPage.ClockStats")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static",
    Scope = "member", Target = "~P:Whirtle.Client.UI.Pages.StatsPage.PlaybackStats")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static",
    Scope = "member", Target = "~P:Whirtle.Client.UI.Pages.SettingsPage.ViewModel")]

// Window subclasses hold MicaController (IDisposable) for the lifetime of the
// window; cleanup happens in the Closed / AppWindow.Closing event handler
// rather than via IDisposable because WinUI Window is not IDisposable and
// users do not call Dispose on windows. Same pattern for App (NetworkMonitor
// released on MainWindow.Closed) and NowPlayingViewModel (disposed via
// ShutdownAsync before app exit).
[assembly: SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Scope = "type", Target = "~T:Whirtle.Client.UI.MainWindow",
    Justification = "Cleanup runs in AppWindow.Closing before Application.Exit.")]
[assembly: SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Scope = "type", Target = "~T:Whirtle.Client.UI.SettingsWindow",
    Justification = "MicaController disposed in the Window.Closed handler.")]
[assembly: SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Scope = "type", Target = "~T:Whirtle.Client.UI.LogsWindow",
    Justification = "MicaController lifetime matches the window; cleaned up in Closed handler.")]
[assembly: SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Scope = "type", Target = "~T:Whirtle.Client.UI.StatsWindow",
    Justification = "MicaController lifetime matches the window; cleaned up in Closed handler.")]
[assembly: SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Scope = "type", Target = "~T:Whirtle.Client.UI.App",
    Justification = "NetworkMonitor disposed in MainWindow.Closed before shutdown.")]
[assembly: SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Scope = "type", Target = "~T:Whirtle.Client.UI.ViewModels.NowPlayingViewModel",
    Justification = "Resources released via ShutdownAsync before the process exits.")]

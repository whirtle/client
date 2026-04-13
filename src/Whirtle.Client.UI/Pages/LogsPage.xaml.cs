// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Whirtle.Client.UI.Logging;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI.Pages;

public sealed partial class LogsPage : Page
{
    private LogsViewModel ViewModel => App.Current.LogsViewModel;

    public LogsPage()
    {
        InitializeComponent();

        // Auto-scroll when a new entry is added
        ViewModel.Entries.CollectionChanged += (_, _) =>
        {
            if (ViewModel.Entries.Count > 0)
                LogList.ScrollIntoView(ViewModel.Entries[^1]);
        };
    }

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CopySelectedButton.IsEnabled = LogList.SelectedItems.Count > 0;
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var lines = LogList.SelectedItems
            .OfType<LogEntry>()
            .Select(entry => entry.FormattedLine);
        SetClipboardText(string.Join('\n', lines));
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var log = string.Join('\n', ViewModel.Entries.Select(entry => entry.FormattedLine));
        SetClipboardText(SystemInfo.BuildHeader() + '\n' + log);
    }

    private async void SaveToFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, NativeWindow.GetForegroundWindow());

        picker.FileTypeChoices.Add("Text file", [".txt"]);
        picker.SuggestedFileName = $"whirtle-logs-{DateTime.Now:yyyyMMdd-HHmmss}";

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var log = string.Join('\n', ViewModel.Entries.Select(entry => entry.FormattedLine));
        await FileIO.WriteTextAsync(file, SystemInfo.BuildHeader() + '\n' + log);
    }

    private static void SetClipboardText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}

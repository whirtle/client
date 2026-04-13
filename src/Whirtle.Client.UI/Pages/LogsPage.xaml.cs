// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Whirtle.Client.UI.Logging;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI.Pages;

public sealed partial class LogsPage : Page
{
    private LogsViewModel ViewModel => App.Current.LogsViewModel;
    private ScrollViewer? _scrollViewer;

    public LogsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scrollViewer = FindScrollViewer(LogList);
        LogList.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(LogList_PointerWheelChanged),
            handledEventsToo: true);
        ViewModel.Entries.CollectionChanged += OnEntriesChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Entries.CollectionChanged -= OnEntriesChanged;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _scrollViewer is null)
            return;

        var atBottom = _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 1.0;
        if (!atBottom)
            return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ViewModel.Entries.Count > 0)
                LogList.ScrollIntoView(ViewModel.Entries[^1]);
        });
    }

    private void LogList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_scrollViewer is null) return;

        var delta = e.GetCurrentPoint(LogList).Properties.MouseWheelDelta;
        _scrollViewer.ChangeView(null, _scrollViewer.VerticalOffset - delta, null, disableAnimation: true);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer sv)
                return sv;
            var result = FindScrollViewer(child);
            if (result != null)
                return result;
        }
        return null;
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
        picker.SettingsIdentifier = "LogSave";

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

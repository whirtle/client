// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Whirtle.Client.UI.Logging;

namespace Whirtle.Client.UI.ViewModels;

public sealed partial class LogsViewModel : ObservableObject
{
    private const int MaxEntries = 10_000;

    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    internal LogsViewModel(InMemorySink sink, DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        sink.NewEntry += OnNewEntry;
    }

    private void OnNewEntry(LogEntry entry)
    {
        _dispatcher.TryEnqueue(() =>
        {
            Entries.Add(entry);
            if (Entries.Count > MaxEntries)
                Entries.RemoveAt(0);
        });
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();
}

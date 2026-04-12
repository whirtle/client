// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Microsoft.UI.Xaml.Controls;
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
}

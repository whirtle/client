using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Whirtle.Client.State;

/// <summary>
/// Owns the global UI state and derives it from two inputs: whether the user
/// has accepted the terms, and whether an active server session exists.
///
/// Callers update state by calling <see cref="Update"/> whenever either input
/// changes. This keeps the service free of WinUI/ViewModel dependencies and
/// makes it straightforward to test from the platform-neutral test project.
/// </summary>
public sealed class AppUiStateService : INotifyPropertyChanged
{
    private AppUiState _currentState;

    public AppUiState CurrentState
    {
        get => _currentState;
        private set
        {
            if (_currentState == value) return;
            _currentState = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public AppUiStateService(bool termsAccepted, bool isConnected)
    {
        _currentState = Compute(termsAccepted, isConnected);
    }

    /// <summary>
    /// Recalculates <see cref="CurrentState"/> from the latest inputs.
    /// Call this whenever <c>TermsAccepted</c> or <c>IsConnected</c> changes.
    /// </summary>
    public void Update(bool termsAccepted, bool isConnected)
    {
        CurrentState = Compute(termsAccepted, isConnected);
    }

    private static AppUiState Compute(bool termsAccepted, bool isConnected)
    {
        if (!termsAccepted)
            return AppUiState.FirstRun;

        // TODO: gate Playing→Waiting transition on buffer drain once
        // PlaybackEngine is wired into the UI layer. For now, transition
        // immediately when the connection drops.
        return isConnected ? AppUiState.Playing : AppUiState.Waiting;
    }
}

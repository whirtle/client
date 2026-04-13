using System.ComponentModel;
using Whirtle.Client.State;

namespace Whirtle.Client.Tests.State;

public class AppUiStateServiceTests
{
    // ── Initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void InitialState_IsFirstRun_WhenTermsNotAccepted()
    {
        var svc = new AppUiStateService(termsAccepted: false, isConnected: false);
        Assert.Equal(AppUiState.FirstRun, svc.CurrentState);
    }

    [Fact]
    public void InitialState_IsFirstRun_WhenTermsNotAccepted_EvenIfConnected()
    {
        var svc = new AppUiStateService(termsAccepted: false, isConnected: true);
        Assert.Equal(AppUiState.FirstRun, svc.CurrentState);
    }

    [Fact]
    public void InitialState_IsWaiting_WhenTermsAccepted_AndNotConnected()
    {
        var svc = new AppUiStateService(termsAccepted: true, isConnected: false);
        Assert.Equal(AppUiState.Waiting, svc.CurrentState);
    }

    [Fact]
    public void InitialState_IsPlaying_WhenTermsAccepted_AndConnected()
    {
        var svc = new AppUiStateService(termsAccepted: true, isConnected: true);
        Assert.Equal(AppUiState.Playing, svc.CurrentState);
    }

    // ── FirstRun transitions ──────────────────────────────────────────────────

    [Fact]
    public void Update_TransitionsFirstRunToWaiting_WhenTermsAccepted()
    {
        var svc = new AppUiStateService(termsAccepted: false, isConnected: false);
        svc.Update(termsAccepted: true, isConnected: false);
        Assert.Equal(AppUiState.Waiting, svc.CurrentState);
    }

    [Fact]
    public void Update_NoTransitionFromFirstRun_WhenOnlyIsConnectedBecomesTrue()
    {
        var svc = new AppUiStateService(termsAccepted: false, isConnected: false);
        svc.Update(termsAccepted: false, isConnected: true);
        Assert.Equal(AppUiState.FirstRun, svc.CurrentState);
    }

    // ── Waiting / Playing transitions ─────────────────────────────────────────

    [Fact]
    public void Update_TransitionsWaitingToPlaying_WhenConnected()
    {
        var svc = new AppUiStateService(termsAccepted: true, isConnected: false);
        svc.Update(termsAccepted: true, isConnected: true);
        Assert.Equal(AppUiState.Playing, svc.CurrentState);
    }

    [Fact]
    public void Update_TransitionsPlayingToWaiting_WhenDisconnected()
    {
        var svc = new AppUiStateService(termsAccepted: true, isConnected: true);
        svc.Update(termsAccepted: true, isConnected: false);
        Assert.Equal(AppUiState.Waiting, svc.CurrentState);
    }

    // ── PropertyChanged ───────────────────────────────────────────────────────

    [Fact]
    public void Update_RaisesPropertyChanged_OnTransition()
    {
        var svc = new AppUiStateService(termsAccepted: false, isConnected: false);
        var raised = new List<string?>();
        svc.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        svc.Update(termsAccepted: true, isConnected: false);

        Assert.Contains(nameof(AppUiStateService.CurrentState), raised);
    }

    [Fact]
    public void Update_DoesNotRaisePropertyChanged_WhenStateUnchanged()
    {
        var svc = new AppUiStateService(termsAccepted: true, isConnected: false);
        var raised = false;
        svc.PropertyChanged += (_, _) => raised = true;

        svc.Update(termsAccepted: true, isConnected: false);

        Assert.False(raised);
    }
}

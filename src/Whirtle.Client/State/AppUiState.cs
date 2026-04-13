namespace Whirtle.Client.State;

public enum AppUiState
{
    /// <summary>
    /// First-run experience: the user must accept the license before proceeding.
    /// No networking is active in this state.
    /// </summary>
    FirstRun,

    /// <summary>
    /// No active session. Covers idle, mDNS listening, and the connection
    /// handshake period. An informational status message is shown.
    /// Volume and mute controls are available.
    /// </summary>
    Waiting,

    /// <summary>
    /// An active session exists. Includes paused-with-buffered-content.
    /// All playback controls are available.
    /// </summary>
    Playing,
}

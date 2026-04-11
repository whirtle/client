namespace Whirtle.Client.Audio;

/// <summary>Describes a single audio endpoint discovered at enumeration time.</summary>
/// <param name="Id">Platform-assigned unique identifier.</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Kind">Whether this is a capture (input) or render (output) device.</param>
/// <param name="IsDefault">True when this is the system default for its kind.</param>
public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceKind Kind, bool IsDefault);

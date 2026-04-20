// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.UI.ViewModels;

/// <summary>
/// A server that has successfully completed a handshake with this client in the
/// current session (whether the connection was ultimately accepted or rejected).
/// Used to populate the server picker in server-initiated mode so the user can
/// pin to a specific server.
/// </summary>
public sealed record KnownServer(string ServerId, string Name);

// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Protocol;

public sealed class HandshakeException : Exception
{
    public string Code { get; }

    public HandshakeException() : base() { Code = string.Empty; }

    public HandshakeException(string message) : base(message) { Code = string.Empty; }

    public HandshakeException(string message, Exception innerException) : base(message, innerException) { Code = string.Empty; }

    public HandshakeException(string code, string message) : base(message)
    {
        Code = code;
    }
}

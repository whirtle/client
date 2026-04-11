// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.Protocol;

public sealed class HandshakeException : Exception
{
    public string Code { get; }

    public HandshakeException(string code, string message) : base(message)
    {
        Code = code;
    }
}

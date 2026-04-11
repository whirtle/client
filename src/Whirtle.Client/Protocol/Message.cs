using System.Text.Json.Serialization;

namespace Whirtle.Client.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage),   "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(PingMessage),    "ping")]
[JsonDerivedType(typeof(PongMessage),    "pong")]
[JsonDerivedType(typeof(ErrorMessage),   "error")]
[JsonDerivedType(typeof(GoodbyeMessage), "goodbye")]
public abstract record Message;

/// <summary>Sent by the client immediately after the WebSocket connection opens.</summary>
public sealed record HelloMessage(string Version) : Message;

/// <summary>Sent by the server to accept the handshake.</summary>
public sealed record WelcomeMessage(string SessionId, string ServerVersion) : Message;

/// <summary>Keepalive probe; the receiver should reply with <see cref="PongMessage"/>.</summary>
public sealed record PingMessage : Message;

/// <summary>Keepalive reply to a <see cref="PingMessage"/>.</summary>
public sealed record PongMessage : Message;

/// <summary>Sent by either side to report a protocol-level error.</summary>
public sealed record ErrorMessage(string Code, string Description) : Message;

/// <summary>Sent by either side to begin a graceful shutdown.</summary>
public sealed record GoodbyeMessage(string Reason) : Message;

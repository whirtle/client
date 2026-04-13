// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whirtle.Client.Protocol;

/// <summary>
/// Serialises and deserialises Sendspin protocol messages.
///
/// Wire format
/// ───────────
/// All messages use a typed envelope:
/// <code>{"type":"client/hello","payload":{…}}</code>
/// Payload field names follow <c>snake_case</c> (e.g. <c>client_id</c>,
/// <c>server_id</c>, <c>connection_reason</c>).
/// </summary>
internal sealed class MessageSerializer
{
    // snake_case serialisation for payload objects.
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    // Maps wire type strings → CLR types (for deserialisation).
    private static readonly Dictionary<string, Type> TypeMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["client/hello"]   = typeof(ClientHelloMessage),
        ["client/time"]    = typeof(ClientTimeMessage),
        ["client/state"]   = typeof(ClientStateMessage),
        ["client/command"] = typeof(ClientCommandMessage),
        ["client/goodbye"] = typeof(ClientGoodbyeMessage),
        ["server/hello"]          = typeof(ServerHelloMessage),
        ["server/time"]           = typeof(ServerTimeMessage),
        ["server/state"]          = typeof(ServerStateMessage),
        ["server/command"]        = typeof(ServerCommandMessage),
        ["group/update"]          = typeof(GroupUpdateMessage),
        ["stream/request-format"] = typeof(StreamRequestFormatMessage),
        ["stream/start"]          = typeof(StreamStartMessage),
        ["stream/clear"]          = typeof(StreamClearMessage),
        ["stream/end"]            = typeof(StreamEndMessage),
    };

    // Maps CLR types → wire type strings (for serialisation).
    private static readonly Dictionary<Type, string> NameMap =
        TypeMap.ToDictionary(kv => kv.Value, kv => kv.Key);

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>Returns the wire type string for <paramref name="message"/>, or the CLR type name if unknown.</summary>
    public string GetWireType(Message message) =>
        NameMap.TryGetValue(message.GetType(), out var name) ? name : message.GetType().Name;

    /// <summary>Serialises <paramref name="message"/> to UTF-8 JSON bytes.</summary>
    public byte[] Serialize(Message message)
    {
        if (!NameMap.TryGetValue(message.GetType(), out var typeName))
            throw new InvalidOperationException(
                $"Cannot serialise unknown message type '{message.GetType().Name}'.");

        using var ms     = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartObject();
        writer.WriteString("type", typeName);
        writer.WritePropertyName("payload");
        JsonSerializer.Serialize(writer, message, message.GetType(), PayloadOptions);
        writer.WriteEndObject();

        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Deserialises UTF-8 JSON bytes to a <see cref="Message"/> instance.
    /// Returns <see cref="UnknownMessage"/> for unrecognised type values rather
    /// than throwing, so callers can ignore future protocol extensions gracefully.
    /// </summary>
    public Message Deserialize(byte[] data)
    {
        using var doc  = JsonDocument.Parse(data);
        var        root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeEl))
            throw new InvalidOperationException("Received message is missing the 'type' field.");

        var typeName = typeEl.GetString() ?? string.Empty;

        if (!TypeMap.TryGetValue(typeName, out var clrType))
            return new UnknownMessage(typeName);

        if (!root.TryGetProperty("payload", out var payload))
            throw new InvalidOperationException(
                $"Received '{typeName}' message is missing the 'payload' field.");

        return (Message)JsonSerializer.Deserialize(payload.GetRawText(), clrType, PayloadOptions)!;
    }
}

// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

// CA2225 requires static factory alternates for implicit operators, but CA1000 bans statics
// on generic types — the two rules are mutually exclusive here.
#pragma warning disable CA1000, CA2225

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whirtle.Client.Protocol;

/// <summary>
/// Represents a protocol field that may be absent from the wire message, explicitly null,
/// or set to a concrete value — three distinct states that cannot be collapsed.
/// </summary>
/// <remarks>
/// The Sendspin <c>server/state</c> spec allows partial updates: absent fields must be
/// retained on the client, while fields explicitly set to null are cleared.
/// <see cref="IsSet"/> is false only for absent fields; it is true for both null and
/// concrete-value fields.
/// </remarks>
public readonly struct PartialField<T> : IEquatable<PartialField<T>>
{
    public bool IsSet { get; }
    public T    Value { get; }

    public PartialField(T value) { IsSet = true; Value = value; }

    public static PartialField<T> From(T value)  => new(value);
    public T                      ToValue()       => Value;

    public bool Equals(PartialField<T> other) =>
        IsSet == other.IsSet && EqualityComparer<T>.Default.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is PartialField<T> o && Equals(o);
    public override int  GetHashCode()        => HashCode.Combine(IsSet, Value);

    public static bool operator ==(PartialField<T> left, PartialField<T> right) => left.Equals(right);
    public static bool operator !=(PartialField<T> left, PartialField<T> right) => !left.Equals(right);

    public static implicit operator PartialField<T>(T value) => new(value);
    public static implicit operator T(PartialField<T> f)     => f.Value;
}

internal sealed class PartialFieldJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(PartialField<>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var innerType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(
            typeof(PartialFieldJsonConverter<>).MakeGenericType(innerType))!;
    }
}

internal sealed class PartialFieldJsonConverter<T> : JsonConverter<PartialField<T>>
{
    public override PartialField<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new PartialField<T>(default!);

        return new PartialField<T>(JsonSerializer.Deserialize<T>(ref reader, options)!);
    }

    public override void Write(Utf8JsonWriter writer, PartialField<T> value, JsonSerializerOptions options)
    {
        if (value.Value is null)
            writer.WriteNullValue();
        else
            JsonSerializer.Serialize(writer, value.Value, options);
    }
}

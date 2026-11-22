using System.Text.Json;
using System.Text.Json.Serialization;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Web.Converters;

public sealed class MessageIdJsonConverter : JsonConverter<MessageId>
{
    public override MessageId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            return new MessageId(reader.GetInt64());
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str)) return null;
            return MessageId.Parse(str);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("value", out var valProp))
            {
                if (valProp.ValueKind == JsonValueKind.Number)
                    return new MessageId(valProp.GetInt64());
                if (valProp.ValueKind == JsonValueKind.String)
                    return MessageId.Parse(valProp.GetString()!);
            }
        }

        throw new JsonException($"Cannot convert token of type {reader.TokenType} to MessageId.");
    }

    public override void Write(Utf8JsonWriter writer, MessageId value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Value);
    }
}

public sealed class UserIdJsonConverter : JsonConverter<UserId>
{
    public override UserId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str)) return null;
            return UserId.Parse(str);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("value", out var valProp))
            {
                if (valProp.ValueKind == JsonValueKind.String)
                    return UserId.Parse(valProp.GetString()!);
            }
        }

        throw new JsonException($"Cannot convert token of type {reader.TokenType} to UserId.");
    }

    public override void Write(Utf8JsonWriter writer, UserId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

public sealed class ConversationIdJsonConverter : JsonConverter<ConversationId>
{
    public override ConversationId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str)) return null;
            return ConversationId.Parse(str);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("value", out var valProp))
            {
                if (valProp.ValueKind == JsonValueKind.String)
                    return ConversationId.Parse(valProp.GetString()!);
            }
        }

        throw new JsonException($"Cannot convert token of type {reader.TokenType} to ConversationId.");
    }

    public override void Write(Utf8JsonWriter writer, ConversationId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

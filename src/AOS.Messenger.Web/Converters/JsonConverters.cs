using System.Globalization;
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
            if (!reader.TryGetInt64(out var id) || id <= 0)
            {
                throw new JsonException("Invalid MessageId value.");
            }

            return new MessageId(id);
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (!long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                throw new JsonException("Invalid MessageId value.");
            }

            return new MessageId(id);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("value", out var valProp))
            {
                if (valProp.ValueKind == JsonValueKind.Number
                    && valProp.TryGetInt64(out var numId)
                    && numId > 0)
                {
                    return new MessageId(numId);
                }

                if (valProp.ValueKind == JsonValueKind.String
                    && long.TryParse(valProp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var strId)
                    && strId > 0)
                {
                    return new MessageId(strId);
                }
            }

            throw new JsonException("Invalid MessageId object value.");
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
            if (!UserId.TryParse(str, out var userId))
            {
                throw new JsonException("Invalid UserId value.");
            }

            return userId;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("value", out var valProp)
                && valProp.ValueKind == JsonValueKind.String
                && UserId.TryParse(valProp.GetString(), out var userId))
            {
                return userId;
            }

            throw new JsonException("Invalid UserId object value.");
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
            if (!ConversationId.TryParse(str, out var conversationId))
            {
                throw new JsonException("Invalid ConversationId value.");
            }

            return conversationId;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("value", out var valProp)
                && valProp.ValueKind == JsonValueKind.String
                && ConversationId.TryParse(valProp.GetString(), out var conversationId))
            {
                return conversationId;
            }

            throw new JsonException("Invalid ConversationId object value.");
        }

        throw new JsonException($"Cannot convert token of type {reader.TokenType} to ConversationId.");
    }

    public override void Write(Utf8JsonWriter writer, ConversationId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

public sealed class MediaIdJsonConverter : JsonConverter<MediaId>
{
    public override MediaId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (!MediaId.TryParse(str, out var mediaId))
            {
                throw new JsonException("Invalid MediaId value.");
            }

            return mediaId;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("value", out var valProp)
                && valProp.ValueKind == JsonValueKind.String
                && MediaId.TryParse(valProp.GetString(), out var mediaId))
            {
                return mediaId;
            }

            throw new JsonException("Invalid MediaId object value.");
        }

        throw new JsonException($"Cannot convert token of type {reader.TokenType} to MediaId.");
    }

    public override void Write(Utf8JsonWriter writer, MediaId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

public sealed class AvatarUploadIdJsonConverter : JsonConverter<AvatarUploadId>
{
    public override AvatarUploadId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (!AvatarUploadId.TryParse(str, out var uploadId))
            {
                throw new JsonException("Invalid AvatarUploadId value.");
            }

            return uploadId;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("value", out var valProp)
                && valProp.ValueKind == JsonValueKind.String
                && AvatarUploadId.TryParse(valProp.GetString(), out var uploadId))
            {
                return uploadId;
            }

            throw new JsonException("Invalid AvatarUploadId object value.");
        }

        throw new JsonException($"Cannot convert token of type {reader.TokenType} to AvatarUploadId.");
    }

    public override void Write(Utf8JsonWriter writer, AvatarUploadId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

public sealed class StickerPackIdJsonConverter : JsonConverter<StickerPackId>
{
    public override StickerPackId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (!StickerPackId.TryParse(str, out var packId))
            {
                throw new JsonException("Invalid StickerPackId value.");
            }

            return packId;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("value", out var valProp)
                && valProp.ValueKind == JsonValueKind.String
                && StickerPackId.TryParse(valProp.GetString(), out var packId))
            {
                return packId;
            }

            throw new JsonException("Invalid StickerPackId object value.");
        }

        throw new JsonException($"Cannot convert token of type {reader.TokenType} to StickerPackId.");
    }

    public override void Write(Utf8JsonWriter writer, StickerPackId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

public sealed class StickerIdJsonConverter : JsonConverter<StickerId>
{
    public override StickerId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (!StickerId.TryParse(str, out var stickerId))
            {
                throw new JsonException("Invalid StickerId value.");
            }

            return stickerId;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("value", out var valProp)
                && valProp.ValueKind == JsonValueKind.String
                && StickerId.TryParse(valProp.GetString(), out var stickerId))
            {
                return stickerId;
            }

            throw new JsonException("Invalid StickerId object value.");
        }

        throw new JsonException($"Cannot convert token of type {reader.TokenType} to StickerId.");
    }

    public override void Write(Utf8JsonWriter writer, StickerId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

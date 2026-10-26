using System.Text.Json;

namespace GangioKeet.Client.Protocol;

public sealed class ProtocolFrameException : Exception
{
    public ProtocolFrameException(string message)
        : base(message)
    {
    }

    public ProtocolFrameException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class ProtocolJson
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static string SerializeStart(StartMessage message)
    {
        return JsonSerializer.Serialize(message, WireOptions);
    }

    public static ServerFrame ParseServerFrame(string json)
    {
        string? type;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                type = null;
            }
            else
            {
                type = typeElement.GetString();
            }
        }
        catch (JsonException ex)
        {
            throw new ProtocolFrameException("Malformed JSON frame.", ex);
        }

        return type switch
        {
            "result" => new ResultFrame(ParseResultMessage(json)),
            "error" => ParseError(json),
            null => throw new ProtocolFrameException("Server frame is missing its 'type' member."),
            _ => throw new ProtocolFrameException($"Unknown server frame type '{type}'."),
        };
    }

    private static ResultMessage ParseResultMessage(string json)
    {
        ResultMessage? result;
        try
        {
            result = JsonSerializer.Deserialize<ResultMessage>(json, WireOptions);
        }
        catch (JsonException ex)
        {
            throw new ProtocolFrameException("Malformed result frame.", ex);
        }

        if (result is null || result.RawText is null || result.FinalText is null || result.Metrics is null)
        {
            throw new ProtocolFrameException("Malformed result frame: required members are missing.");
        }

        return result.Warnings is null ? result with { Warnings = [] } : result;
    }

    private static ErrorFrame ParseError(string json)
    {
        ErrorFrame? error;
        try
        {
            error = JsonSerializer.Deserialize<ErrorFrame>(json, WireOptions);
        }
        catch (JsonException ex)
        {
            throw new ProtocolFrameException("Malformed error frame.", ex);
        }

        if (error is null || error.Code is null || error.Message is null)
        {
            throw new ProtocolFrameException("Malformed error frame: 'code' and 'message' are required.");
        }

        return error;
    }
}

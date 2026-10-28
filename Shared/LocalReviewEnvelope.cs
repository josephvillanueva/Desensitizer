using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Desensitizer;

// Validate transport envelopes separately from the entity JSON. Malformed server
// replies enter the existing bounded retry policy, never become an empty review.
public static class LocalReviewEnvelope
{
    public static string ReadChat(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = Object(document.RootElement);
            var choices = Required(root, "choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1) throw Invalid();
            var choice = Object(choices[0]);
            if (choice.TryGetProperty("finish_reason", out var reason))
            {
                if (reason.ValueKind != JsonValueKind.String) throw Invalid();
                if (reason.GetString() == "length") throw Truncated();
                if (reason.GetString() != "stop") throw Invalid();
            }
            var message = Object(Required(choice, "message"));
            var content = Required(message, "content");
            if (content.ValueKind != JsonValueKind.String) throw Invalid();
            return content.GetString()!;
        }
        catch (JsonException) { throw Invalid(); }
    }

    public static string ReadCompletion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = Object(document.RootElement);
            if (root.TryGetProperty("truncated", out var truncated))
            {
                if (truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
                if (truncated.ValueKind == JsonValueKind.True) throw Truncated();
            }
            if (root.TryGetProperty("stop_type", out var stop))
            {
                if (stop.ValueKind != JsonValueKind.String) throw Invalid();
                if (stop.GetString() == "limit") throw Truncated();
            }
            var content = Required(root, "content");
            if (content.ValueKind != JsonValueKind.String) throw Invalid();
            return content.GetString()!;
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static JsonElement Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw Invalid();
        return value;
    }

    private static JsonElement Required(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) ? property : throw Invalid();

    // Do not retain payload, parser details, InnerException or Exception.Data.
    private static InvalidDataException Invalid()
        => new("The local AI response was invalid or incomplete. The scan is incomplete.");
    private static InvalidDataException Truncated()
        => new("The local AI response was cut off. The scan is incomplete.");
}

using System.Text.Json;

namespace SelectAndRead;

/// <summary>
/// The OpenAI Realtime wire format: builds the client frames and parses the server ones.
///
/// Deliberately pure - no sockets, no Windows APIs, and no dependency on <see cref="Config"/>
/// (which touches the registry and so cannot be compiled for a plain net10.0 test assembly).
/// Everything it needs arrives as parameters. Same arrangement as <see cref="TextCleaner"/>,
/// and for the same reason: it makes the fiddly part testable on a machine that cannot run
/// the app.
/// </summary>
internal static class RealtimeProtocol
{
    /// <summary>
    /// The only sample format the app asks for. Realtime emits signed 16-bit little-endian
    /// mono at this rate; the player is configured to match and neither side resamples.
    /// </summary>
    internal const int SampleRate = 24000;

    internal const int Channels = 1;
    internal const int BitsPerSample = 16;

    internal static Uri EndpointFor(string model) =>
        new($"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(model)}");

    // --- Client frames ----------------------------------------------------------

    /// <summary>
    /// Configures the session for one-shot reading: audio out only, PCM at
    /// <see cref="SampleRate"/>, and no turn detection - the app never sends microphone
    /// audio, and leaving server VAD on makes the model wait for speech that never comes.
    /// </summary>
    internal static string BuildSessionUpdate(string voice, string instructions) =>
        JsonSerializer.Serialize(new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                output_modalities = new[] { "audio" },
                instructions,
                audio = new
                {
                    input = new { turn_detection = (object?)null },
                    output = new
                    {
                        voice,
                        format = new { type = "audio/pcm", rate = SampleRate },
                    },
                },
            },
        });

    /// <summary>
    /// The crop plus the reading prompt, as a single user message.
    ///
    /// The image goes first: the prompt reads as an instruction about the image that
    /// precedes it, which is the ordering the vision models are trained on.
    /// </summary>
    internal static string BuildImageItem(byte[] png, string prompt) =>
        JsonSerializer.Serialize(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "input_image",
                        image_url = "data:image/png;base64," + Convert.ToBase64String(png),
                        detail = "high",
                    },
                    new { type = "input_text", text = prompt },
                },
            },
        });

    internal static string BuildResponseCreate() =>
        JsonSerializer.Serialize(new
        {
            type = "response.create",
            response = new { output_modalities = new[] { "audio" } },
        });

    // --- Server frames ----------------------------------------------------------

    /// <summary>Token counts from <c>response.done</c>, for the --read-file cost report.</summary>
    internal sealed record Usage(
        int InputText,
        int InputImage,
        int InputCached,
        int OutputText,
        int OutputAudio)
    {
        internal int Total => InputText + InputImage + OutputText + OutputAudio;
    }

    internal abstract record Event
    {
        /// <summary>A chunk of PCM, already base64-decoded.</summary>
        internal sealed record Audio(byte[] Pcm) : Event;

        /// <summary>A chunk of the spoken text, accumulated for the clipboard.</summary>
        internal sealed record Transcript(string Text) : Event;

        /// <summary>The response finished normally. Usage is null if the server omitted it.</summary>
        internal sealed record Completed(Usage? Usage) : Event;

        /// <summary>A server error, or a response that ended in a non-completed state.</summary>
        internal sealed record Failed(string Message) : Event;

        /// <summary>Anything this client does not act on. Never an error.</summary>
        internal sealed record Ignored : Event;
    }

    private static readonly Event.Ignored IgnoredInstance = new();

    /// <summary>
    /// Parses one server frame. Never throws: an unrecognised event type is
    /// <see cref="Event.Ignored"/> (the Realtime API emits many events this client has no
    /// use for, and new ones appear over time), while a frame that is not usable JSON at
    /// all is <see cref="Event.Failed"/> - that indicates a genuine problem rather than a
    /// forward-compatible addition.
    /// </summary>
    internal static Event Parse(string frame)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame);
        }
        catch (JsonException ex)
        {
            return new Event.Failed($"Malformed frame from the server: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return IgnoredInstance;
            if (!root.TryGetProperty("type", out var typeElement)) return IgnoredInstance;
            if (typeElement.ValueKind != JsonValueKind.String) return IgnoredInstance;

            return typeElement.GetString() switch
            {
                "response.output_audio.delta" => ParseAudio(root),
                "response.output_audio_transcript.delta" => ParseTranscript(root),
                "response.done" => ParseDone(root),
                "error" => new Event.Failed(ErrorMessage(root, "error")),
                _ => IgnoredInstance,
            };
        }
    }

    private static Event ParseAudio(JsonElement root)
    {
        if (!TryGetString(root, "delta", out var base64)) return IgnoredInstance;

        try
        {
            return new Event.Audio(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return new Event.Failed("Audio chunk was not valid base64.");
        }
    }

    private static Event ParseTranscript(JsonElement root) =>
        TryGetString(root, "delta", out var text) && text.Length > 0
            ? new Event.Transcript(text)
            : IgnoredInstance;

    /// <summary>
    /// A <c>response.done</c> is not automatically a success - a response cut short by a
    /// content filter or an incomplete turn arrives here too, with the reason buried in
    /// status_details. Treating every response.done as completion would silently truncate
    /// the reading and report no error at all.
    /// </summary>
    private static Event ParseDone(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) ||
            response.ValueKind != JsonValueKind.Object)
        {
            return new Event.Completed(null);
        }

        if (TryGetString(response, "status", out var status) &&
            status is not ("completed" or "in_progress"))
        {
            return new Event.Failed(StatusMessage(response, status));
        }

        return new Event.Completed(ParseUsage(response));
    }

    private static string StatusMessage(JsonElement response, string status)
    {
        if (response.TryGetProperty("status_details", out var details) &&
            details.ValueKind == JsonValueKind.Object)
        {
            if (details.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                TryGetString(error, "message", out var message))
            {
                return message;
            }

            if (TryGetString(details, "reason", out var reason))
                return $"The reading was {status} ({reason}).";
        }

        return $"The reading was {status}.";
    }

    private static Usage? ParseUsage(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = Detail(usage, "input_token_details");
        var output = Detail(usage, "output_token_details");

        return new Usage(
            InputText: Count(input, "text_tokens"),
            InputImage: Count(input, "image_tokens"),
            InputCached: Count(input, "cached_tokens"),
            OutputText: Count(output, "text_tokens"),
            OutputAudio: Count(output, "audio_tokens"));

        static JsonElement? Detail(JsonElement usage, string name) =>
            usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
                ? value
                : null;

        static int Count(JsonElement? detail, string name) =>
            detail is { } element &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var count)
                ? count
                : 0;
    }

    private static string ErrorMessage(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var error) &&
            error.ValueKind == JsonValueKind.Object &&
            TryGetString(error, "message", out var message))
        {
            return message;
        }

        return "The server reported an unspecified error.";
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }
}

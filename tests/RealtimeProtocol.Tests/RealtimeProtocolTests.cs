using System.Text;
using System.Text.Json;

namespace SelectAndRead.Tests;

/// <summary>
/// Covers the Realtime wire format. Like the TextCleaner suite these run on any platform,
/// which makes them the only part of the cloud reading engine verifiable during development
/// on macOS - everything downstream of Parse needs the VM.
/// </summary>
public class RealtimeProtocolTests
{
    // --- Client frames ----------------------------------------------------------

    [Fact]
    public void SessionUpdateRequestsAudioOnlyPcmAtTheAgreedRate()
    {
        var session = Property(RealtimeProtocol.BuildSessionUpdate("marin", "Read it."), "session");

        Assert.Equal(new[] { "audio" }, session.GetProperty("output_modalities")
                                              .EnumerateArray()
                                              .Select(e => e.GetString())
                                              .ToArray());

        var output = session.GetProperty("audio").GetProperty("output");
        Assert.Equal("marin", output.GetProperty("voice").GetString());
        Assert.Equal("audio/pcm", output.GetProperty("format").GetProperty("type").GetString());

        // The player is hardwired to this rate; a mismatch would play back at the wrong pitch.
        Assert.Equal(RealtimeProtocol.SampleRate, output.GetProperty("format").GetProperty("rate").GetInt32());
    }

    [Fact]
    public void SessionUpdateDisablesTurnDetection()
    {
        // The app never sends microphone audio. With server VAD left on, the model waits
        // for speech that never arrives and the read never starts.
        var session = Property(RealtimeProtocol.BuildSessionUpdate("marin", "Read it."), "session");
        var detection = session.GetProperty("audio").GetProperty("input").GetProperty("turn_detection");

        Assert.Equal(JsonValueKind.Null, detection.ValueKind);
    }

    [Fact]
    public void ImageItemCarriesThePngAsADataUri()
    {
        var png = new byte[] { 1, 2, 3, 250, 251, 252 };

        var content = Property(RealtimeProtocol.BuildImageItem(png), "item")
            .GetProperty("content");

        var image = content[0];
        Assert.Equal("input_image", image.GetProperty("type").GetString());
        Assert.Equal(
            "data:image/png;base64," + Convert.ToBase64String(png),
            image.GetProperty("image_url").GetString());
    }

    [Fact]
    public void ImageItemCarriesNothingButTheImage()
    {
        // The prompt lives in the session instructions and nowhere else. Restoring it here
        // would make the turn read as a request, which theoreticlly could produce spoken preambles
        // like "ok, let me read that for you" before the response had begun.
        var content = Property(RealtimeProtocol.BuildImageItem([0]), "item")
            .GetProperty("content");

        Assert.Equal(1, content.GetArrayLength());
    }

    [Fact]
    public void PromptsWithJsonMetacharactersSurviveTheRoundTrip()
    {
        // The prompt is user-editable in Settings, so it is untrusted input as far as
        // frame construction is concerned. It reaches the wire as the session instructions.
        const string prompt = "Say \"hello\" \\ {not json} \n verbatim";

        var instructions = Property(RealtimeProtocol.BuildSessionUpdate("marin", prompt), "session")
            .GetProperty("instructions")
            .GetString();

        Assert.Equal(prompt, instructions);
    }

    [Fact]
    public void EndpointEscapesTheModelName()
    {
        var uri = RealtimeProtocol.EndpointFor("gpt-realtime-2.1-mini");

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("api.openai.com", uri.Host);
        Assert.Equal("/v1/realtime", uri.AbsolutePath);
        Assert.Equal("?model=gpt-realtime-2.1-mini", uri.Query);
    }

    // --- Audio ------------------------------------------------------------------

    [Fact]
    public void DecodesAnAudioDelta()
    {
        var pcm = new byte[] { 0x00, 0x01, 0xFF, 0x7F };
        var frame = $$"""
            {"type":"response.output_audio.delta","delta":"{{Convert.ToBase64String(pcm)}}"}
            """;

        var audio = Assert.IsType<RealtimeProtocol.Event.Audio>(RealtimeProtocol.Parse(frame));
        Assert.Equal(pcm, audio.Pcm);
    }

    [Fact]
    public void ReportsAudioThatIsNotValidBase64()
    {
        var frame = """{"type":"response.output_audio.delta","delta":"not base64!!"}""";

        var failed = Assert.IsType<RealtimeProtocol.Event.Failed>(RealtimeProtocol.Parse(frame));
        Assert.Contains("base64", failed.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- Transcript -------------------------------------------------------------

    [Fact]
    public void AccumulatesTranscriptAcrossDeltas()
    {
        string[] frames =
        [
            """{"type":"response.output_audio_transcript.delta","delta":"The quick "}""",
            """{"type":"response.output_audio.delta","delta":"AAA="}""",
            """{"type":"response.output_audio_transcript.delta","delta":"brown fox."}""",
        ];

        var transcript = new StringBuilder();
        foreach (var frame in frames)
        {
            if (RealtimeProtocol.Parse(frame) is RealtimeProtocol.Event.Transcript t)
                transcript.Append(t.Text);
        }

        Assert.Equal("The quick brown fox.", transcript.ToString());
    }

    [Fact]
    public void IgnoresAnEmptyTranscriptDelta()
    {
        var frame = """{"type":"response.output_audio_transcript.delta","delta":""}""";
        Assert.IsType<RealtimeProtocol.Event.Ignored>(RealtimeProtocol.Parse(frame));
    }

    // --- Completion -------------------------------------------------------------

    [Fact]
    public void ReadsUsageFromAFinishedResponse()
    {
        var frame = """
            {"type":"response.done","response":{"status":"completed","usage":{
              "input_token_details":{"text_tokens":250,"image_tokens":500,"cached_tokens":64},
              "output_token_details":{"text_tokens":130,"audio_tokens":800}}}}
            """;

        var completed = Assert.IsType<RealtimeProtocol.Event.Completed>(RealtimeProtocol.Parse(frame));
        Assert.NotNull(completed.Usage);
        var usage = completed.Usage;

        Assert.Equal(250, usage.InputText);
        Assert.Equal(500, usage.InputImage);
        Assert.Equal(64, usage.InputCached);
        Assert.Equal(130, usage.OutputText);
        Assert.Equal(800, usage.OutputAudio);

        // Cached tokens are already counted within the input totals, so Total must not
        // add them again or the --read-file cost report overstates every run.
        Assert.Equal(1680, usage.Total);
    }

    [Fact]
    public void CompletesWithoutUsageWhenTheServerOmitsIt()
    {
        var frame = """{"type":"response.done","response":{"status":"completed"}}""";

        var completed = Assert.IsType<RealtimeProtocol.Event.Completed>(RealtimeProtocol.Parse(frame));
        Assert.Null(completed.Usage);
    }

    [Fact]
    public void TreatsAFailedResponseAsAFailureNotACompletion()
    {
        // A response.done is not automatically success. Reporting this as completion would
        // truncate the reading silently and surface no error at all.
        var frame = """
            {"type":"response.done","response":{"status":"failed","status_details":{
              "error":{"message":"The server had a problem."}}}}
            """;

        var failed = Assert.IsType<RealtimeProtocol.Event.Failed>(RealtimeProtocol.Parse(frame));
        Assert.Equal("The server had a problem.", failed.Message);
    }

    [Fact]
    public void FallsBackToTheReasonWhenAnIncompleteResponseCarriesNoErrorMessage()
    {
        var frame = """
            {"type":"response.done","response":{"status":"incomplete","status_details":{
              "reason":"max_output_tokens"}}}
            """;

        var failed = Assert.IsType<RealtimeProtocol.Event.Failed>(RealtimeProtocol.Parse(frame));
        Assert.Contains("incomplete", failed.Message);
        Assert.Contains("max_output_tokens", failed.Message);
    }

    // --- Errors and forward compatibility ---------------------------------------

    [Fact]
    public void SurfacesAServerErrorMessage()
    {
        var frame = """
            {"type":"error","error":{"type":"invalid_request_error","message":"Invalid API key."}}
            """;

        var failed = Assert.IsType<RealtimeProtocol.Event.Failed>(RealtimeProtocol.Parse(frame));
        Assert.Equal("Invalid API key.", failed.Message);
    }

    [Fact]
    public void StillFailsWhenAnErrorCarriesNoMessage()
    {
        var failed = Assert.IsType<RealtimeProtocol.Event.Failed>(
            RealtimeProtocol.Parse("""{"type":"error"}"""));

        Assert.NotEqual(string.Empty, failed.Message);
    }

    [Theory]
    // The Realtime API emits many events this client has no use for, and adds new ones
    // over time. Every one of these must be inert rather than fatal.
    [InlineData("""{"type":"response.output_audio.done"}""")]
    [InlineData("""{"type":"session.created","session":{}}""")]
    [InlineData("""{"type":"rate_limits.updated"}""")]
    [InlineData("""{"type":"some.event.invented.next.year","payload":{"a":[1,2,3]}}""")]
    [InlineData("""{"no_type_at_all":true}""")]
    [InlineData("""{"type":42}""")]
    [InlineData("[1,2,3]")]
    public void IgnoresEventsItDoesNotActOn(string frame) =>
        Assert.IsType<RealtimeProtocol.Event.Ignored>(RealtimeProtocol.Parse(frame));

    [Fact]
    public void ReportsAFrameThatIsNotJsonAtAll()
    {
        // Distinct from an unknown event type: this is a real problem, not a
        // forward-compatible addition, so it must not be swallowed.
        Assert.IsType<RealtimeProtocol.Event.Failed>(RealtimeProtocol.Parse("<html>502</html>"));
    }

    // --- Helpers ----------------------------------------------------------------

    private static JsonElement Property(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(name).Clone();
    }
}

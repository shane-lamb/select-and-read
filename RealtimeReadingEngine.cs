using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace SelectAndRead;

/// <summary>
/// Raised when the cloud reading fails. <see cref="Spoke"/> is what tells the caller
/// whether falling back to the local engine is still sensible: retrying after the user has
/// already heard half the page read out loud would restart it from the top, which is worse
/// than reporting the failure.
/// </summary>
internal sealed class RealtimeException(string message, bool spoke, Exception? inner = null)
    : Exception(message, inner)
{
    internal bool Spoke { get; } = spoke;
}

/// <summary>
/// Reads a crop by sending it to the OpenAI Realtime API and playing the audio it streams
/// back (SPEC 14). One socket per reading: the session carries no state worth keeping
/// between selections, and a short-lived connection avoids having to police idle timeouts.
/// </summary>
internal sealed class RealtimeReadingEngine : IReadingEngine
{
    /// <summary>Long enough for a cold TLS handshake on a slow link, short enough to fail over.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Cap on silence from the server. Guards the case where the socket stays open but no
    /// frames arrive, which would otherwise hang the reading with no diagnosis.
    /// </summary>
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(60);

    private const int ReceiveBufferSize = 64 * 1024;

    private readonly string _apiKey;
    private string _model = Config.DefaultCloudModel;
    private string _voice = Config.DefaultCloudVoice;
    private string _prompt = Config.DefaultCloudPrompt;

    private RealtimeAudioPlayer? _player;
    private CancellationTokenSource? _cts;

    internal RealtimeReadingEngine(string apiKey) => _apiKey = apiKey;

    public bool IsSpeaking => _player?.IsPlaying ?? false;

    public void ApplySettings(Config config)
    {
        _model = Blank(config.CloudModel) ? Config.DefaultCloudModel : config.CloudModel;
        _voice = Blank(config.CloudVoice) ? Config.DefaultCloudVoice : config.CloudVoice;
        _prompt = config.EffectiveCloudPrompt;

        static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
    }

    /// <summary>What the call actually cost and how fast it started, for --read-file.</summary>
    internal sealed record Diagnostics(
        TimeSpan TimeToFirstAudio,
        TimeSpan Total,
        RealtimeProtocol.Usage? Usage);

    public async Task<string?> ReadAsync(Bitmap crop, CancellationToken cancellationToken) =>
        (await ReadDetailedAsync(crop, cancellationToken)).Text;

    internal async Task<(string? Text, Diagnostics Info)> ReadDetailedAsync(
        Bitmap crop, CancellationToken cancellationToken)
    {
        Stop();

        // Encode before anything touches the network so the caller can release the freeze
        // frame immediately, rather than holding tens of megabytes for the whole reading
        // (the reason SPEC 2.2 split recognition from playback in the first place).
        var png = crop.ToPng();

        var previous = _cts;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        previous?.Dispose();

        var token = _cts.Token;
        var player = new RealtimeAudioPlayer();
        _player = player;

        var clock = Stopwatch.StartNew();
        var transcript = new StringBuilder();
        var firstAudio = TimeSpan.Zero;
        RealtimeProtocol.Usage? usage = null;

        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + _apiKey);

            await ConnectAsync(socket, RealtimeProtocol.EndpointFor(_model), token);

            await SendAsync(socket, RealtimeProtocol.BuildSessionUpdate(_voice, _prompt), token);
            await SendAsync(socket, RealtimeProtocol.BuildImageItem(png, _prompt), token);
            await SendAsync(socket, RealtimeProtocol.BuildResponseCreate(), token);

            await foreach (var frame in ReceiveAsync(socket, token))
            {
                switch (RealtimeProtocol.Parse(frame))
                {
                    case RealtimeProtocol.Event.Audio audio:
                        if (firstAudio == TimeSpan.Zero) firstAudio = clock.Elapsed;
                        player.Append(audio.Pcm);
                        break;

                    case RealtimeProtocol.Event.Transcript text:
                        transcript.Append(text.Text);
                        break;

                    case RealtimeProtocol.Event.Completed completed:
                        usage = completed.Usage;
                        goto done;

                    case RealtimeProtocol.Event.Failed failure:
                        throw new RealtimeException(failure.Message, spoke: firstAudio != TimeSpan.Zero);
                }
            }

        done:
            player.CompleteInput();
            await player.WaitForCompletionAsync(token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own Stop(), or a timeout that already fired. Not an error to report.
        }
        catch (RealtimeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException)
        {
            throw new RealtimeException(
                Describe(ex), spoke: firstAudio != TimeSpan.Zero, ex);
        }
        finally
        {
            player.Stop();
            player.Dispose();
            if (ReferenceEquals(_player, player)) _player = null;
        }

        var spoken = transcript.ToString().Trim();
        return (spoken.Length == 0 ? null : spoken,
                new Diagnostics(firstAudio, clock.Elapsed, usage));
    }

    private static async Task ConnectAsync(ClientWebSocket socket, Uri endpoint, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            await socket.ConnectAsync(endpoint, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new RealtimeException(
                $"Could not reach the reading service within {ConnectTimeout.TotalSeconds:0} seconds.",
                spoke: false);
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, string json, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, token);
    }

    /// <summary>
    /// Yields complete server frames. A single Realtime event routinely spans several
    /// WebSocket frames - audio deltas are large - so partial reads must be reassembled
    /// before any of it reaches the JSON parser.
    /// </summary>
    private static async IAsyncEnumerable<string> ReceiveAsync(
        ClientWebSocket socket,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new byte[ReceiveBufferSize];
        var message = new MemoryStream();

        while (socket.State == WebSocketState.Open)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ReceiveTimeout);

            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new RealtimeException(
                    "The reading service stopped responding.", spoke: false);
            }

            if (result.MessageType == WebSocketMessageType.Close) yield break;

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var frame = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);

            yield return frame;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        WebSocketException { WebSocketErrorCode: WebSocketError.NotAWebSocket } =>
            "The reading service rejected the connection. Check the API key in Settings.",
        _ => "Could not reach the reading service: " + ex.Message,
    };

    public void Stop()
    {
        _cts?.Cancel();
        _player?.Stop();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _player?.Dispose();
    }
}

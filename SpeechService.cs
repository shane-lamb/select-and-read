using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace SelectAndRead;

/// <summary>
/// Synthesis is behind an interface so that the chunked, lower-latency implementation
/// described in SPEC 7.4 can replace the simple one without touching callers.
/// </summary>
internal interface ISpeechEngine : IDisposable
{
    bool IsSpeaking { get; }
    void ApplySettings(Config config);
    Task SpeakAsync(string text, CancellationToken cancellationToken);
    void Stop();
}

/// <summary>
/// Windows' built-in speech synthesis (SPEC 7). Synthesises the whole utterance and then
/// plays it - the simple path. If measurement on real hardware shows first-word latency
/// above the ~700ms target, SPEC 7.4 describes the chunked replacement.
/// </summary>
internal sealed class SpeechService : ISpeechEngine
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly MediaPlayer _player = new();

    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _playbackFinished;
    private MediaSource? _currentSource;

    public bool IsSpeaking { get; private set; }

    internal SpeechService()
    {
        // Lets the OS duck other audio appropriately for speech.
        _player.AudioCategory = MediaPlayerAudioCategory.Speech;

        // Without this the app appears in the System Media Transport Controls and
        // hijacks the keyboard's media keys.
        _player.CommandManager.IsEnabled = false;

        _player.MediaEnded += (_, _) => Complete();
        _player.MediaFailed += (_, e) => Fail(e.ErrorMessage);
    }

    public void ApplySettings(Config config)
    {
        var voice = SelectVoice(config.VoiceId);
        if (voice is not null) _synthesizer.Voice = voice;
        // Verified on Windows 11: the synthesizer's own default is 1.0, and this
        // assignment is honoured - the audio length scales with it exactly.
        _synthesizer.Options.SpeakingRate = Math.Clamp(config.SpeakingRate, 0.5, 6.0);
    }

    /// <summary>
    /// SPEC 7.2. Whether Windows' downloadable natural voices are exposed to
    /// non-Narrator apps varies by build, so this is a runtime probe with a fallback
    /// rather than a hard dependency on any particular voice existing.
    /// </summary>
    internal static VoiceInformation? SelectVoice(string? voiceId)
    {
        var all = SpeechSynthesizer.AllVoices;

        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            var configured = all.FirstOrDefault(v => v.Id == voiceId);
            if (configured is not null) return configured;
        }

        var natural = all.FirstOrDefault(
            v => v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase));

        return natural ?? SpeechSynthesizer.DefaultVoice;
    }

    internal static IReadOnlyList<VoiceInformation> AvailableVoices() =>
        SpeechSynthesizer.AllVoices.ToList();

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        Stop();

        var previous = _cts;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        previous?.Dispose();

        var token = _cts.Token;

        var stream = await _synthesizer.SynthesizeTextToStreamAsync(text).AsTask(token);
        token.ThrowIfCancellationRequested();

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _playbackFinished = finished;

        _currentSource = MediaSource.CreateFromStream(stream, stream.ContentType);
        _player.Source = _currentSource;
        IsSpeaking = true;
        _player.Play();

        try
        {
            using (token.Register(() => finished.TrySetCanceled()))
            {
                await finished.Task;
            }
        }
        finally
        {
            ReleaseSource();
        }
    }

    /// <summary>Immediate, and safe to call in any state including when idle (SPEC 7.5).</summary>
    public void Stop()
    {
        _cts?.Cancel();

        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch (ObjectDisposedException)
        {
            // Racing with Dispose is harmless here.
        }

        ReleaseSource();
        Settle(pending => pending.TrySetCanceled());
    }

    private void Complete() => Settle(pending => pending.TrySetResult());

    private void Fail(string? message) => Settle(pending => pending.TrySetException(
        new InvalidOperationException(
            string.IsNullOrWhiteSpace(message) ? "Audio playback failed." : message)));

    private void Settle(Action<TaskCompletionSource> resolve)
    {
        IsSpeaking = false;
        var pending = Interlocked.Exchange(ref _playbackFinished, null);
        if (pending is not null) resolve(pending);
    }

    /// <summary>
    /// MediaSource is disposable and one is created per utterance, so dropping it without
    /// disposing would leak the synthesised audio buffer on every read.
    /// </summary>
    private void ReleaseSource()
    {
        var source = Interlocked.Exchange(ref _currentSource, null);
        source?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _player.Dispose();
        _synthesizer.Dispose();
    }
}

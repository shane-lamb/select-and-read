using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace SelectAndRead;

/// <summary>
/// Where the reading has got to, as a range of characters in the text being spoken, closed
/// at both ends (SPEC 16.1).
///
/// The synthesiser publishes word boundaries as SpeechCue objects on a timed metadata track,
/// each carrying the offsets of the input text it covers. That is the whole reason the
/// position can be tied back to a place on screen: an ordinal count of spoken words could
/// not be, because one written token can produce several spoken ones - "$12.50" is
/// measurably five cues over the same six input characters.
///
/// It is a range and not a single offset because a cue can also run the other way, covering
/// *more* than one written word: "in 2018" comes back as one seven-character range repeated
/// across five cues, since the voice groups the preposition with the number it expands.
/// Reading only the start of that range marks "in" and leaves the mark stranded there for as
/// long as "two thousand and eighteen" takes to say.
/// </summary>
internal readonly record struct WordCue(TimeSpan Start, int From, int To);

/// <summary>
/// Synthesis is behind an interface so that the chunked, lower-latency implementation
/// described in SPEC 7.4 can replace the simple one without touching callers.
/// </summary>
internal interface ISpeechEngine : IDisposable
{
    /// <summary>
    /// True from the moment playback starts until the utterance ends, is stopped or fails -
    /// including while it is paused. It means "there is a live utterance", not "audio is
    /// audible right now", which is what lets a caller tell a pausable reading apart from
    /// one that is still being synthesised.
    /// </summary>
    bool IsSpeaking { get; }

    void ApplySettings(Config config);
    Task SpeakAsync(string text, CancellationToken cancellationToken);

    /// <summary>
    /// Raised as the reading moves on, with the range of the text passed to
    /// <see cref="SpeakAsync"/> that is now being spoken, and with null when nothing is.
    /// Raised on a timer thread, so handlers that touch UI must marshal.
    /// </summary>
    event Action<WordCue?>? WordSpoken;

    /// <summary>Holds playback where it is, leaving it resumable (SPEC 2.5).</summary>
    void Pause();

    /// <summary>Continues from where <see cref="Pause"/> left off.</summary>
    void Resume();

    void Stop();
}

/// <summary>
/// Windows' built-in speech synthesis (SPEC 7). Synthesises the whole utterance and then
/// plays it - the simple path. If measurement on real hardware shows first-word latency
/// above the ~700ms target, SPEC 7.4 describes the chunked replacement.
/// </summary>
internal sealed class SpeechService : ISpeechEngine
{
    /// <summary>
    /// How often playback position is sampled to advance the highlight. Measured on Windows
    /// 11 ARM64: at 50ms a word is detected 2-58ms (mean 30ms) after it actually starts,
    /// against words lasting 150-400ms, so the highlight lands within the word every time.
    /// </summary>
    private const int PollIntervalMs = 50;

    /// <summary>
    /// How long synthesis will wait for the boundary track to be published before giving up
    /// and speaking without one. Bounded rather than open-ended on purpose: the highlight is
    /// an addition, and delaying the audio for it would trade the app's core promise for a
    /// secondary one (SPEC 7.4).
    /// </summary>
    private const int TrackWaitMs = 250;

    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly MediaPlayer _player = new();

    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _playbackFinished;
    private MediaSource? _currentSource;

    private System.Threading.Timer? _tracker;
    private IReadOnlyList<WordCue> _cues = [];
    private int _lastReported = -1;

    public bool IsSpeaking { get; private set; }

    public event Action<WordCue?>? WordSpoken;

    internal SpeechService()
    {
        // Without this the synthesiser publishes no boundary track at all, so there is
        // nothing to read back and the reading is silently unmarked (SPEC 16.1). Set once
        // here rather than per-utterance: it costs nothing when no one is watching, and
        // tying it to the setting would mean re-synthesising to turn the mark back on.
        _synthesizer.Options.IncludeWordBoundaryMetadata = true;

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

        // The boundary cues hang off a playback item, not off the source, so the player is
        // given the item even when nothing is listening for them.
        var item = new MediaPlaybackItem(_currentSource);
        _cues = await WordCuesAsync(_currentSource, item, token);
        _lastReported = -1;

        _player.Source = item;
        IsSpeaking = true;
        _player.Play();

        StartTracking();

        try
        {
            using (token.Register(() => finished.TrySetCanceled()))
            {
                await finished.Task;
            }
        }
        finally
        {
            StopTracking();
            ReleaseSource();
        }
    }

    // --- Word boundaries (SPEC 16.1) --------------------------------------------

    /// <summary>
    /// Collects the word-boundary cue table for an utterance, or an empty one if the voice
    /// does not publish boundaries or is too slow to.
    ///
    /// The track is not there for the asking: it appears only once the source has opened, and
    /// then only after the list has been repopulated, so a single read finds nothing even for
    /// a voice that emits them. Failing quietly is deliberate - a reading with no highlight
    /// is a working reading.
    /// </summary>
    private static async Task<IReadOnlyList<WordCue>> WordCuesAsync(
        MediaSource source, MediaPlaybackItem item, CancellationToken cancellationToken)
    {
        try
        {
            await source.OpenAsync().AsTask(cancellationToken);

            for (var waited = 0; item.TimedMetadataTracks.Count == 0 && waited < TrackWaitMs;
                 waited += PollIntervalMs)
            {
                await Task.Delay(PollIntervalMs, cancellationToken);
            }

            // Word and sentence boundaries arrive as two tracks of the same kind, so the
            // label is the only thing separating them.
            var track = item.TimedMetadataTracks.FirstOrDefault(
                t => t.Label?.Contains("Word", StringComparison.OrdinalIgnoreCase) == true);

            if (track is null) return [];

            // EndPositionInInput is the index of the range's last character, not one past it.
            // It is taken rather than assumed equal to the start because a cue routinely
            // covers more than the one word - see WordCue.
            return track.Cues
                .OfType<SpeechCue>()
                .Where(cue => cue.StartPositionInInput is not null)
                .Select(cue => new WordCue(
                    cue.StartTime,
                    cue.StartPositionInInput!.Value,
                    cue.EndPositionInInput ?? cue.StartPositionInInput!.Value))
                .OrderBy(cue => cue.Start)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Any failure to read the metadata costs the highlight, never the reading.
            return [];
        }
    }

    private void StartTracking()
    {
        StopTracking();
        if (_cues.Count == 0) return;

        _tracker = new System.Threading.Timer(
            _ => Track(), null, PollIntervalMs, PollIntervalMs);
    }

    private void StopTracking()
    {
        var tracker = Interlocked.Exchange(ref _tracker, null);
        if (tracker is null) return;

        tracker.Dispose();
        WordSpoken?.Invoke(null);
    }

    /// <summary>
    /// Reports the word covering the current playback position. Reads position rather than
    /// counting elapsed time, which is what makes pausing free: a paused player stops
    /// advancing, so the highlight simply stays on the word the reading stopped at.
    /// </summary>
    private void Track()
    {
        TimeSpan position;

        try
        {
            position = _player.PlaybackSession.Position;
        }
        catch (ObjectDisposedException)
        {
            return;                                 // racing with Dispose
        }

        // The last cue that has started is the word being spoken. Cues have no useful
        // duration - every one measured came back as zero - so a word owns the screen until
        // the next one begins.
        var index = _cues.Count - 1;
        while (index >= 0 && _cues[index].Start > position) index--;

        if (index < 0 || index == _lastReported) return;

        _lastReported = index;
        WordSpoken?.Invoke(_cues[index]);
    }

    /// <summary>
    /// Pausing differs from <see cref="Stop"/> in what it leaves alone: the token source,
    /// the MediaSource and the pending completion all stay exactly as they are, so the
    /// awaited SpeakAsync is still running and Resume picks up mid-word. Stop tears all
    /// three down, which is why it cannot be reused here (SPEC 7.5).
    /// </summary>
    public void Pause()
    {
        if (!IsSpeaking) return;

        try
        {
            _player.Pause();
        }
        catch (ObjectDisposedException)
        {
            // Racing with Dispose is harmless here.
        }
    }

    public void Resume()
    {
        if (!IsSpeaking) return;

        try
        {
            _player.Play();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Immediate, and safe to call in any state including when idle (SPEC 7.5).</summary>
    public void Stop()
    {
        _cts?.Cancel();

        // Tracking is torn down here and deliberately not in Pause: a paused reading is still
        // live, and its highlight has to stay on screen marking where it will resume.
        StopTracking();

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
        StopTracking();
        _cts?.Dispose();
        _player.Dispose();
        _synthesizer.Dispose();
    }
}

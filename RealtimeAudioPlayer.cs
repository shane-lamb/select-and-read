using System.Threading.Channels;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace SelectAndRead;

/// <summary>
/// Plays PCM as it arrives, rather than after it is complete (SPEC 14.3).
///
/// SpeechService cannot be reused here: it synthesises a whole utterance to a stream and
/// hands the finished stream to MediaPlayer, which is exactly the full-utterance latency
/// the cloud engine exists to remove. A MediaStreamSource instead pulls samples on demand,
/// so playback starts as soon as the first chunk lands and continues while the rest is
/// still on the wire.
///
/// MediaStreamSource is chosen over AudioGraph because it needs no unsafe code and no
/// IMemoryBufferByteAccess interop, and because it keeps the MediaPlayer configuration that
/// SPEC 7 already settled: AudioCategory.Speech so the OS ducks other audio correctly, and
/// CommandManager disabled so the app does not hijack the keyboard's media keys.
/// </summary>
internal sealed class RealtimeAudioPlayer : IDisposable
{
    /// <summary>Bytes of PCM per second, at the format agreed in RealtimeProtocol.</summary>
    private const int BytesPerSecond =
        RealtimeProtocol.SampleRate * RealtimeProtocol.Channels * (RealtimeProtocol.BitsPerSample / 8);

    /// <summary>
    /// How much audio to let MediaStreamSource buffer before it starts. Enough to ride out
    /// ordinary network jitter without adding latency the user would notice.
    /// </summary>
    private static readonly TimeSpan BufferTime = TimeSpan.FromMilliseconds(200);

    private readonly MediaPlayer _player = new();
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly TaskCompletionSource _finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly MediaStreamSource _source;
    private MediaSource? _mediaSource;
    private TimeSpan _position;
    private bool _started;
    private bool _paused;
    private int _stopped;

    internal RealtimeAudioPlayer()
    {
        _player.AudioCategory = MediaPlayerAudioCategory.Speech;
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded += (_, _) => Settle(pending => pending.TrySetResult());
        _player.MediaFailed += (_, e) => Settle(pending => pending.TrySetException(
            new InvalidOperationException(
                string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Audio playback failed." : e.ErrorMessage)));

        var properties = AudioEncodingProperties.CreatePcm(
            RealtimeProtocol.SampleRate,
            RealtimeProtocol.Channels,
            RealtimeProtocol.BitsPerSample);

        _source = new MediaStreamSource(new AudioStreamDescriptor(properties))
        {
            // A live stream: no duration is known up front and there is nothing to seek to.
            CanSeek = false,
            BufferTime = BufferTime,
        };

        _source.Starting += OnStarting;
        _source.SampleRequested += OnSampleRequested;
    }

    internal bool IsPlaying => _started && !_finished.Task.IsCompleted;

    /// <summary>Queues a chunk. Playback begins on the first one.</summary>
    internal void Append(byte[] pcm)
    {
        if (pcm.Length == 0) return;
        if (!_chunks.Writer.TryWrite(pcm)) return;

        if (!_started)
        {
            _started = true;
            _mediaSource = MediaSource.CreateFromMediaStreamSource(_source);
            _player.Source = _mediaSource;

            // Chunks keep arriving from the socket while playback is paused, so this must
            // not be an unconditional Play: otherwise a chunk landing during a pause would
            // silently start the audio again.
            if (!_paused) _player.Play();
        }
    }

    /// <summary>
    /// Holds playback where it is. Sample requests stop; the socket carries on filling the
    /// channel, which simply queues up for the resume.
    /// </summary>
    internal void Pause()
    {
        _paused = true;

        try
        {
            _player.Pause();
        }
        catch (ObjectDisposedException)
        {
            // Racing with Dispose is harmless here.
        }
    }

    internal void Resume()
    {
        _paused = false;
        if (!_started) return;

        try
        {
            _player.Play();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Signals that no further chunks are coming. Playback continues until the queue
    /// drains, at which point the source reports end-of-stream and MediaEnded fires.
    /// </summary>
    internal void CompleteInput()
    {
        _chunks.Writer.TryComplete();

        // Nothing ever arrived, so there is no MediaEnded to wait for.
        if (!_started) Settle(pending => pending.TrySetResult());
    }

    /// <summary>Completes when playback finishes, is stopped, or fails.</summary>
    internal async Task WaitForCompletionAsync(CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(() => Settle(pending => pending.TrySetCanceled())))
        {
            await _finished.Task;
        }
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        // Live source: always start from the beginning, whatever position is requested.
        args.Request.SetActualStartPosition(TimeSpan.Zero);
    }

    /// <summary>
    /// Pulls the next chunk. Leaving <c>request.Sample</c> null is how MediaStreamSource is
    /// told the stream has ended - there is no separate "finished" call - so the null path
    /// here is the normal end of every reading, not an error path.
    /// </summary>
    private async void OnSampleRequested(
        MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        var deferral = request.GetDeferral();

        try
        {
            var chunk = await NextChunkAsync();
            if (chunk is null) return;

            var duration = TimeSpan.FromSeconds((double)chunk.Length / BytesPerSecond);

            var sample = MediaStreamSample.CreateFromBuffer(ToBuffer(chunk), _position);
            sample.Duration = duration;
            request.Sample = sample;

            _position += duration;
        }
        catch (Exception ex)
        {
            Settle(pending => pending.TrySetException(ex));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<byte[]?> NextChunkAsync()
    {
        while (await _chunks.Reader.WaitToReadAsync())
        {
            if (_chunks.Reader.TryRead(out var chunk)) return chunk;
        }

        return null;
    }

    /// <summary>
    /// byte[] to IBuffer via DataWriter rather than the AsBuffer interop extension, for the
    /// same reason OcrService gives: DataWriter is a plain WinRT projection that takes a
    /// byte[] directly, with no dependency on the WindowsRuntime marshalling extensions.
    /// </summary>
    private static IBuffer ToBuffer(byte[] bytes)
    {
        var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    public void Stop()
    {
        // Completing the writer unblocks any SampleRequested currently awaiting a chunk,
        // which would otherwise hold its deferral open forever.
        _chunks.Writer.TryComplete();

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

    private void Settle(Action<TaskCompletionSource> resolve)
    {
        // MediaEnded and an explicit Stop can race; whichever lands first wins, and the
        // other becomes a no-op rather than an InvalidOperationException on the TCS.
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        resolve(_finished);
    }

    private void ReleaseSource()
    {
        var source = Interlocked.Exchange(ref _mediaSource, null);
        source?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _source.Starting -= OnStarting;
        _source.SampleRequested -= OnSampleRequested;
        _player.Dispose();
    }
}

using EnbNext.Assets;
using EnbNext.Presentation;
using Silk.NET.OpenAL;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    private Task<EnbRetailMusicCatalog>? _musicCatalogTask;
    private Task<DecodedMusic>? _musicDecode;
    private CancellationTokenSource _musicCancellation = new();
    private Queue<EnbRetailMusicTheme> _musicPlaylist = new();
    private sealed record MusicCategoryProgress(Queue<EnbRetailMusicTheme> Playlist,EnbRetailMusicTheme? Theme,int Component,int? LastTheme);
    private readonly Dictionary<string,MusicCategoryProgress> _musicCategories = new();
    private string _musicCategory = "Wander";
    private EnbSceneMusicContext? _musicContext;
    private EnbRetailMusicTheme? _musicTheme;
    private int _musicComponent;
    private int? _lastMusicTheme;
    private sealed class MusicStream
    {
        public uint Source;
        public readonly Dictionary<uint,int> Buffers = new();
        public int UploadedBytes, ConsumedBytes;
        public DecodedMusic? Pcm;
        public double FadeStarted, FadeDuration;
    }
    private MusicStream _musicStream = new();
    private readonly List<MusicStream> _outgoingMusic = new();
    private uint _musicSource {get=>_musicStream.Source;set=>_musicStream.Source=value;}
    private Dictionary<uint,int> _musicBuffers=>_musicStream.Buffers;
    private int _musicUploadedBytes {get=>_musicStream.UploadedBytes;set=>_musicStream.UploadedBytes=value;}
    private int _musicConsumedBytes=>_musicStream.ConsumedBytes;
    private double _musicNotBefore;
    private DecodedMusic? _playingMusic {get=>_musicStream.Pcm;set=>_musicStream.Pcm=value;}
    private int _musicRepeats;
    private bool _overrideCompleted;
    private bool _musicIsOverride;
    private string _musicStatus = "music not requested";
    /// <summary>Next music player. Left alone it changes nothing: retail's choice of music.</summary>
    internal EnbMusicPlayer? Player { get; set; }
    private Task<(IReadOnlyList<EnbRetailMusicTheme> Themes, string? Error)>? _ownMusicTask;
    private IReadOnlyList<EnbRetailMusicTheme> _ownMusic = [];
    private EnbRetailMusicCatalog? _playerCatalog, _playerCatalogBase;
    private int _playerRaces = -1, _playerOwnRace = -1;
    /// <summary>The pieces played, oldest first (Previous goes back through them).</summary>
    private readonly List<EnbRetailMusicTheme> _musicHistory = [];
    private bool _musicQuickStart, _musicPlayingQuick;
    private EnbMusicTrack? _musicTrack;

    // 0074BFB0 chooses Planet for modes 1/5; any tracked attacker overrides
    // this with Combat. Each category retains its own component queue.
    public void UpdateMusic(EnbSceneMusicContext? context, EnbRetailAssetBundle assets, double nowSeconds, float gain)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LiveWorldOpenAlAudio));
        if (context is null) { StopMusic(); if (Player is { } idle) PublishPlayer(idle); return; }
        try
        {
            if (_musicContext is null || _musicContext.Race != context.Race || _musicContext.MusicId != context.MusicId)
            {
                StopMusic(); _musicContext = context;
                _musicNotBefore = nowSeconds + (context.MusicId < 0 ? 5 : 0); // 0043A1F0 initial delay.
            }
            _musicContext = context;
            _musicCatalogTask ??= Task.Run(() => EnbRetailMusicCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"music.ini")));
            if (!_musicCatalogTask.IsCompleted) { _musicStatus = "music loading definitions"; return; }
            var catalog = _musicCatalogTask.GetAwaiter().GetResult();
            var player = Player;
            if (player is not null) catalog = PreparePlayer(player, catalog, context);
            // The player may keep combat from interrupting the music: the moment's own music plays on.
            string category = player is { CombatMusic: false } && context.Combat ? (context.Planet ? "Planet" : "Wander") : context.Category;
            if (!EnsureInitialized()) { _musicStatus = "music blocked · " + _status; return; }
            if (!_alc!.MakeContextCurrent(_context)) throw new InvalidOperationException("Cannot bind music audio context.");
            _al!.GetError();
            UpdateOutgoingMusic(nowSeconds,gain);
            if (!(context.MusicId>=0 && !_overrideCompleted))
                SelectMusicCategory(category,catalog,nowSeconds);
            if (player is not null && PlayerRequests(player, context, nowSeconds)) return;

            if (_musicSource != 0)
            {
                // Paused: the piece holds where it is (a refill would start it again).
                if (player is { Paused: true })
                {
                    _al.GetSourceProperty(_musicSource, GetSourceInteger.SourceState, out int held);
                    if (held == (int)SourceState.Playing) { _al.SourcePause(_musicSource); CheckMusicError("pause"); }
                    _musicStatus = "music paused"; return;
                }
                RefillMusic();
                _al.GetSourceProperty(_musicSource, GetSourceInteger.SourceState, out int state);
                _al.GetSourceProperty(_musicSource, SourceFloat.SecOffset, out float offset);
                offset += (float)_musicConsumedBytes / (_playingMusic!.Channels * 2 * _playingMusic.SampleRate);
                CheckMusicError("query");
                if (state == (int)SourceState.Playing)
                {
                    var playing = _playingMusic!;
                    double fadeIn = _musicComponent == 1 && !_musicIsOverride ? playing.Component.FadeInSeconds : 0;
                    // A piece the player asked for starts at once (at most a second's fade).
                    if (_musicPlayingQuick) fadeIn = Math.Min(fadeIn, 1);
                    double fadeOut = _musicComponent == _musicTheme!.Components.Count && !_musicIsOverride ? playing.Component.FadeOutSeconds : 0;
                    float envelope = MusicEnvelope(offset, playing.DurationSeconds, fadeIn, fadeOut);
                    _al.SetSourceProperty(_musicSource, SourceFloat.Gain, Math.Clamp(gain,0f,1f) * envelope);
                    CheckMusicError("gain");
                    _musicStatus = $"music playing {_musicTheme.Id:D3}/{_musicComponent} {playing.Component.ResourceName} · {playing.Channels}ch {playing.SampleRate}Hz · {offset:0.0}/{playing.DurationSeconds:0.0}s · gain {gain * envelope:0.###} · device {DeviceName}";
                    return;
                }
                if (_musicIsOverride && (_musicRepeats == 0 || --_musicRepeats > 0))
                {
                    _musicStream.UploadedBytes = _musicStream.ConsumedBytes = 0;
                    foreach (uint buffer in _musicBuffers.Keys.ToArray()) QueueMusicBlock(buffer);
                    _al.SourcePlay(_musicSource); CheckMusicError("repeat"); return;
                }
                RetireMusicSource();
                if (_musicIsOverride) { _overrideCompleted = true; _musicTheme = null; }
                else if (_musicComponent >= _musicTheme!.Components.Count)
                {
                    _lastMusicTheme = _musicTheme.Id; _musicTheme = null;
                    _musicNotBefore = nowSeconds + EnbRetailPresentationRandom.Shared.Next((int)(catalog.SilenceMinSeconds*1000),(int)(catalog.SilenceMaxSeconds*1000))*.001;
                }
            }
            // An override that just completed must release deferred category choice.
            if (_overrideCompleted) SelectMusicCategory(category,catalog,nowSeconds);
            // Paused between pieces: the next one waits too.
            if (player is { Paused: true }) { _musicStatus = "music paused"; return; }
            if (nowSeconds < _musicNotBefore) { _musicStatus = $"music pause · {_musicNotBefore-nowSeconds:0.0}s"; return; }
            if (_musicTheme is null)
            {
                if (context.MusicId >= 0 && !_overrideCompleted)
                {
                    var theme=catalog.Themes.FirstOrDefault(t => t.Id == context.MusicId && t.Components.Count > 0);
                    _musicTheme=theme is null?null:EnbRetailMusicCatalog.SelectComponent(theme,EnbRetailPresentationRandom.Shared.Next);
                }
                else
                {
                    if (_musicPlaylist.Count == 0)
                        foreach (var theme in player is null
                                     ? catalog.Playlist(_musicCategory,context.Race,context.OverallLevel,_lastMusicTheme,EnbRetailPresentationRandom.Shared.Next)
                                     : catalog.Playlist(_musicCategory,player.RaceIncluded,context.OverallLevel,_lastMusicTheme,EnbRetailPresentationRandom.Shared.Next))
                            _musicPlaylist.Enqueue(theme);
                    if (_musicPlaylist.Count > 0) _musicTheme = _musicPlaylist.Dequeue();
                }
                if (_musicTheme is null) { _musicStatus = "music blocked · no eligible authored theme"; return; }
                _musicComponent = 0;
                _musicIsOverride = context.MusicId >= 0 && !_overrideCompleted;
                _musicRepeats = _musicIsOverride ? _musicTheme.LoopCount : 1;
            }
            _musicDecode ??= DecodeMusicAsync(assets,_musicTheme.Components[_musicComponent],_musicCancellation.Token);
            if (!_musicDecode.IsCompleted) { _musicStatus = "music decoding " + _musicTheme.Components[_musicComponent].ResourceName; return; }
            var decoded = _musicDecode.GetAwaiter().GetResult();
            if (decoded.Error is not null)
            {
                // 00437C30: a component whose sound cannot be created (009EFEC0
                // returns NULL) is skipped; the theme ends after its last component.
                _musicStatus = "music component skipped · " + decoded.Error;
                if (_musicIsOverride)
                {
                    if (_musicRepeats == 0 || --_musicRepeats > 0) return;
                    _musicDecode = null; _overrideCompleted = true; _musicTheme = null;
                    return;
                }
                _musicDecode = null;
                if (++_musicComponent < _musicTheme.Components.Count) return;
                _lastMusicTheme = _musicTheme.Id; _musicTheme = null;
                _musicNotBefore = nowSeconds + EnbRetailPresentationRandom.Shared.Next((int)(catalog.SilenceMinSeconds*1000),(int)(catalog.SilenceMaxSeconds*1000))*.001;
                return;
            }
            _musicDecode = null;
            _playingMusic = decoded;
            _musicStream.UploadedBytes = _musicStream.ConsumedBytes = 0;
            _musicSource = _al.GenSource();
            for (int i=0;i<4;i++)
            {
                uint buffer = _al.GenBuffer(); _musicBuffers.Add(buffer,0); QueueMusicBlock(buffer);
            }
            _al.SetSourceProperty(_musicSource, SourceBoolean.SourceRelative, true);
            _al.SetSourceProperty(_musicSource, SourceVector3.Position, 0f,0f,0f);
            _al.SetSourceProperty(_musicSource, SourceFloat.RolloffFactor, 0f);
            _al.SetSourceProperty(_musicSource, SourceBoolean.Looping, false);
            _al.SetSourceProperty(_musicSource, SourceFloat.Gain,
                _musicComponent == 0 && !_musicIsOverride && decoded.Component.FadeInSeconds > 0 ? 0f : Math.Clamp(gain,0f,1f));
            CheckMusicError("upload/configure");
            _al.SourcePlay(_musicSource); CheckMusicError("start");
            if (_musicComponent == 0)
            {
                _musicPlayingQuick = _musicQuickStart; _musicQuickStart = false;
                _musicHistory.Add(_musicTheme!); if (_musicHistory.Count > 50) _musicHistory.RemoveAt(0);
                _musicTrack = player is null ? null : TrackOf(player, _musicTheme!);
            }
            _musicComponent++;
            // One component ahead, bounded lifetime. Never run file I/O/MP3 decode in the GL callback.
            if (!_musicIsOverride && _musicComponent < _musicTheme.Components.Count)
                _musicDecode = DecodeMusicAsync(assets,_musicTheme.Components[_musicComponent],_musicCancellation.Token);
            _musicStatus = "music started " + decoded.Component.ResourceName;
        }
        catch (Exception ex)
        {
            RetireMusicSource();
            _musicStatus = $"music blocked · {ex.GetType().Name}: {ex.Message}";
        }
        finally { if (Player is { } shown) PublishPlayer(shown); }
    }

    /// <summary>The player's own music joins the game's, its choice of races is applied, and the library is listed.</summary>
    private EnbRetailMusicCatalog PreparePlayer(EnbMusicPlayer player, EnbRetailMusicCatalog catalog, EnbSceneMusicContext context)
    {
        player.OwnRace = context.Race;
        if (player.RescanRequested)
        {
            player.RescanRequested = false; string folder = player.Folder;
            _ownMusicTask = Task.Run(() => EnbMusicPlayer.LoadFolder(folder));
        }
        bool changed = false;
        if (_ownMusicTask is { IsCompleted: true } scan)
        {
            (_ownMusic, player.FolderError) = scan.GetAwaiter().GetResult();
            player.OwnCount = _ownMusic.Count; _ownMusicTask = null; _playerCatalog = null;
        }
        if (_playerCatalog is null || !ReferenceEquals(_playerCatalogBase, catalog))
        {
            _playerCatalogBase = catalog;
            _playerCatalog = _ownMusic.Count == 0 ? catalog : catalog with { Themes = [.. catalog.Themes, .. _ownMusic] };
            player.Library = EnbMusicPlayer.BuildLibrary(catalog, _ownMusic); changed = true;
        }
        // Another choice of races or new music of one's own changes what comes next; what plays now finishes.
        if (changed || _playerRaces != player.Races || _playerOwnRace != context.Race)
        {
            _playerRaces = player.Races; _playerOwnRace = context.Race; _musicPlaylist.Clear();
            foreach (string key in _musicCategories.Keys.ToArray()) _musicCategories[key] = _musicCategories[key] with { Playlist = new() };
        }
        return _playerCatalog;
    }

    /// <summary>The window's requests: move within the piece, the next or previous piece, or a chosen piece.
    /// True when this update has nothing more to do.</summary>
    private bool PlayerRequests(EnbMusicPlayer player, EnbSceneMusicContext context, double now)
    {
        if (player.Seek is double seek)
        {
            player.Seek = null;
            if (_musicSource != 0 && _playingMusic is { } pcm) SeekMusic(pcm, seek);
        }
        EnbRetailMusicTheme? start = null;
        if (player.Chosen is { } chosen) { player.Chosen = null; start = chosen.Theme; }
        else if (player.Step > 0)
        {
            player.Step = 0;
            // The next piece of the queue, without the silence between pieces.
            FadeOutCurrentMusic(now); CancelMusicDecode(); _musicTheme = null; _musicNotBefore = now; _musicQuickStart = true;
            if (context.MusicId >= 0) _overrideCompleted = true;
            return false;
        }
        else if (player.Step < 0)
        {
            player.Step = 0;
            // Back to the start of this piece; near its start, the piece before it.
            if (_musicSource != 0 && player.Position > 4 && _playingMusic is { } playing) { SeekMusic(playing, 0); return false; }
            if (_musicHistory.Count >= 2)
            {
                _musicHistory.RemoveAt(_musicHistory.Count - 1);
                start = _musicHistory[^1]; _musicHistory.RemoveAt(_musicHistory.Count - 1);
            }
            else if (_musicSource != 0 && _playingMusic is { } first) { SeekMusic(first, 0); return false; }
            else return false;
        }
        if (start is null) return false;
        FadeOutCurrentMusic(now); CancelMusicDecode();
        _musicTheme = start; _musicComponent = 0; _musicIsOverride = false; _musicRepeats = 1;
        _musicNotBefore = now; _musicQuickStart = true;
        if (context.MusicId >= 0) _overrideCompleted = true;
        return false;
    }

    /// <summary>The piece now playing fades out in a moment (another one starts).</summary>
    private void FadeOutCurrentMusic(double now)
    {
        if (_musicSource == 0) return;
        _musicStream.FadeStarted = now; _musicStream.FadeDuration = .6;
        _outgoingMusic.Add(_musicStream); _musicStream = new();
    }

    /// <summary>Plays the piece on from a point (its decoded samples are kept, so this is immediate).</summary>
    private void SeekMusic(DecodedMusic pcm, double seconds)
    {
        int frame = pcm.Channels * 2;
        if (frame == 0 || pcm.SampleRate == 0) return;
        long bytes = (long)(Math.Clamp(seconds, 0, Math.Max(0, pcm.DurationSeconds - .5)) * pcm.SampleRate) * frame;
        _al!.SourceStop(_musicSource);
        _al.GetSourceProperty(_musicSource, GetSourceInteger.BuffersQueued, out int queued);
        while (queued-- > 0) { uint released = 0; _al.SourceUnqueueBuffers(_musicSource, 1, &released); }
        _musicStream.UploadedBytes = _musicStream.ConsumedBytes = (int)bytes;
        foreach (uint buffer in _musicBuffers.Keys.ToArray()) { _musicBuffers[buffer] = 0; QueueMusicBlock(buffer); }
        _al.SourcePlay(_musicSource); CheckMusicError("seek");
    }

    private static EnbMusicTrack TrackOf(EnbMusicPlayer player, EnbRetailMusicTheme theme)
    {
        foreach (var track in player.Library) if (track.Same(theme)) return track;
        return new(theme, EnbMusicPlayer.PieceName(theme.Components[0].ResourceName), "");
    }

    /// <summary>What plays, for the window: the piece, how far it is, and the kind of music now.</summary>
    private void PublishPlayer(EnbMusicPlayer player)
    {
        player.Category = _musicContext is null ? "" : _musicCategory;
        if (_musicSource == 0 || _playingMusic is not { Channels: > 0, SampleRate: > 0 } pcm || _al is null)
        { player.Current = null; player.Position = player.Duration = 0; return; }
        try
        {
            _al.GetSourceProperty(_musicSource, SourceFloat.SecOffset, out float offset);
            player.Position = offset + (double)_musicConsumedBytes / (pcm.Channels * 2 * pcm.SampleRate);
            player.Duration = pcm.DurationSeconds; player.Current = _musicTrack;
        }
        catch (InvalidOperationException) { player.Current = null; }
    }

    internal static float MusicEnvelope(double offset, double duration, double fadeIn, double fadeOut)
        => (float)Math.Min(fadeIn <= 0 ? 1 : Math.Clamp(offset/fadeIn,0,1),
            fadeOut <= 0 ? 1 : Math.Clamp((duration-offset)/fadeOut,0,1));

    private void SelectMusicCategory(string category,EnbRetailMusicCatalog catalog,double now)
    {
        if (_musicCategory==category) return;
        if (_musicSource!=0)
        {
            // 0043AE30 -> MusicCategoryFadeOut 00438230 hands the category's current
            // sound to its own fade (vtable +0xAC) and releases it; fades have no count limit.
            _musicStream.FadeStarted=now;
            _musicStream.FadeDuration=_musicCategory switch {
                "Combat"=>catalog.CombatFadeOutSeconds,
                "Planet"=>catalog.PlanetFadeOutSeconds,
                _=>catalog.WanderFadeOutSeconds };
            _outgoingMusic.Add(_musicStream);
            _musicStream=new();
        }
        // Native consumes a component at launch. Retain the next component and
        // queued themes, not the interrupted sample offset (00438230/00437EBD).
        if (_musicTheme is {} completed && _musicComponent>=completed.Components.Count)
        { _lastMusicTheme=completed.Id; _musicTheme=null; _musicComponent=0; }
        _musicCategories[_musicCategory]=new(_musicPlaylist,_musicTheme,_musicComponent,_lastMusicTheme);
        CancelMusicDecode();
        _musicCategory=category;
        if (_musicCategories.TryGetValue(category,out var next))
        { _musicPlaylist=next.Playlist; _musicTheme=next.Theme; _musicComponent=next.Component; _lastMusicTheme=next.LastTheme; }
        else { _musicPlaylist=new(); _musicTheme=null; _musicComponent=0; _lastMusicTheme=null; }
        // The incoming category has no owned stream: native FadeIn is a no-op
        // here. Its first component retains its own authored fade on creation.
        UpdateOutgoingMusic(now,0,updateGain:false);
    }

    private void CancelMusicDecode()
    {
        _musicCancellation.Cancel(); _musicCancellation.Dispose(); _musicCancellation=new();
        _musicDecode=null;
    }

    private void UpdateOutgoingMusic(double now,float gain,bool updateGain=true)
    {
        for(int i=_outgoingMusic.Count-1;i>=0;i--)
        {
            var stream=_outgoingMusic[i];
            if(now-stream.FadeStarted>=stream.FadeDuration)
            { RetireMusicSource(stream); _outgoingMusic.RemoveAt(i); continue; }
            if(!updateGain)continue;
            RefillMusic(stream);
            _al!.GetSourceProperty(stream.Source,GetSourceInteger.SourceState,out int state);
            if(state!=(int)SourceState.Playing)
            { RetireMusicSource(stream); _outgoingMusic.RemoveAt(i); continue; }
            float envelope=(float)Math.Clamp(1-(now-stream.FadeStarted)/stream.FadeDuration,0,1);
            _al.SetSourceProperty(stream.Source,SourceFloat.Gain,Math.Clamp(gain,0,1)*envelope);
            CheckMusicError("outgoing fade");
        }
    }

    private void CheckMusicError(string operation)
    {
        var error = _al!.GetError();
        if (error != AudioError.NoError) throw new InvalidOperationException($"OpenAL music {operation}: {error}");
    }

    private void RetireMusicSource(MusicStream? stream=null)
    {
        stream ??= _musicStream;
        if (_al is not null && _alc is not null && _context != null && _alc.MakeContextCurrent(_context))
        {
            if (stream.Source != 0) { _al.SourceStop(stream.Source); DeleteVoiceSource(stream.Source); }
            foreach (uint buffer in stream.Buffers.Keys) _al.DeleteBuffer(buffer);
            _al.GetError();
        }
        stream.Source = 0; stream.Buffers.Clear(); stream.Pcm = null;
    }

    private void QueueMusicBlock(uint buffer,MusicStream? stream=null)
    {
        stream ??= _musicStream;
        var pcm = stream.Pcm!;
        int count = Math.Min(65536,pcm.Pcm.Length-stream.UploadedBytes);
        if (count <= 0) return;
        fixed (byte* samples = pcm.Pcm)
            _al!.BufferData(buffer,pcm.Channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16,
                samples+stream.UploadedBytes,count,pcm.SampleRate);
        _al!.SourceQueueBuffers(stream.Source,1,&buffer);
        stream.Buffers[buffer] = count; stream.UploadedBytes += count;
    }

    private void RefillMusic(MusicStream? stream=null)
    {
        stream ??= _musicStream;
        _al!.GetSourceProperty(stream.Source,GetSourceInteger.BuffersProcessed,out int processed);
        while (processed-- > 0)
        {
            uint buffer = 0;
            _al.SourceUnqueueBuffers(stream.Source,1,&buffer);
            CheckMusicError("stream unqueue");
            stream.ConsumedBytes += stream.Buffers[buffer];
            stream.Buffers[buffer] = 0;
            QueueMusicBlock(buffer,stream);
        }
        _al.GetSourceProperty(stream.Source,GetSourceInteger.BuffersQueued,out int queued);
        _al.GetSourceProperty(stream.Source,GetSourceInteger.SourceState,out int state);
        if (queued > 0 && state != (int)SourceState.Playing) _al.SourcePlay(stream.Source);
        CheckMusicError("stream refill");
    }

    private void StopMusic()
    {
        RetireMusicSource();
        foreach(var stream in _outgoingMusic) RetireMusicSource(stream);
        _outgoingMusic.Clear(); _musicCategories.Clear(); _musicCategory="Wander";
        if (_musicContext is not null || _musicDecode is not null)
        {
            _musicCancellation.Cancel(); _musicCancellation.Dispose();
            _musicCancellation = new();
        }
        _musicDecode = null; _musicPlaylist.Clear(); _musicContext = null;
        _musicTheme = null; _lastMusicTheme = null; _musicComponent = 0; _overrideCompleted = false;
        _musicTrack = null;
        _musicStatus = "music stopped";
    }

    private static Task<DecodedMusic> DecodeMusicAsync(EnbRetailAssetBundle assets, EnbRetailMusicComponent component, CancellationToken cancellation)
        => Task.Run(() => DecodeMusic(assets,component,cancellation));

    internal static DecodedMusic DecodeMusic(EnbRetailAssetBundle assets, EnbRetailMusicComponent component, CancellationToken cancellation = default)
    {
        try
        {
            byte[] encoded;
            // The player's own music is a file on disk (EnbMusicPlayer.LoadFolder gives its full path).
            if (Path.IsPathFullyQualified(component.ResourceName)) encoded = File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(component.ResourceName));
            else if (assets.Paths.TryResolveRetailLooseResource(component.ResourceName, out var path)) encoded = File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(path));
            else if (assets.RetailMixResources is not null && assets.RetailMixResources.TryReadResource(component.ResourceName,out var bytes,out _)) encoded = bytes;
            else throw new FileNotFoundException("Installed music resource missing: " + component.ResourceName);
            cancellation.ThrowIfCancellationRequested();
            // 00437C30 opens each component through the ordinary sound factory
            // (009EFEC0), so a WAV component such as null.wav is a sound like any other.
            if (!component.ResourceName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                if (!EnbRetailWaveAudio.TryDecodePcm16(encoded, out EnbRetailWavePcm16 wave, out string? waveError))
                    throw new InvalidDataException(waveError);
                return new(component, wave.Pcm16LittleEndian, wave.Channels, wave.SampleRate, null);
            }
            var pcm = EnbMp3Decoder.Decode(encoded,cancellation: cancellation);
            if (pcm.Bytes.Length == 0) throw new InvalidDataException("Music decoder returned no samples.");
            return new(component,pcm.Bytes,pcm.Channels,pcm.SampleRate,null);
        }
        catch (Exception ex) { return new(component,[],0,0,ex.Message); }
    }

    internal sealed record DecodedMusic(EnbRetailMusicComponent Component, byte[] Pcm, int Channels, int SampleRate, string? Error)
    {
        public double DurationSeconds => Channels == 0 || SampleRate == 0 ? 0 : (double)Pcm.Length/(Channels*2*SampleRate);
    }
}

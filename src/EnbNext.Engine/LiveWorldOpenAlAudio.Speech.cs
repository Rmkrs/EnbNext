using EnbNext.Assets;
using EnbNext.Rendering;
using Silk.NET.OpenAL;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    internal SpeechScope Speech { get; set; } = new();

    // One scope is shared by the world and HUD belonging to a client view.
    // Decode completion must not reorder requests from those two producers.
    internal sealed class SpeechScope
    {
        private readonly Queue<SpeechRequest> _loading = [];
        private readonly EnbRetailSpeechManager<SpeechVoice> _manager = new();
        internal readonly EnbRetailSoundCallGate Calls = new();
        private bool _pumping;
        internal bool NeedsUpdate => _loading.Count != 0 || _manager.Current is not null || _manager.HasPending;
        internal SpeechVoice? Current => _manager.Current;
        internal void Queue(SpeechRequest request) => _loading.Enqueue(request);
        internal bool Protects(LiveWorldOpenAlAudio owner, string key) =>
            _manager.Current is { } current && ReferenceEquals(current.Owner, owner) && current.Sound.ResourceName == key;
        internal void Pump()
        {
            if (_pumping) return;
            _pumping = true;
            try
            {
                _manager.Current?.Update();
                if (_manager.Current?.StartupFailed == true) _manager.Stop();
                else _manager.Update();
                if (_manager.Current?.Loading == true) return;
                while (_loading.TryPeek(out var request))
                {
                    if (request.Cancelled || request.Owner._disposed) { _loading.Dequeue(); continue; }
                    _loading.Dequeue();
                    request.Handle = _manager.RequestSpeech(request.Name, request.Priority, request.Create, request.Owner);
                    request.Submitted = true;
                    request.CopyState();
                    if (_manager.Current?.Loading == true) break;
                }
            }
            finally
            {
                _pumping = false;
            }
        }
        internal void Stop()
        {
            foreach (var request in _loading) request.Cancelled = true;
            _loading.Clear();
            _manager.Stop();
        }
        internal void Retire(LiveWorldOpenAlAudio owner)
        {
            foreach (var request in _loading)
                if (ReferenceEquals(request.Owner, owner)) request.Cancelled = true;
            _manager.DiscardPendingOwner(owner);
            if (_manager.Current is { } current && ReferenceEquals(current.Owner, owner)) current.Stop();
            // The next shared update drains a surviving request on its own
            // device. Clearing one view must not stop another view's speech.
        }
    }

    internal sealed class SpeechRequest(LiveWorldOpenAlAudio owner, StationSound caller,
        string name, EnbRetailAssetBundle assets, float gain, double elapsed,
        EnbRetailSoundDefinition definition, float priority)
    {
        internal readonly LiveWorldOpenAlAudio Owner = owner;
        internal readonly string Name = name;
        internal readonly float Priority = priority;
        private readonly bool _forceFile = caller.ForceSpeechFile;
        private readonly float? _volume = caller.VolumeOverride, _priority = caller.PriorityOverride;
        private readonly int? _loopCount = caller.LoopCountOverride;
        internal bool Cancelled, Submitted;
        internal SpeechVoice? Handle;

        internal SpeechVoice? Create()
        {
            try { return CreateCore(); }
            catch (Exception ex)
            {
                if (ReferenceEquals(caller.SpeechRequest, this))
                    caller.Status = "station audio blocked: " + ex.Message;
                return null;
            }
        }

        private SpeechVoice? CreateCore()
        {
            // After admission the manager owns the request, independently of
            // the caller's null handle (0043F2D0's queued return).
            if (Owner._disposed) return null;
            if (!Owner.Speech.Calls.TryCall(definition))
            {
                if (ReferenceEquals(caller.SpeechRequest, this))
                    caller.Status = "station audio suppressed by authored call interval";
                return null;
            }
            // 0043E360 gates the call before 0043E230 selects the file. A
            // queued request reaches this point only when it actually drains.
            int choice = EnbCurrentRetailWeightedSoundSelection.NextChoice(definition.Files, Owner.EffectChoiceSample);
            if (definition.Files[choice].IsNullChoice) return null;
            bool weighted = definition.Files.Count != 1 || definition.Files[0].Weight is not null;
            var actual = new StationSound
            {
                Name = Name,
                AllowSpeech = true, ForceSpeechFile = _forceFile,
                BypassSpeechManager = true, UseAuthoredLoopCount = true,
                VolumeOverride = _volume, PriorityOverride = _priority,
                LoopCountOverride = _loopCount,
                SelectedDefinition = weighted ? definition with { Files = [definition.Files[choice] with { Weight = null }] } : null,
                ResourceName = weighted ? $"\u001Fspeech:{Name}:{choice}" : null
            };
            var resource = Owner.GetStationResource(actual, Name, assets);
            if (resource is null) throw new InvalidDataException("Speech audio cache unavailable.");
            var voice = new SpeechVoice(Owner, actual, Name, assets, gain, elapsed, resource,
                actual.SelectedDefinition ?? definition);
            voice.Update();
            if (voice.StartupFailed)
            {
                if (ReferenceEquals(caller.SpeechRequest, this)) caller.Status = actual.Status;
                return null;
            }
            return voice;
        }

        internal void CopyState()
        {
            if (!ReferenceEquals(caller.SpeechRequest, this)) return;
            if (Cancelled)
            {
                caller.Source = 0; caller.Completed = true; caller.Status = "speech cancelled";
                return;
            }
            if (Handle is { } handle)
            {
                caller.Source = handle.Sound.Source;
                caller.Completed = handle.Sound.Completed;
                caller.Volume = handle.Sound.Volume;
                caller.Status = handle.Sound.Status;
            }
            else if (Submitted)
            {
                caller.Source = 0;
                caller.Completed = true;
                if (!caller.Status.StartsWith("station audio blocked:", StringComparison.Ordinal))
                    caller.Status = "speech request has no caller handle";
            }
        }
    }

    internal sealed class SpeechVoice : IEnbRetailSpeechVoice
    {
        internal readonly LiveWorldOpenAlAudio Owner;
        internal readonly StationSound Sound;
        private readonly string _name;
        private readonly EnbRetailAssetBundle _assets;
        private readonly float _gain;
        private EnbRetailSpeechClock? _clock;
        private readonly double _initialOffset;
        private readonly EffectSoundResource _resource;
        private readonly EnbRetailSoundDefinition _definition;
        internal bool Loading => _clock is null && !Sound.Completed;
        internal bool StartupFailed => _clock is null && Sound.Completed && Sound.Status.StartsWith("station audio blocked:", StringComparison.Ordinal);
        public string FileName { get; }
        public float Priority { get; }
        public uint DurationMilliseconds => _clock?.Duration ?? 0;
        public uint ElapsedMilliseconds => _clock?.Elapsed ?? 0;
        internal SpeechVoice(LiveWorldOpenAlAudio owner, StationSound sound, string name,
            EnbRetailAssetBundle assets, float gain, double initialOffset, EffectSoundResource resource, EnbRetailSoundDefinition definition)
        {
            Owner = owner; Sound = sound; _name = name; _assets = assets; _gain = gain;
            _initialOffset = initialOffset; _resource = resource; _definition = definition;
            FileName = definition.Files.Single().LogicalResourceName!;
            Priority = sound.PriorityOverride ?? definition.EffectivePriority;
        }
        public bool Active
        {
            get
            {
                if (Owner._disposed || Sound.Completed || Owner._al is null) return false;
                if (Loading) return true;
                if (Sound.Source == 0) return false;
                Owner._alc!.MakeContextCurrent(Owner._context);
                Owner._al.GetSourceProperty(Sound.Source, GetSourceInteger.SourceState, out int state);
                return state is (int)SourceState.Playing or (int)SourceState.Paused;
            }
        }
        internal void Update()
        {
            if (!Owner._disposed && !Sound.Completed)
            {
                Owner.UpdateStationSound(Sound, _name, _assets, _gain, _clock is null ? _initialOffset : 0);
                if (_clock is null && Sound.Source != 0)
                {
                    var pcm = _resource.Load.GetAwaiter().GetResult().Pcm!;
                    uint duration = (uint)((long)pcm.Pcm16LittleEndian.Length * 1000 / (pcm.SampleRate * pcm.Channels * 2L));
                    _clock = new(unchecked((uint)Environment.TickCount), duration,
                        Sound.LoopCountOverride ?? _definition.EffectiveLoopCount);
                }
                _clock?.Advance(unchecked((uint)Environment.TickCount));
                if (_clock?.Complete == true) Stop();
            }
        }
        public void Stop()
        {
            string? failure = Sound.Status.StartsWith("station audio blocked:", StringComparison.Ordinal) ? Sound.Status : null;
            Owner.StopStationSound(Sound);
            Sound.Completed = true;
            Sound.Status = failure ?? "speech complete or interrupted";
        }
        public void ReleaseManagerReference() => Stop();
    }

    private EffectSoundResource? GetStationResource(StationSound sound, string name, EnbRetailAssetBundle assets)
    {
        if (sound.SelectedDefinition is { } selected)
            return GetEffectResource(sound.ResourceName!, assets, true, selectedDefinition: selected);
        // Packaged creator narration retains its explicitly approved definition.
        bool direct = sound.ForceSpeechFile && !EnbNextAudioAssetCatalog.TryResolve(name, AppContext.BaseDirectory, out _);
        sound.ResourceName = direct ? "speech-file/" + name : name;
        return GetEffectResource(sound.ResourceName, assets, true,
            selectedDefinition: direct ? assets.Sounds?.GetSpeechFileDefinition(name) : null);
    }

    private bool TryUpdateSpeech(StationSound sound, string name, EnbRetailAssetBundle assets, float gain, double elapsed)
    {
        if (sound.FontLifetime is not null || assets.Sounds is not { } sounds ||
            (!sound.ForceSpeechFile && sounds.GetDefinitionOrFile(name).EffectiveSoundType != 2)) return false;
        // Preserve the existing no-device frontend timeline; it is not an audible voice.
        if (!EnsureInitialized()) return false;
        if (sound.Name != name) { StopStationSound(sound); sound.Name = name; }
        if (sound.SpeechRequest is null && !sound.Completed)
        {
            var definition = sound.ForceSpeechFile ? sounds.GetSpeechFileDefinition(name) : sounds.GetDefinitionOrFile(name);
            if (EnbNextAudioAssetCatalog.TryResolve(name, AppContext.BaseDirectory, out _))
                definition = EnbNextAudioAssetCatalog.CharacterCreationSpeechDefinition(name, definition);
            sound.Status = "station audio decoding";
            sound.SpeechRequest = new(this, sound, name, assets, gain, elapsed, definition,
                sound.PriorityOverride ?? definition.EffectivePriority);
            Speech.Queue(sound.SpeechRequest);
        }
        Speech.Pump();
        sound.SpeechRequest?.CopyState();
        return true;
    }
}

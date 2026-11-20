using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using System.Numerics;

namespace EnbNext.Engine;

/// <summary>HUD +118/current, +11C/selected and avatar +204/mode (00563910/00563C50).</summary>
internal sealed class EnbCallerPortraitState
{
    private sealed class Actor
    {
        public required LiveAvatarSpeechPlayback Playback;
        public required EnbSceneAvatarSnapshot Appearance;
        public readonly EnbRetailAvatarReceivedSpeech Received = new();
        public EnbAvatarPortraitAsset? Pose;
        public int Mode;
    }
    private readonly Dictionary<ulong, Actor> actors = [];
    private readonly Dictionary<uint, Actor> cachedActors = [];
    private readonly Dictionary<uint,ulong> cachedLifetimes = [];
    private IReadOnlyDictionary<uint,ulong> lifetimes = new Dictionary<uint,ulong>();
    private bool station;
    private ulong modeRevision;
    private Guid stream;
    private long sequence;
    private ulong? selected, current, observedSelection;
    private EnbRetailAssetBundle? assets;
    public IEnbRenderObject? Current { get; private set; }
    public EnbAvatarPortraitAsset? Pose { get; private set; }
    public string? Diagnostic { get; private set; }
    public readonly record struct AnimationSound(string Name,bool Flat,Vector3 Position);
    private readonly List<AnimationSound> animationSounds=[];
    public IReadOnlyList<AnimationSound> AnimationSounds=>animationSounds;
    public IReadOnlyList<ulong> AdditionalPortraits {get;set;}=[];
    public EnbAvatarPortraitAsset? PoseFor(ulong id)=>actors.TryGetValue(id,out var actor)?actor.Pose:null;
    private void PrepareCache(IEnbSceneFrame scene,EnbRetailAssetBundle? bundle)
    {
        bool nextStation=scene.ActiveStarbaseId is not null;
        if(stream!=(scene.Hud.Chat?.StreamId??Guid.Empty) || !ReferenceEquals(assets,bundle) || station!=nextStation || modeRevision!=scene.MeshBufferResetRevision)
        {
            actors.Clear();cachedActors.Clear();cachedLifetimes.Clear();selected=current=observedSelection=null;sequence=0;
            stream=scene.Hud.Chat?.StreamId??Guid.Empty;assets=bundle;station=nextStation;modeRevision=scene.MeshBufferResetRevision;Diagnostic=null;
        }
        lifetimes=scene.AvatarCacheLifetimes;
        if(scene.AvatarDescriptions is {} descriptions)
            foreach(uint id in cachedActors.Keys.Where(id=>!descriptions.ContainsKey(id) || cachedLifetimes.GetValueOrDefault(id)!=lifetimes.GetValueOrDefault(id)).ToArray())
            {
                var removed=cachedActors[id];cachedActors.Remove(id);cachedLifetimes.Remove(id);
                foreach(var key in actors.Where(p=>ReferenceEquals(p.Value,removed)).Select(p=>p.Key).ToArray())actors.Remove(key);
            }
    }
    private Actor Acquire(uint id,EnbSceneAvatarSnapshot appearance,EnbRetailAssetBundle bundle,double now)
    {
        if(cachedActors.TryGetValue(id,out var actor))return actor;
        var playback=new LiveAvatarSpeechPlayback(bundle,appearance,portraitIdle:true);
        playback.StartIdle(now);
        actor=new(){Playback=playback,Appearance=appearance};
        cachedActors.Add(id,actor);
        cachedLifetimes[id]=lifetimes.GetValueOrDefault(id);
        return actor;
    }
    public void AcquireGroupAvatar(IEnbSceneFrame scene,EnbRetailAssetBundle bundle,uint id,double now)
    {
        PrepareCache(scene,bundle);
        if(scene.ActiveStarbaseId is not null || scene.AvatarDescriptions?.GetValueOrDefault(id) is not {} appearance)return;
        // 595530 ->74C7B0 ->77E3F0 initializes idle/NLP even for a name-only
        // consumer. It does not attach a PIP or activate/update the actor.
        try {Acquire(id,appearance,bundle,now);}
        catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        {Diagnostic=ex.Message;}
    }
    public IReadOnlyList<EnbNext.Net7.Gameplay.Net7PlayerNlpRecord>? ProduceChat(IEnbSceneFrame scene,EnbRetailAssetBundle bundle,string text,double now)
    {
        PrepareCache(scene,bundle);
        if(scene.LocalAvatarObjectId is not uint id || scene.AvatarDescriptions?.GetValueOrDefault(id) is not {} appearance)return null;
        // 74C840/77E610 and slash 77E3F0 share identical first acquisition
        // initialization; only the latter adds a native reference count.
        return Acquire(id,appearance,bundle,now).Playback.ProduceChat(text);
    }
    private bool expanded = true;
    public bool Expanded
    {
        get => expanded;
        set
        {
            if(value && !expanded && current is ulong id && actors.TryGetValue(id,out var actor) && actor.Mode==0)
                actor.Mode=1; // Reattachment: 564030 -> 7E3A20 activates an inactive cached actor.
            expanded=value;
        }
    }

    public void Update(IEnbSceneFrame scene, EnbRetailAssetBundle? bundle, double now)
    {
        animationSounds.Clear();
        PrepareCache(scene,bundle);
        var available = scene.Objects.Where(o => o.Avatar is not null).ToDictionary(o => o.RenderIdentity);
        var evaluated=new HashSet<Actor>();
        void EvaluateActor(Actor actor)
        {
            if(!evaluated.Add(actor))return; // 783B50 +E4 guards the global frame.
            actor.Pose=actor.Playback.Evaluate(now);
            if(actor.Playback.Diagnostic is {} diagnostic)Diagnostic=diagnostic;
            // 74C3B0 constructs the space cache actor with identity +330;
            // 7E3A20/786410 reads its animated bones, not the ship transform.
            if(actor.Pose?.AnimationSoundPosition is {} position)
                foreach(var cue in actor.Playback.AnimationSounds)
                    animationSounds.Add(new(cue.Sound.Sound,cue.Sound.Flat,position(cue.Bone)));
            if(actor.Mode==2 && !actor.Playback.Active)actor.Mode=0;
        }
        var primed = new HashSet<Actor>();
        void PrimeHidden(Actor actor)
        {
            // 563910 attaches/updates a changed recipient before 564030
            // reapplies the collapsed state. Later hidden ticks do not update.
            if(Expanded || scene.ActiveStarbaseId is not null || !primed.Add(actor))return;
            EvaluateActor(actor);
        }
        foreach (ulong id in actors.Keys.Where(id => !available.ContainsKey(id)).ToArray()) actors.Remove(id);
        var target = scene.Objects.FirstOrDefault(o => o.IsCurrentTarget && o.ObjectType == 1 && o.Avatar is not null);
        ulong? selection = target?.RenderIdentity;
        if (target?.Avatar is {} selectedAppearance && bundle is not null && !actors.ContainsKey(target.RenderIdentity))
        {
            try
            {
                var actor=Acquire(target.GameId,selectedAppearance,bundle,now);
                actor.Mode=1;actors.Add(target.RenderIdentity,actor);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            { Diagnostic=ex.Message; }
        }
        if (selection != observedSelection)
        {
            observedSelection=selection;
            selected = current = selection;
            if (selection is ulong id && actors.TryGetValue(id, out var actor)) { PrimeHidden(actor); actor.Mode = 1; }
        }
        if (scene.Hud.Chat is {} chat)
        foreach (var message in chat.AvatarSpeech)
        {
            if (message.Sequence <= sequence) continue;
            sequence = message.Sequence;
            if (bundle is null || scene.ActiveStarbaseId is not null) continue;
            var obj = available.Values.FirstOrDefault(o => o.GameId == message.AvatarId && o.WorldCreationOrdinal == message.CreationOrdinal);
            EnbSceneAvatarSnapshot? appearance=obj?.Avatar;
            if(message.CacheLifetime is ulong lifetime)
            {
                if(!scene.AvatarCacheLifetimes.TryGetValue(message.AvatarId,out ulong currentLifetime) || currentLifetime!=lifetime)continue;
                appearance=scene.AvatarDescriptions?.GetValueOrDefault(message.AvatarId);
            }
            if(appearance is null)continue;
            try
            {
                var actor=Acquire(message.AvatarId,appearance,bundle,now);
                if(obj is not null && !actors.ContainsKey(obj.RenderIdentity))
                {
                    actor.Mode=obj.RenderIdentity==selected?1:0;actors.Add(obj.RenderIdentity,actor);
                }
                var commands = actor.Received.Consume(message.Commands, actor.Mode, actor.Playback.Mood);
                actor.Playback.Idle.Posture = actor.Received.Posture;
                actor.Playback.ReplaceReceived(commands, actor.Received.Mood, now);
                // Ingress feeds the actor before changing its portrait mode.
                if (message.ShowCaller)
                {
                    // 736F10 feeds the cache first. 563910 then clears both
                    // HUD IDs when its separate world-object lookup fails.
                    if(!message.WorldObjectPresent || obj is null){current=selected=null;continue;}
                    if (obj.RenderIdentity == selected) { if(current!=selected)PrimeHidden(actor); current = selected; actor.Mode = 1; }
                    else if (current != obj.RenderIdentity) { PrimeHidden(actor); current = obj.RenderIdentity; actor.Mode = 2; }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            { Diagnostic = ex.Message; }
        }
        // 563C50 observes the prior update's inactive mode before 7E3A20
        // advances the attached actor. Collapsing releases that attachment
        // (564030 -> 7E4350); the cache retains its queue and body slots.
        if (current is ulong active && (!available.ContainsKey(active) || Expanded && actors.TryGetValue(active, out var state) && state.Mode == 0))
            current = current == selected ? selected = null : selected;
        foreach(ulong id in AdditionalPortraits)
            if(actors.TryGetValue(id,out var retained) && available.TryGetValue(id,out var updated) && updated.Avatar is {} appearance &&
                !EnbWorldRenderer.SamePortrait(retained.Appearance,appearance))
            {
                try {retained.Playback.RefreshAppearance(appearance);retained.Appearance=appearance;}
                catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
                {Diagnostic=ex.Message;}
            }
        foreach (var (id, actor) in actors)
        {
            if (actor.Mode == 0 || id != current || !Expanded || scene.ActiveStarbaseId is not null)
            { if(!AdditionalPortraits.Contains(id))actor.Playback.Pause(now); continue; }
            EvaluateActor(actor);
        }
        // 5E2BF0 and 5473F0 obtain the same 74C7B0 actor as the caller
        // view. A second window activates an inactive actor, but cannot
        // advance its slots or emit the same cue twice in one update.
        if(bundle is not null && scene.ActiveStarbaseId is null)
        foreach(ulong id in AdditionalPortraits)
        {
            if(!available.TryGetValue(id,out var obj) || obj.Avatar is not {} appearance)continue;
            try
            {
                if(!actors.TryGetValue(id,out var actor))
                {
                    actor=Acquire(obj.GameId,appearance,bundle,now);actors.Add(id,actor);
                }
                if(actor.Mode==0)actor.Mode=1;
                EvaluateActor(actor);
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            {Diagnostic=ex.Message;}
        }
        foreach(var actor in cachedActors.Values.Where(a=>!actors.ContainsValue(a)))actor.Playback.Pause(now);
        Current = scene.ActiveStarbaseId is null && current is ulong key ? available.GetValueOrDefault(key) : null;
        Pose = Current is {} visible && actors.TryGetValue(visible.RenderIdentity, out var owner) ? owner.Pose : null;
    }
}

namespace EnbNext.Rendering;
public sealed partial class EnbCurrentRetailEffectRslObjectRuntime
{
    public sealed partial class ObjectState
    {
        /// <summary>Explicit diagnostic import. The renderer never retains the
        /// snapshot or subclasses it; fixture fields initialize a separate owner.</summary>
        public static ObjectState ImportDiagnostic(EnbCurrentRetailEffectRslObjectUpdate snapshot)
        {
            var owner=new ObjectState(0,0,0,snapshot.CurrentObjectStateId,snapshot.ResourceGraph!)
            {IsHistoricalQuery=true,Counters=RslCallbackCounters.FromDiagnostic(snapshot.Receipt),InitialObjectSceneEventCount=snapshot.InitialObjectSceneEventCount,
             FactoryActionCount=snapshot.FactoryActionCount,FactoryObjectStateId=snapshot.FactoryObjectStateId,OwnerDeleteRequested=snapshot.OwnerDeleteRequested};
            owner.ActiveStateIds.UnionWith(snapshot.ActiveStateIds);
            foreach(int id in snapshot.ActiveListStateIds)owner.ActiveList.Enable(id);
            owner.Actions.AddRange(snapshot.Actions);owner.ActivationVirtualActionOrdinals.UnionWith(snapshot.ActivationVirtualActionOrdinals);
            owner.HiddenObjectStateIds.UnionWith(snapshot.HiddenObjectStateIds);
            if(snapshot.CurrentObjectHidden && snapshot.CurrentObjectStateId is int current)owner.HiddenObjectStateIds.Add(current);
            owner.CompletedSounds.UnionWith(snapshot.CompletedSoundStateIds);owner.SoundHandleReleases.AddRange(snapshot.SoundHandleReleases);
            owner.ObjectSceneEvents.AddRange(snapshot.ObjectSceneEvents);
            foreach(var pair in snapshot.EffectiveActionPacketOrdinals)owner.EffectiveActionPacketOrdinals.Add(pair.Key,pair.Value);
            foreach(var pair in snapshot.ObjectVisibilityTransitionCounts)owner.ObjectVisibilityTransitionCounts.Add(pair.Key,pair.Value);
            foreach(var pair in snapshot.AnimationEntryFrames)owner.AnimationEntryFrames.Add(pair.Key,pair.Value);
            foreach(var pair in snapshot.EffectPassTransitionFlags)owner.EffectPassFlags.Add(pair.Key,pair.Value);
            foreach(var pair in snapshot.AnimationFrames)owner.AnimationFrames.Add(pair.Key,new(owner)
                {Frame=pair.Value,PreviousFrame=snapshot.AnimationPreviousFrames.GetValueOrDefault(pair.Key),
                Flags=snapshot.AnimationTransitionFlags.GetValueOrDefault(pair.Key),ActivationTick=snapshot.AnimationActivationTicks.GetValueOrDefault(pair.Key)});
            foreach(var pair in snapshot.AnimationWeights)owner.AnimationEnvelopes.Add(pair.Key,new(owner,default,0){ContributionWeight=pair.Value});
            foreach(var pair in snapshot.ObjectElapsedSeconds)owner.ObjectLifetimes.Add(pair.Key,new(owner)
                {Elapsed=pair.Value,Flags=snapshot.ObjectTransitionFlags.GetValueOrDefault(pair.Key)});
            foreach(var pair in snapshot.LegoControllerQueryUpdates)owner.LegoControllerQueryUpdates.Add(pair.Key,ImportDiagnostic(pair.Value));
            owner.FactoryLegoQuery=snapshot.FactoryLegoQuery is {} factory?ImportDiagnostic(factory):null;
            if(owner.Graph is not null)
                owner._renderMembership=new(owner.ActiveList.Snapshot(),owner.Graph,owner.CurrentObjectStateId,snapshot.ActiveStateIds,
                    snapshot.ActiveEffectPassStateIds,new(owner.Graph,snapshot.ActiveStateIds,owner.ActiveList.Snapshot(),owner.CurrentObjectStateId));
            return owner;
        }

        // A LEGO mutation may query an earlier packet prefix. This is an event
        // checkpoint, never a per-frame publication or a renderer view subclass.
        internal ObjectState CopyAtControllerMutation()
        {
            var copy=new ObjectState(VisualGeneration,PacketActionCount,LastClientTick,CurrentObjectStateId,Graph)
            {
                IsHistoricalQuery=true,Counters=Counters,InitialObjectSceneEventCount=InitialObjectSceneEventCount,
                FactoryActionCount=FactoryActionCount,FactoryObjectStateId=FactoryObjectStateId,OwnerDeleteRequested=OwnerDeleteRequested
            };
            copy.ActiveStateIds.UnionWith(ActiveStateIds);
            foreach(int id in ActiveList.Snapshot())copy.ActiveList.Enable(id);
            copy.Actions.AddRange(Actions);copy.ActivationVirtualActionOrdinals.UnionWith(ActivationVirtualActionOrdinals);
            copy.HiddenObjectStateIds.UnionWith(HiddenObjectStateIds);copy.CompletedSounds.UnionWith(CompletedSounds);
            copy.SoundHandleReleases.AddRange(SoundHandleReleases);copy.ObjectSceneEvents.AddRange(ObjectSceneEvents);
            foreach(var item in EffectiveActionPacketOrdinals)copy.EffectiveActionPacketOrdinals.Add(item.Key,item.Value);
            foreach(var item in ObjectVisibilityTransitionCounts)copy.ObjectVisibilityTransitionCounts.Add(item.Key,item.Value);
            foreach(var item in AnimationEntryFrames)copy.AnimationEntryFrames.Add(item.Key,item.Value);
            foreach(var item in BoundAnimations)copy.BoundAnimations.Add(item.Key,item.Value);
            foreach(var item in BoundAnimationObjectStates)copy.BoundAnimationObjectStates.Add(item.Key,item.Value);
            foreach(var item in EffectPassFlags)copy.EffectPassFlags.Add(item.Key,item.Value);
            foreach(var item in AnimationFrames)copy.AnimationFrames.Add(item.Key,new(copy)
                {Frame=item.Value.Frame,PreviousFrame=item.Value.PreviousFrame,Flags=item.Value.Flags,ActivationTick=item.Value.ActivationTick});
            foreach(var item in AnimationEnvelopes)copy.AnimationEnvelopes.Add(item.Key,new(copy,item.Value.Envelope,item.Value.LastUpdateTick)
                {ContributionWeight=item.Value.ContributionWeight});
            foreach(var item in ObjectLifetimes)copy.ObjectLifetimes.Add(item.Key,new(copy){Elapsed=item.Value.Elapsed,Flags=item.Value.Flags});
            return copy;
        }
    }
}

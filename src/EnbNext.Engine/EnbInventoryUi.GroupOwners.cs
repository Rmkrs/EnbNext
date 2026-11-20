using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;
internal sealed partial class EnbInventoryUi
{
    // MainView+1D8 and StarbaseView's separately constructed +F1 group owner.
    // Switching views does not rebuild the main owner's retained visual state.
    private sealed class GroupVisualOwner
    {
        public Guid Identity=Guid.NewGuid();
        public EnbRetainedGroupMembers Members=new();
        public Dictionary<(int Index,string Kind),EnbRetailGaugeTrail> Gauges=[];
        public Dictionary<long,string> Labels=[];
        public (string Name,bool Present)[] GaugeMembers=[];
        public ulong GaugeRevision;
        public uint GaugeTime,FormationTime;
        public EnbGroupFormationVisual Formation=new();
        public EnbSceneGroupSnapshot? FormationSource;
        public EnbGroupFrameClock Clock=new();
        public EnbGroupAttentionFlash FormationAttention=new();
        public Dictionary<long,EnbGroupAttentionFlash> MemberAttention=[];
        public uint AttentionTime;
    }
    private GroupVisualOwner _spaceGroupVisual=new();
    private GroupVisualOwner? _stationGroupVisual;
    private void StoreGroupVisualOwner(GroupVisualOwner state)
    {
        state.Members=_groupMembers;state.Gauges=_groupGaugeTrails;state.GaugeMembers=_groupGaugeMembers;
        state.Labels=_groupMemberLabels;
        state.GaugeRevision=_groupGaugeRevision;state.GaugeTime=_groupGaugeTime;
        state.Formation=_groupFormationVisual;state.FormationSource=_groupFormationSource;
        state.FormationTime=_groupFormationTime;state.Clock=_groupFrameClock;
    }
    private void LoadGroupVisualOwner(GroupVisualOwner state)
    {
        _groupMembers=state.Members;_groupGaugeTrails=state.Gauges;_groupGaugeMembers=state.GaugeMembers;
        _groupMemberLabels=state.Labels;
        _groupGaugeRevision=state.GaugeRevision;_groupGaugeTime=state.GaugeTime;
        _groupFormationVisual=state.Formation;_groupFormationSource=state.FormationSource;
        _groupFormationTime=state.FormationTime;_groupFrameClock=state.Clock;
    }
    private void SwitchGroupVisualOwner(bool station)
    {
        if(StationGroup==station)return;
        if(StationGroup)
        {
            // 74BA30 releases the station view; the main view survives this close.
            _stationGroupVisual=null;LoadGroupVisualOwner(_spaceGroupVisual);
        }
        else
        {
            StoreGroupVisualOwner(_spaceGroupVisual);
            _stationGroupVisual=new();LoadGroupVisualOwner(_stationGroupVisual);
        }
    }
    private void ResetGroupVisualOwners()
    {
        _spaceGroupVisual=new();_stationGroupVisual=StationGroup?new():null;
        LoadGroupVisualOwner(_stationGroupVisual??_spaceGroupVisual);
        _receivedGroupSymbols.Clear();
    }
}

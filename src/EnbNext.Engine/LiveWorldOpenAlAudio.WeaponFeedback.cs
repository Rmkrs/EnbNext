using EnbNext.Assets;
namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    private readonly StationSound[] _weaponRangeVoices=Enumerable.Range(0,12).Select(_=>new StationSound {UseAuthoredLoopCount=true}).ToArray();
    private readonly int[] _weaponRangeRequested=new int[12],_weaponRangeActive=new int[12];
    private readonly System.Collections.Concurrent.ConcurrentQueue<(int Slot,bool Enabled)> _weaponRangeChanges=new();
    public void SetWeaponRangeSound(int slot,bool enabled)
    {
        if((uint)slot<12 && Interlocked.Exchange(ref _weaponRangeRequested[slot],enabled?1:0)!=(enabled?1:0))
            _weaponRangeChanges.Enqueue((slot,enabled));
    }
    private void StopWeaponRangeSounds()
    {
        _weaponRangeChanges.Clear();
        for(int i=0;i<12;i++)
        {StopStationSound(_weaponRangeVoices[i]);_weaponRangeActive[i]=0;Volatile.Write(ref _weaponRangeRequested[i],0);}
    }
    private void UpdateWeaponRangeSounds(EnbRetailAssetBundle assets,float gain)
    {
        const string name="weapon_range_green_1.wav";
        while(_weaponRangeChanges.TryDequeue(out var change))
        {StopStationSound(_weaponRangeVoices[change.Slot]);_weaponRangeActive[change.Slot]=change.Enabled?1:0;}
        for(int i=0;i<12;i++)
        {
            var voice=_weaponRangeVoices[i];
            if(_weaponRangeActive[i]==0)continue;
            UpdateStationSound(voice,name,assets,gain);
        }
    }
}

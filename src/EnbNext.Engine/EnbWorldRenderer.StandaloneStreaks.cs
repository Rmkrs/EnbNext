using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(uint,ulong,ulong),StandaloneEffectStreakInstance> _effectStandaloneStreakInstances=[];

    private void ApplyStandaloneEffectStreaks(List<EnbW3dDrawTask> packets,HashSet<(uint,ulong,ulong)> active,
        EnbResolvedRenderCamera camera,uint now,ref int draws,ref int blocked,ref string? failure)
    {
        foreach(var (key,entry) in _effectStandaloneStreakInstances.ToArray())
        {
            uint age=unchecked(now-entry.Node.ActualStartTick);
            var buffer=entry.Instance.StandaloneStreak!;
            try
            {
                if(!active.Contains(key))buffer.Advance(age,Matrix4x4.Identity,false);
                var samples=buffer.Sample(age);
                if(!active.Contains(key) && samples.Count==0)
                {_effectStandaloneStreakInstances.Remove(key);continue;}
                if(!EnbCurrentRetailStreakRuntime.TryBuildPacket(entry.Asset.Key,entry.Owner.RenderIdentity,entry.Owner.VisualGeneration,
                    entry.Owner.GameId,entry.Asset.Definition.Name,0,entry.Asset.Definition,samples,camera,age,out var packet,out var error,entry.Asset.Texture,entry.Asset.RuntimeTextureName,entry.Asset.RetainedTexture))
                    throw new InvalidDataException(error);
                if(packet is null)continue;
                packets.Add((packet).ForkTask().SetRenderInstancePath($"effect-standalone-streak/{key.Item1}/{key.Item2}/{key.Item3}"));
                draws++;
            }
            catch(Exception ex){blocked++;failure??=$"E{entry.Node.Definition.DefinitionId}: {ex.Message}";}
        }
    }

    private sealed record StandaloneEffectStreakInstance(EnbCurrentRetailEffectOwner Node,IEnbRenderObject Owner,
        EnbCurrentRetailStreakAttachment Asset,EffectObjectInstance Instance);
}

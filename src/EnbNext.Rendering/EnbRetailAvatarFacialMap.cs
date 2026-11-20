using EnbNext.Assets;

namespace EnbNext.Rendering;

public readonly record struct EnbRetailAvatarFaceKeys(float From,float To,float Blend)
{
    public float Frame=>From+(To-From)*Blend;
}
public readonly record struct EnbRetailAvatarFace(float Mouth,float Brow,float Eye)
{
    public EnbRetailAvatarFaceKeys? MouthKeys {get;init;}
    public EnbRetailAvatarFaceKeys? BrowKeys {get;init;}
    public EnbRetailAvatarFaceKeys? EyeKeys {get;init;}
}

/// <summary>007829B0, 00782CA0, 00782FF0: the three source time maps.</summary>
public sealed class EnbRetailAvatarFacialMap
{
    private readonly SortedDictionary<int,float> mouth=[],brow=[],eye=[];
    public int SpeechEndFrame {get;}
    public int EndFrame {get;}
    public float FinalBrow {get;}
    // 00988610 retains the greatest inserted key as +54; 00987050 returns
    // that value unchanged (not key + 1) to the head HLOD frame-count consumer.
    public int AnimationFrameCount=>Math.Max(mouth.Keys.Max(),Math.Max(brow.Keys.Max(),eye.Keys.Max()));
    public EnbRetailAvatarFacialMap(int frameCount,int speechDelay,int blinkDelay,int mood,string text,
        EnbRetailIniDocument? authored,float previousBrow=0,Func<int,int,int>? random=null,bool commandActive=false)
    {
        random??=EnbRetailPresentationRandom.Shared.Next;
        int offset=mood==3?0:mood*17;
        var phonemes=EnbStationSpeechPhonemes.Parse(text);
        mouth[0]=offset;int time=speechDelay;
        foreach(int phoneme in phonemes){mouth[time]=offset+phoneme+1;time+=speechDelay;}
        mouth[time+speechDelay]=offset;
        SpeechEndFrame=text.Length==0?-1:time+speechDelay;
        int frames=Math.Max(frameCount,SpeechEndFrame);
        EndFrame=frames-1;
        brow[0]=previousBrow;eye[0]=0;
        if(authored is not null && frameCount>=3)
        {
            int currentMood=0;
            for(int i=0;authored.TryGetSection($"Index-{i:0000}",out var key);i++)
            {
                if(!int.TryParse(key.GetValueOrDefault("Frame"),out int frame)||frame<=0||frame>=frameCount-1)continue;
                if(key.ContainsKey("Mood"))currentMood=Enum(key.GetValueOrDefault("Mood"),["Mormal","Angry","Happy","Puzzled"],0);
                int b=Enum(key.GetValueOrDefault("Brow"),["Open1","Close1","Open2","Close2","Wink"],-1);
                int e=Enum(key.GetValueOrDefault("Eye"),["Straight","Left","Fullleft","Right","Fullright"],-1);
                if(b>=0)brow[frame]=b+currentMood*5;
                if(e>=0)eye[frame]=e;
            }
            brow[frameCount-1]=mood*5;eye[frameCount-1]=0;FinalBrow=mood*5;
        }
        else
        {
            int delay=Math.Max(1,blinkDelay);
            int count=frames<delay?random(0,1):Math.Min(random(1,frames/delay),frames/4);
            int interval=count>0?frames/count:frames;
            int neutral=mood*5+(random(0,commandActive||text.Length>0?1:3)==1?2:0);
            for(int i=0;i<count;i++)
            {
                int start=random(2+i*interval,2+i*interval-7+interval);
                brow[start]=neutral;brow[start+2]=neutral+1;brow[start+4]=neutral;
            }
            brow[EndFrame]=neutral;FinalBrow=neutral;
            for(int i=0;i<count;i++)
            {
                int direction=random(0,1)*2+1,duration=random(2,interval/4);
                int start=random(i*interval+1,(i+1)*interval-duration-4);
                eye[start]=0;eye[start+2]=direction;eye[start+2+duration]=direction;eye[start+4+duration]=0;
            }
            eye[EndFrame]=0;
        }
    }
    private static int Enum(string? text,string[] names,int fallback)
    {int i=Array.FindIndex(names,n=>n.Equals(text,StringComparison.OrdinalIgnoreCase));return i<0?fallback:i;}
    public EnbRetailAvatarFace Sample(float frame)
    {
        var m=Sample(mouth,frame);var b=Sample(brow,frame);var e=Sample(eye,frame);
        return new(m.Frame,b.Frame,e.Frame){MouthKeys=m,BrowKeys=b,EyeKeys=e};
    }
    // 00986CD0 returns two source pose indices and a fraction. 00988360 /
    // 00988410 evaluate both endpoints before blending their transforms.
    private static EnbRetailAvatarFaceKeys Sample(SortedDictionary<int,float> keys,float frame)
    {
        var previous=keys.First();
        if(frame<previous.Key)return new(previous.Value,previous.Value,0);
        foreach(var key in keys.Skip(1))
        {
            if(frame<key.Key)return new(previous.Value,key.Value,(frame-previous.Key)/(key.Key-previous.Key));
            previous=key;
        }
        return new(previous.Value,previous.Value,0);
    }
}

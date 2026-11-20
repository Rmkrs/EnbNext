using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbRetailAvatarSpeechCommand(string AnimationTag,string Text,int Mood=-1)
{
    public int? AnimationNumber { get; init; }
}
public sealed record EnbRetailAvatarNlpRecord(byte Type,byte Argument,string? Text=null);

/// <summary>0078F050/0078E980/0078EEF0 and the stationary 00784700 command consumer.</summary>
public sealed class EnbRetailAvatarSpeech
{
    private sealed record Phrase(string Text,string[] Contexts);
    private readonly Dictionary<string,int> actions=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,int> moods=new(StringComparer.OrdinalIgnoreCase);
    private readonly Phrase[] phrases;
    public int SpeechDelay {get;}
    public int FinalMood {get;private set;}=-1;
    public EnbRetailAvatarSpeech(EnbRetailIniDocument personality,EnbRetailIniDocument lexicon)
    {
        SpeechDelay=personality.TryGetSection("HEADER",out var header)?Integer(header,"SpeechDelay",3):3;
        for(int i=0;i<4;i++)
            if(personality.TryGetSection($"MOOD-{i}",out var mood))moods[mood.GetValueOrDefault("MoodName","")]=Integer(mood,"MoodNumber",0);
        for(int i=0;personality.TryGetSection($"BANIM-{i}",out var action);i++)
            actions[action.GetValueOrDefault("Tag","")]=Math.Min(255,Integer(action,"AnimNumber",0));
        var all=new List<Phrase>();
        for(int i=0;lexicon.TryGetSection($"LEXICON-{i}",out var entry);i++)
            for(int j=0;entry.TryGetValue($"Phrase{j}",out var text);j++)
                if(text.Length>0)all.Add(new(text,new[]{"ContextBegin","ContextMiddle","ContextEnd","ContextAlone"}.Select(k=>entry.GetValueOrDefault(k,"")).ToArray()));
        // 0078E870 sorts lexically; 0078E980 searches each bucket backwards.
        phrases=all.OrderByDescending(p=>p.Text,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static int Integer(IReadOnlyDictionary<string,string> section,string key,int fallback)
        =>int.TryParse(section.GetValueOrDefault(key),out int value)?value:fallback;
    public string? Talk(int frames,Func<int,int,int> random,bool seated=false)
    {
        string tag=TalkTag(frames,random,seated);
        return actions.TryGetValue(tag,out int id)&&unchecked((byte)id)!=0?tag:null;
    }
    private static string TalkTag(int frames,Func<int,int,int> random,bool seated=false)
    {
        int[] thresholds=[7,15,30,45,60];int index=Array.FindIndex(thresholds,t=>frames<t);
        return (seated?"sit_talk":random(0,10)<3?"talk":"idle_talk")+(index<0?5:index+1);
    }
    // 00781760 ->0078F050 produces wire records, not the stationary command
    // consumer's merged moods/terminal breathe. The caller owns finalization.
    public IReadOnlyList<EnbRetailAvatarNlpRecord> Produce(string text,Func<int,int,int>? random=null)
        => Produce(text,random??EnbRetailPresentationRandom.Shared.Next,s=>EnbStationSpeechPhonemes.Parse(s).Length);
    internal IReadOnlyList<EnbRetailAvatarNlpRecord> Produce(string text,Func<int,int,int> random,Func<string,int> phonemes)
    {
        FinalMood=-1;
        int nul=text.IndexOf('\0');if(nul>=0)text=text[..nul];
        List<EnbRetailAvatarNlpRecord> records=[];
        int start=0,at=0;
        // 0078EEF0 looks the chosen talk/idle_talk tag up in the personality; an absent tag reports an assertion and
        // then reads through NULL+4. That native read is undefined, so the safety rule stops here and keeps the records
        // already produced.
        bool stopped=false;
        int? Speech(string words)
            =>actions.TryGetValue(TalkTag((phonemes(words)+3)*SpeechDelay,random),out int id)?id:null;
        void Text(int end,bool punctuation=false)
        {
            string words=text[start..end];int length=words.Length-(punctuation?1:0);
            if(length==0){records.Add(new(1,0,words));return;}
            if(Speech(words[..length]) is not int id){stopped=true;return;}
            records.Add(new(1,unchecked((byte)id),words));
        }
        int Next(int position)
        {
            int next=position;
            while(next<text.Length && text[next]!=' ' && !".!?,".Contains(text[next]))next++;
            while(next<text.Length && text[next]==' ')next++;
            return next!=position && next<text.Length?next:text.Length;
        }
        while(at<text.Length && !stopped)
        {
            if(".!?,".Contains(text[at])){Text(at+1,true);start=++at;continue;}
            if(text[at]=='/')
            {
                int end=text.IndexOf(' ',at);end=end<0?text.Length:end+1;
                string token=text.Substring(at+1,Math.Min(63,end-at-1)).TrimEnd(' ');
                bool action=actions.TryGetValue(token,out int id),mood=moods.TryGetValue(token,out int value)&&value>=0;
                if(action||mood)
                {
                    if(text[start..at].Any(c=>c!=' ')){Text(at);if(stopped)break;}
                    if(action)records.Add(new(2,unchecked((byte)id)));
                    else {FinalMood=value;records.Add(new(0,unchecked((byte)value)));}
                    start=at=end;continue;
                }
                at=end;continue;
            }
            var phrase=text[at]>=128?null:phrases.FirstOrDefault(p=>text.AsSpan(at).StartsWith(p.Text,StringComparison.OrdinalIgnoreCase)&&
                (at+p.Text.Length==text.Length || text[at+p.Text.Length] is <'a' or >'z'));
            if(phrase is not null)
            {
                int end=at+phrase.Text.Length;
                int context=at==0?(Next(end)<text.Length?0:3):(Next(end)<text.Length?1:2);
                string tag=phrase.Contexts[context];
                if(!tag.Equals("none",StringComparison.OrdinalIgnoreCase)&&actions.TryGetValue(tag,out int id))
                {records.Add(new(1,unchecked((byte)id),text[start..end]));start=at=end;continue;}
            }
            at=Next(at);
        }
        if(!stopped && start<text.Length)Text(text.Length);
        return records;
    }
    public IReadOnlyList<EnbRetailAvatarSpeechCommand> Parse(string text,Func<int,int,int>? random=null,bool terminalBreathe=false)
    {
        FinalMood=-1;
        random??=EnbRetailPresentationRandom.Shared.Next;
        int nul=text.IndexOf('\0');if(nul>=0)text=text[..nul];
        var result=new List<EnbRetailAvatarSpeechCommand>();int start=0,at=0,pendingMood=-1;
        void Emit(string? tag,string words)
        {
            if(tag is null || !actions.TryGetValue(tag,out int id)||unchecked((byte)id)==0)return;
            result.Add(new(tag,words,pendingMood));pendingMood=-1;
        }
        // 784A50 runs the same 0078F050 producer before 784700 consumes; an absent talk tag stops as in Produce.
        bool stopped=false;
        void Text(int end,bool punctuation=false)
        {
            string words=text[start..end];
            int speechLength=punctuation?words.Length-1:words.Length;
            if(speechLength<=0)return;
            string tag=TalkTag((EnbStationSpeechPhonemes.Parse(words[..speechLength]).Length+3)*SpeechDelay,random);
            if(!actions.ContainsKey(tag)){stopped=true;return;}
            Emit(tag,words);
        }
        int Next(int position)
        {
            int next=position;
            while(next<text.Length && text[next]!=' ' && !".!?,".Contains(text[next]))next++;
            while(next<text.Length && text[next]==' ')next++;
            return next!=position&&next<text.Length?next:text.Length;
        }
        while(at<text.Length && !stopped)
        {
            if(".!?,".Contains(text[at])){Text(at+1,true);start=++at;continue;}
            if(text[at]=='/')
            {
                int end=text.IndexOf(' ',at);end=end<0?text.Length:end+1;
                string token=text.Substring(at+1,Math.Min(63,end-at-1)).TrimEnd(' ');
                bool action=actions.ContainsKey(token),mood=moods.TryGetValue(token,out int value)&&value>=0;
                if(action||mood)
                {
                    if(text[start..at].Any(c=>c!=' ')){Text(at);if(stopped)break;}
                    if(action)Emit(token,"");else {pendingMood=value;FinalMood=value;}
                    start=at=end;continue;
                }
                at=end;continue; // Unknown slash remains part of the text span.
            }
            var phrase=text[at]>=128?null:phrases.FirstOrDefault(p=>text.AsSpan(at).StartsWith(p.Text,StringComparison.OrdinalIgnoreCase)&&
                (at+p.Text.Length==text.Length || text[at+p.Text.Length] is <'a' or >'z'));
            if(phrase is not null)
            {
                int end=at+phrase.Text.Length;
                int context=at==0?(Next(end)<text.Length?0:3):(Next(end)<text.Length?1:2);
                string tag=phrase.Contexts[context];
                if(!tag.Equals("none",StringComparison.OrdinalIgnoreCase)&&actions.ContainsKey(tag))
                {Emit(tag,text[start..end]);start=at=end;continue;}
            }
            at=Next(at);
        }
        if(!stopped && start<text.Length)Text(text.Length);
        // Stationary commands preserve mood until the next action; a trailing
        // mood gets the native terminal breathe1 command (00784940).
        if(pendingMood>=0 || terminalBreathe)Emit("breathe1","");
        return result;
    }
}

/// <summary>007854B0: 20 Hz smoothing, interval counter once per nonzero call.</summary>
public sealed class EnbRetailAvatarHeadMotion
{
    public float Remainder {get;private set;}
    public uint Calls {get;private set;}
    public uint NextTarget {get;private set;}
    public float Yaw {get;private set;}
    public float Pitch {get;private set;}
    private float targetYaw,targetPitch;
    public void ClearTargets(){targetYaw=targetPitch=0;}
    public void Advance(float seconds,Func<int,int,int> random,float lookYaw=0,float lookPitch=0)
    {
        Remainder+=seconds;int steps=(int)(Remainder*20);
        if(steps==0)return;
        Remainder-=steps*.05f;
        if(Calls>=NextTarget){NextTarget=Calls+(uint)random(5,30);targetYaw=random(-10,10);targetPitch=random(-5,5);}
        Calls++;
        for(int i=0;i<steps;i++){Yaw=.95f*Yaw+.05f*(targetYaw+lookYaw);Pitch=.95f*Pitch+.05f*(targetPitch+lookPitch);}
    }
    public System.Numerics.Matrix4x4 Matrix
    {
        get
        {
            double radians=BitConverter.Int64BitsToDouble(0x3F91DF46AAAAAAAB); // 3F91DF46AAAAAAAB
            float cy=(float)Math.Cos(Yaw*radians),sy=(float)Math.Sin(Yaw*radians),cp=(float)Math.Cos(Pitch*radians),sp=(float)Math.Sin(Pitch*radians);
            return new(cy*cp,sy*cp,-sp,0,-sy,cy,0,0,cy*sp,sy*sp,cp,0,0,0,0,1);
        }
    }
}

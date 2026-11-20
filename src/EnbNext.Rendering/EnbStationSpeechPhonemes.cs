namespace EnbNext.Rendering;

/// <summary>Current retail 00791400, 007917B0..00791F80 and table 00B9DAB8.</summary>
public static class EnbStationSpeechPhonemes
{
    private static readonly (string Text,int A,int B)[] Substitutions = new (string,int,int)[]
    {
        ("air",2,-1),("ar",3,-1),("ch",8,-1),("ea",1,-1),("ee",1,-1),("er",15,-1),
        ("oa",3,-1),("oo",4,-1),("ou",3,4),("ow",4,0),("qu",4,-1),("sch",14,12),
        ("sh",8,-1),("tch",8,-1),("th",10,-1),("gn",13,-1),("kn",13,-1),("eye",2,-1),
        ("uy",2,-1),("kw",4,-1),("and",13,11),("ze",6,1),("ro",4,3),("one",4,0),
        ("two",1,4),("thr",10,6),("fou",7,3),("ive",2,7),("six",5,2),("se",5,1),
        ("ven",7,1),("eight",0,1),("ni",10,2),("ne",10,-1)
    }.OrderByDescending(x=>x.Item1,StringComparer.Ordinal).ToArray();

    public static int[] Parse(string text)
    {
        text=text[..Math.Min(text.Length,1023)].ToLowerInvariant();
        List<int> result=[];
        for(int i=0;i<text.Length && result.Count<255;)
        {
            int a=11,b=-1,count=1;
            var match=Substitutions.FirstOrDefault(s=>text.AsSpan(i).StartsWith(s.Text,StringComparison.Ordinal));
            if(match.Text is not null){a=match.A;b=match.B;count=match.Text.Length;}
            else
            {
                char next=At(1),after=At(2),previous=At(-1);
                switch(text[i])
                {
                    case 'a':
                        if(Vowel(next))
                        {
                            count=2;
                            if(next=='i'){b=1;if(after=='s')a=0;}
                            else if(next=='o')b=1;
                            else if(next=='u')
                            {if(previous=='g')b=1;else {a=0;if(previous=='s')b=4;}}
                        }
                        else if(Vowel(after))b=1;
                        else a=0;
                        break;
                    case 'e':
                        a=1;
                        if(!Vowel(next)){if(previous!='b' && next!='\0')a=4;}
                        else
                        {
                            count=2;
                            if(next=='a' && after=='u'){b=4;count=3;}
                            else if(next=='i')
                            {if(previous=='h'){a=0;b=1;}else if(after=='g'){a=11;b=1;}else a=after=='z'?1:2;}
                            else if(next=='o')a=previous=='p'?1:11;
                            else if(next=='u')a=4;
                        }
                        break;
                    case 'i':
                        a=2;
                        if(Vowel(next))
                        {
                            count=2;a=11;
                            if(next=='e')
                            {if(after=='\0' && previous=='l'){a=0;b=1;}else if(after is 'u' or 'w'){a=4;count=3;}else a=1;}
                            else if(next=='o')a=1;
                        }
                        break;
                    case 'o':
                        a=3;
                        if(Vowel(next))
                        {
                            count=2;a=5;
                            if(next=='a'){if(!(At(-2)=='b' && previous=='r')){a=3;b=4;}}
                            else if(next=='e'){a=4;if(previous=='d'&&after=='s')a=5;else if(At(-2)=='p'&&previous=='h')a=1;}
                            else if(next=='i')b=1;
                            else if(next=='o')a=4;
                            else if(next=='u'){a=4;if(At(-2)=='t'&&previous=='r')a=5;else if(after=='r'){a=11;count=3;}}
                        }
                        else if(next!='\0' && Vowel(after))a=4;
                        break;
                    case 'u': a=5;if(Vowel(next)){count=2;if(next is 'e' or 'i')a=4;}break;
                    case 'b':case 'm':case 'p':a=9;break;
                    case 'c':case 'g':case 'k':case 'q':a=12;break;
                    case 'd':case 't':a=11;break;
                    case 'f':case 'v':a=7;break;
                    case 'j':a=8;break;
                    case 'l':a=10;break;
                    case 'n':a=13;break;
                    case 'r':a=6;break;
                    case 's':a=14;if(next is 'h' or 'u')a=8;else if(next=='c'){a=after=='h'?8:11;count=after=='h'?3:2;if(after=='h')b=11;}break;
                    case 'w':a=4;break;
                    case 'x':a=12;b=14;break;
                    case 'z':a=14;break;
                }
                char At(int offset)=>i+offset>=0&&i+offset<text.Length?text[i+offset]:'\0';
            }
            Add(a);Add(b);i+=count;
        }
        return result.ToArray();
        void Add(int value){if(value>=0 && result.Count<255 && (result.Count==0||result[^1]!=value))result.Add(value);}
    }
    private static bool Vowel(char c)=>c is 'a' or 'e' or 'i' or 'o' or 'u';
}

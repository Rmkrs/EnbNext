using SkiaSharp;
namespace EnbNext.Host.Sdl;
internal sealed unsafe partial class SdlUiGpuSurface
{
    private sealed class StatusText : IDisposable
    {
        private readonly SKTypeface face=SKTypeface.FromFamilyName("Inter");
        private readonly SKFont font;
        private readonly HarfBuzzSharp.Font shaper;
        private SKTextBlob?[] lines=[];
        private string text="";private float width,widest;
        /// <summary>The widest line's width.</summary>
        internal float Width=>widest;
        internal StatusText(float size=14)
        {
            font=new(face,size){Edging=SKFontEdging.SubpixelAntialias,Subpixel=true,Hinting=SKFontHinting.Full,BaselineSnap=true};
            using var stream=face.OpenStream(out int index);using var data=SKData.Create(stream,(ulong)stream.Length);
            using var blob=new HarfBuzzSharp.Blob(data.Data,(int)data.Size,HarfBuzzSharp.MemoryMode.Duplicate);
            using var hbFace=new HarfBuzzSharp.Face(blob,(uint)index);shaper=new(hbFace);shaper.SetScale(face.UnitsPerEm,face.UnitsPerEm);shaper.SetFunctionsOpenType();
        }
        private (ushort[] Glyphs,SKPoint[] Points,float Width) Shape(string value)
        {
            using var buffer=new HarfBuzzSharp.Buffer();buffer.AddUtf16(value);buffer.GuessSegmentProperties();shaper.Shape(buffer);
            var info=buffer.GlyphInfos;var positions=buffer.GlyphPositions;var glyphs=new ushort[info.Length];var points=new SKPoint[info.Length];double x=0;double scale=font.Size/face.UnitsPerEm;
            for(int i=0;i<info.Length;i++){glyphs[i]=(ushort)info[i].Codepoint;points[i]=new((float)(x+positions[i].XOffset*scale),(float)(-positions[i].YOffset*scale));x+=positions[i].XAdvance*scale;}
            return(glyphs,points,(float)x);
        }
        internal void Use(string value,float available)
        {
            if(text==value&&width==available)return;text=value;width=available;foreach(var line in lines)line?.Dispose();
            var result=new List<SKTextBlob?>();int start=0;widest=0;
            while(start<value.Length)
            {
                int end=start,lastBreak=-1;
                while(end<value.Length&&value[end]!='\n')
                {
                    int next=end+(char.IsHighSurrogate(value[end])&&end+1<value.Length?2:1);
                    if(end>start&&Shape(value[start..next]).Width>available)break;
                    end=next;if(char.IsWhiteSpace(value[end-1]))lastBreak=end;
                }
                if(end<value.Length&&value[end]!='\n'&&lastBreak>start)end=lastBreak;
                var run=Shape(value[start..end]);widest=Math.Max(widest,run.Width);using var builder=new SKTextBlobBuilder();var output=builder.AllocatePositionedRun(font,run.Glyphs.Length);output.SetGlyphs(run.Glyphs);output.SetPositions(run.Points);result.Add(builder.Build());
                start=end;if(start<value.Length&&value[start]=='\n')start++;
            }
            lines=result.ToArray();
        }
        internal float Height=>lines.Length*font.Spacing;
        internal void Draw(SKCanvas canvas,float x,float y,SKPaint paint)
        {float baseline=y-font.Metrics.Ascent;foreach(var line in lines){if(line is not null)canvas.DrawText(line,x,baseline,paint);baseline+=font.Spacing;}}
        public void Dispose(){foreach(var line in lines)line?.Dispose();shaper.Dispose();font.Dispose();face.Dispose();}
    }
}

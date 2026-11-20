namespace EnbNext.Presentation;

/// <summary>Logical authored UI coordinates; conversion to physical pixels belongs to the draw consumer.</summary>
public readonly record struct EnbUiSize(double Width, double Height)
{
    public bool Equals(EnbUiSize other) => Width == other.Width && Height == other.Height;
    public override int GetHashCode() => HashCode.Combine(Width,Height);
}
public readonly record struct EnbUiPoint(double X, double Y)
{
    public static EnbUiPoint operator +(EnbUiPoint a,EnbUiPoint b)=>new(a.X+b.X,a.Y+b.Y);
    public static EnbUiPoint operator -(EnbUiPoint a,EnbUiPoint b)=>new(a.X-b.X,a.Y-b.Y);
}
public readonly record struct EnbUiRect(double X, double Y, double Width, double Height)
{
    public EnbUiRect(EnbUiPoint topLeft, EnbUiPoint bottomRight)
        : this(topLeft.X, topLeft.Y, bottomRight.X-topLeft.X, bottomRight.Y-topLeft.Y) { }
    public bool Equals(EnbUiRect other) => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
    public override int GetHashCode() => HashCode.Combine(X,Y,Width,Height);
    public bool Intersects(EnbUiRect other) => other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;
    public bool Contains(EnbUiPoint point) => point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;
    public EnbUiRect Translate(EnbUiPoint offset)=>Translate(offset.X,offset.Y);
    public EnbUiRect Translate(double dx, double dy) => new(X+dx,Y+dy,Width,Height);
    public EnbUiRect Inflate(double x, double y) => new(X-x,Y-y,Math.Max(0,Width+2*x),Math.Max(0,Height+2*y));
    public EnbUiRect Deflate(double value) => Inflate(-value,-value);
    public EnbUiPoint TopLeft => new(X,Y);
    public EnbUiPoint BottomRight => new(Right,Bottom);
    public EnbUiRect Intersect(EnbUiRect other)
    {
        double left=Math.Max(X,other.X),top=Math.Max(Y,other.Y),right=Math.Min(Right,other.Right),bottom=Math.Min(Bottom,other.Bottom);
        return right>=left && bottom>=top ? new(left,top,right-left,bottom-top) : default;
    }
    public EnbUiRect Union(EnbUiRect other)
    {
        if (Width == 0 && Height == 0) return other;
        if (other.Width == 0 && other.Height == 0) return this;
        double left=Math.Min(X,other.X), top=Math.Min(Y,other.Y);
        return new(left,top,Math.Max(Right,other.Right)-left,Math.Max(Bottom,other.Bottom)-top);
    }
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Left => X;
    public double Top => Y;
    public EnbUiPoint Center => new(X + Width / 2, Y + Height / 2);
    public EnbUiSize Size => new(Width, Height);
}

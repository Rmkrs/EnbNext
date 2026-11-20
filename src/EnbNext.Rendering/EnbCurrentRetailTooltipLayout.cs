namespace EnbNext.Rendering;

/// <summary>006ABE90 cursor anchor and 00517C50 viewport clamp, in top-left coordinates.</summary>
public static class EnbCurrentRetailTooltipLayout
{
    public static (double X, double Y) Place(double pointerX, double pointerY,
        double width, double height, double viewportWidth, double viewportHeight)
    {
        double sx = viewportWidth / 640, sy = viewportHeight / 480;
        // Retail UI is Y-up; center = cursor + half-size - 3 in each axis.
        // The dynamic window then clamps its edges to a 5-unit screen margin.
        double x=pointerX-3*sx,y=pointerY-height+3*sy;
        // 517C50 checks the leading edge first, otherwise the trailing edge.
        // This ordering also applies when a framed tooltip exceeds the viewport.
        if(x<5*sx)x=5*sx;else if(x+width>viewportWidth-5*sx)x=viewportWidth-5*sx-width;
        if(y<5*sy)y=5*sy;else if(y+height>viewportHeight-5*sy)y=viewportHeight-5*sy-height;
        return(x,y);
    }
}

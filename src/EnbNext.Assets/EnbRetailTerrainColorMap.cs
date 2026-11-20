namespace EnbNext.Assets;

/// <summary>00469777..00469972: integer block reduction of land color and illumination.</summary>
public static class EnbRetailTerrainColorMap
{
    /// <summary>4696B9..469746's BGR plane, after the authored TGA flip and
    /// optional reduction. The texture decoder exposes top-origin RGBA.</summary>
    public static byte[] CreatePrelightPlane(EnbTextureImage image,int width,bool flipRows)
    {
        if(width<=0||image.Width!=width||image.Height<width)
            throw new InvalidDataException("Terrain color map does not cover the height field.");
        var bytes=new byte[checked(width*width*3)];
        for(int y=0;y<width;y++)for(int x=0;x<width;x++)
        {
            var color=image.GetPixel(x,flipRows?image.Height-1-y:y);
            int offset=(y*width+x)*3;
            bytes[offset]=color.B;bytes[offset+1]=color.G;bytes[offset+2]=color.R;
        }
        return bytes;
    }

    public static EnbTextureImage Reduce(EnbTextureImage image, int width, bool flipRows)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (image.Width <= width) return image;
        int factor = image.Width / width;
        if (image.Height < checked(width * factor))
            throw new InvalidDataException("Terrain color map is shorter than its sampled square.");
        uint divisor = (uint)(factor * factor);
        var pixels = new EnbColor32[checked(width * width)];
        for (int y = 0; y < width; y++)
        for (int x = 0; x < width; x++)
        {
            uint r=0,g=0,b=0,a=0;
            for (int dy = 0; dy < factor; dy++)
            for (int dx = 0; dx < factor; dx++)
            {
                int row=y*factor+dy;
                // The decoder exposes top-origin rows. Reduce in the row order
                // consumed by land, including cropping after its authored flip.
                var c=image.GetPixel(x*factor+dx,flipRows?image.Height-1-row:row);
                r=unchecked(r+c.R);g=unchecked(g+c.G);b=unchecked(b+c.B);a=unchecked(a+c.A);
            }
            int outputRow=flipRows?width-1-y:y;
            pixels[outputRow*width+x]=new((byte)(r/divisor),(byte)(g/divisor),(byte)(b/divisor),(byte)(a/divisor));
        }
        return new(width,width,image.Format,pixels){TgaPixelDepth=image.TgaPixelDepth};
    }
}

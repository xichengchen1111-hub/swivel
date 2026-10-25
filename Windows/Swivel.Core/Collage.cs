using System;
using System.Collections.Generic;

namespace Swivel.Core;

public enum CollageStyle { Grid, Row, Column, Featured }
public sealed record CollageSettings(int Width=2400,int Height=1800,int Gap=24,CollageStyle Style=CollageStyle.Grid,bool Fill=false,uint Background=0xffffff)
{
    public IReadOnlyList<PixelCrop> Layout(int count)
    {
        if(count is <2 or >12)throw new UserError("Choose 2 to 12 images for a collage.");
        if(Width is <256 or >4000 || Height is <256 or >4000 || Gap is <0 or >100 || !Enum.IsDefined(Style) || Background>0xffffff)
            throw new UserError("Collage dimensions must be 256–4000 pixels, with a gap of 0–100 pixels.");
        ImageRules.Validate(Width,Height);
        if (Style == CollageStyle.Featured)
        {
            int mainWidth = (Width-3*Gap)/2, available = Height-2*Gap;
            int columnsRight = count > 6 ? 2 : 1, rowsRight = (count-1+columnsRight-1)/columnsRight;
            int rightX = 2*Gap+mainWidth, rightWidth = Width-Gap-rightX;
            int smallWidth = (rightWidth-(columnsRight-1)*Gap)/columnsRight, smallHeight = (available-(rowsRight-1)*Gap)/rowsRight;
            if (mainWidth < 16 || available < 16 || smallWidth < 16 || smallHeight < 16) throw new UserError("The collage gap leaves too little room for the images.");
            var featured = new PixelCrop[count]; featured[0] = new(Gap,Gap,mainWidth,available);
            for (int i = 1; i < count; i++) featured[i] = new(rightX+(i-1)%columnsRight*(smallWidth+Gap),Gap+(i-1)/columnsRight*(smallHeight+Gap),smallWidth,smallHeight);
            foreach (var rect in featured) rect.Validate(Width,Height);
            return Array.AsReadOnly(featured);
        }
        int columns=Style==CollageStyle.Row?count:Style==CollageStyle.Column?1:(int)Math.Ceiling(Math.Sqrt(count));
        int rows=(count+columns-1)/columns;
        int cellWidth=(Width-Gap*(columns+1))/columns,cellHeight=(Height-Gap*(rows+1))/rows;
        if(cellWidth<16 || cellHeight<16)throw new UserError("The collage gap leaves too little room for the images.");
        var frames=new PixelCrop[count];
        for(int i=0;i<count;i++)
        {
            int row=i/columns,col=i%columns,remaining=Math.Min(columns,count-row*columns);
            int used=remaining*cellWidth+(remaining-1)*Gap;
            frames[i]=new((Width-used)/2+col*(cellWidth+Gap),Gap+row*(cellHeight+Gap),cellWidth,cellHeight);
            frames[i].Validate(Width,Height);
        }
        return Array.AsReadOnly(frames);
    }
}

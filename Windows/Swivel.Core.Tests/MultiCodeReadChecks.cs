using System;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class MultiCodeReadChecks
{
    internal static (string Text,PixelImage Image)[] Fixtures()=>new[]{"FIRST 123","SECOND 456","SAME PAYLOAD","THIRD 789"}
        .Select(text=>(text,QrFixtureEncoder.Render(text))).ToArray();

    internal static PixelImage Canvas(int width,int height,params (PixelImage Image,int X,int Y)[] items)
    {
        var output=Enumerable.Repeat((byte)255,width*height*4).ToArray();
        foreach(var (image,x,y) in items)for(int row=0;row<image.Height;row++)
            Buffer.BlockCopy(image.Bgra,row*image.Width*4,output,((row+y)*width+x)*4,image.Width*4);
        return new(width,height,output);
    }

    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        int count=0;void Verify(bool value,string name){check(value,name);count++;}
        var fixtures=Fixtures();
        foreach(var (text,image) in fixtures)
        {
            var strict=QrFlatRead.Read(image,default);var all=QrPhotoRead.ReadAll(image,default);
            Verify(strict.Text==text&&all.Count==1&&all[0].Qr==strict,"Independent exact QR retains every strict result field");
        }
        var scene=Canvas(650,450,(fixtures[1].Image,300,20),(fixtures[0].Image,20,20),(fixtures[2].Image,20,230),(fixtures[2].Image,300,230));
        var before=(byte[])scene.Bgra.Clone();var codes=QrPhotoRead.ReadAll(scene,default);
        Verify(codes.Select(c=>c.Text).SequenceEqual(new[]{fixtures[0].Text,fixtures[1].Text,fixtures[2].Text,fixtures[2].Text}),"Several independent QRs read top-to-bottom then left-to-right, retaining spatial duplicates");
        Verify(codes.All(c=>c.Type=="QR"&&c.Qr?.Photo==true)&&scene.Bgra.SequenceEqual(before),"Multi QR sampling reports provenance and never mutates pixels");
        var damaged=fixtures[0].Image with{Bgra=(byte[])fixtures[0].Image.Bgra.Clone()};
        for(int y=12;y<48;y++)for(int x=12;x<48;x++){int p=(y*damaged.Width+x)*4;damaged.Bgra[p]=damaged.Bgra[p+1]=damaged.Bgra[p+2]=255;}
        var partial=QrPhotoRead.ReadAll(Canvas(650,180,(damaged,20,20),(fixtures[1].Image,300,20)),default);
        Verify(partial.Count==1&&partial[0].Text==fixtures[1].Text,"A failed QR candidate does not hide the other code");
        foreach(var level in Enum.GetValues<QrCorrection>())foreach(int scale in new[]{1,3})
        {
            var exact=QrReadChecks.Image(QrCode.Encode("exact Unicode 中文",level),scale);var strict=QrFlatRead.Read(exact,default);
            Verify(QrPhotoRead.ReadAll(exact,default).Single().Qr==strict,"Existing exact flat output remains record-identical for every level");
        }
        foreach(var level in Enum.GetValues<QrCorrection>())foreach(int length in new[]{30,100,300,800,QrCode.Limit(level)})foreach(int scale in new[]{1,3})
        {
            var exact=QrReadChecks.Image(QrCode.Encode(new string('x',length),level),scale);
            Verify(ImageCodeRead.Read(exact,default).Single().Qr==QrFlatRead.Read(exact,default),"Large and maximum-size exact QR results retain every field through the all-code reader");
        }
        foreach(int turn in new[]{0,1,2,3})
        {
            var rotated=LinearBarcodeReadChecks.Rotate(scene,turn);var found=ImageCodeRead.Read(rotated,default);
            Verify(found.Count==4&&found.Count(c=>c.Text==fixtures[2].Text)==2,"Four QR symbols survive right-angle rotation without value deduplication");
            Verify(found.SequenceEqual(found.OrderBy(c=>c.Y).ThenBy(c=>c.X).ThenBy(c=>c.Type,StringComparer.Ordinal)),"Rotated QR results use source-raster reading order");
        }
        var rough=LinearBarcodeReadChecks.Degrade(scene,true,16);
        Verify(QrPhotoRead.ReadAll(rough,default).Count==4,"Several independent QR symbols survive mild blur and deterministic noise");
        var qrPair=Canvas(650,180,(fixtures[0].Image,20,20),(fixtures[1].Image,300,20));
        foreach(var (type,text) in new[]{("EAN-13","5901234123457"),("UPC-A","036000291452"),("EAN-8","96385074"),("Code 128","Swivel-128 / 42"),("Code 39","SWIVEL-39"),("ITF","12345670")})
        {
            var bars=LinearBarcodeReadChecks.Render(type,text);
            var mixed=Canvas(Math.Max(650,bars.Width+40),350,(qrPair,0,0),(bars,20,220));
            foreach(int turn in new[]{0,1,2,3})
            {
                var found=ImageCodeRead.Read(LinearBarcodeReadChecks.Rotate(mixed,turn),default);
                Verify(found.Count==3&&found.Count(c=>c.Type=="QR")==2&&found.Any(c=>c.Type==type&&c.Text==text),"Independent QR pair plus "+type+" reads every symbol at "+turn*90+" degrees");
            }
        }
        var grid=Enumerable.Range(0,20).Select(i=>(fixtures[i%4].Image,(i%5)*140,(i/5)*140)).ToArray();
        var bounded=ImageCodeRead.Read(Canvas(700,560,grid),default);
        Verify(bounded.Count==ImageCodeRead.MaxCodes,"More than sixteen independent QR symbols obey the shared result cap: "+bounded.Count);
        Verify(ImageCodeRead.Read(Canvas(400,300),default).Count==0,"Empty image returns no codes through the combined reader");
        var huge=Canvas(4000,4000);var watch=System.Diagnostics.Stopwatch.StartNew();
        Verify(ImageCodeRead.Read(huge,default).Count==0,"A valid 16-megapixel empty image completes with no false code");
        Console.WriteLine($"Combined code reader 16 MP blank: {watch.Elapsed.TotalSeconds:F2}s (core only)");
        using(var cancel=new CancellationTokenSource())
        {
            cancel.CancelAfter(1);bool stopped=false;
            try{ImageCodeRead.Read(huge,cancel.Token);}catch(OperationCanceledException){stopped=true;}
            Verify(stopped,"Cancellation requested during a 16-megapixel read interrupts actual work");
        }
        reject(()=>QrPhotoRead.ReadAll(scene,new CancellationToken(true)),"Multi QR pre-cancellation is real");count++;
        reject(()=>QrPhotoRead.ReadAll(new(4001,4001,new byte[4]),default),"Multi QR huge input is refused before allocation");count++;
        Console.WriteLine($"Multi-code QR checks: {count}");
    }
}

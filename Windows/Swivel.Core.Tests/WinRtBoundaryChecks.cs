using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;

internal static class WinRtBoundaryChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        using(var inner=new MemoryStream(new byte[]{1,2,3,4},true))
        {
            using(var view=new BoundedStream(inner,4,false,default)){check(view.CanRead&&!view.CanWrite&&view.Length==4,"Read-only WinRT view facts");reject(()=>view.WriteByte(1),"Read-onlyview never becomes writable");reject(()=>view.SetLength(0),"ReadonlySetLength refused");reject(()=>view.Position=5,"Source seek beyond length refused");check(view.ReadByte()==1&&view.Seek(-1,SeekOrigin.End)==3&&view.ReadByte()==4,"Source seeks/reads exact underlying bytes");}
            check(inner.CanRead&&inner.Length==4,"Adapter dispose retains caller lease");
        }
        var rng=new Random(500);
        for(int caseNo=0;caseNo<100;caseNo++)
        {
            using var actual=new MemoryStream();using var expected=new MemoryStream();using var bounded=new BoundedStream(actual,128,true,default);
            for(int op=0;op<100;op++)
            {
                int position=rng.Next(129);bounded.Position=expected.Position=position;var bytes=new byte[rng.Next(17)];rng.NextBytes(bytes);
                if(position+bytes.Length>128)reject(()=>bounded.Write(bytes),"Budgetguard precedes whole write");else{bounded.Write(bytes);expected.Write(bytes);}
                check(actual.ToArray().SequenceEqual(expected.ToArray()),"Random seek/write effects agree with independent MemoryStream");
            }
            reject(()=>bounded.Seek(long.MaxValue,SeekOrigin.Current),"Overflowingseek refused");reject(()=>bounded.Seek(-1,SeekOrigin.Begin),"Negative seek refused");reject(()=>bounded.SetLength(129),"SetLength budget");reject(()=>bounded.Position=129,"Position budget");bounded.SetLength(0);bounded.Position=128;reject(()=>bounded.WriteByte(1),"Shrinking cannot bypass bytewrite bound");
        }
        using(var inner=new MemoryStream())using(var view=new BoundedStream(inner,3,true,default)){view.WriteAsync(new byte[]{1,2,3}).AsTask().GetAwaiter().GetResult();reject(()=>view.WriteAsync(new byte[]{4}).AsTask().GetAwaiter().GetResult(),"Asyncwrite bounded");check(inner.Length==3,"Overbudgetasyncwrite no partial mutation");}
        using(var inner=new MemoryStream())using(var token=new CancellationTokenSource())using(var view=new BoundedStream(inner,9,true,token.Token)){token.Cancel();reject(()=>view.WriteByte(1),"Cancelledbytewrite");reject(()=>view.ReadByte(),"Cancelledread");reject(()=>view.SetLength(2),"Cancelledlength");check(inner.Length==0,"Cancelledboundedstream no writes");}
        foreach((byte[] Bytes,string Kind) fixture in new[]{(new byte[]{255,251,144,0},"MP3 Layer III"),(new byte[]{255,243,144,0},"MP3 Layer III"),(new byte[]{255,241,80,128},"ADTS AAC"),(new byte[]{255,249,80,128},"ADTS AAC"),("fLaC"u8.ToArray(),"FLAC")})
        {
            using var input=new MemoryStream(fixture.Bytes);check(SystemAudioInput.Identify(input,default)==fixture.Kind&&input.Position==0,"Closed raw selfcontained audio signatures");var id3=new byte[]{73,68,51,4,0,0,0,0,0,3,1,2,3}.Concat(fixture.Bytes).ToArray();using var withTag=new MemoryStream(id3);check(SystemAudioInput.Identify(withTag,default)==fixture.Kind,"Bounded ID3 preceding supported audio");
        }
        foreach(byte[] bytes in new[]{"#EXTM3U\nhttp://x"u8.ToArray(),"http://example"u8.ToArray(),new byte[]{0,0,0,16,102,116,121,112},new byte[]{255,255,255,255},new byte[]{255,233,144,0},new byte[]{255,241,252,128}}){using var input=new MemoryStream(bytes);reject(()=>SystemAudioInput.Identify(input,default),"Playlist/URI/MP4/reservedheader refused before codec");}
        var wave=new byte[12];"RIFF"u8.CopyTo(wave);"WAVE"u8.CopyTo(wave.AsSpan(8));BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4),4);using(var input=new MemoryStream(wave))check(SystemAudioInput.Identify(input,default)=="WAV","Header gate does not claim full WAV parse");wave[4]=5;using(var input=new MemoryStream(wave))reject(()=>SystemAudioInput.Identify(input,default),"RIFF mismatch refused at gate");
        check(PdfRasterPlan.Parse("1,3-5,2,5-3",5).SequenceEqual(new[]{0,2,3,4,1,4,3,2}),"PDF selection repeated descending ranges retain explicit order");
        foreach(string bad in new[]{"","0","6","1,,2","1-2-3","１","١","-1","1,+2",new string('1',513),string.Join(",",Enumerable.Repeat("1",51))})reject(()=>PdfRasterPlan.Parse(bad,5),"Bounded ASCII page selection");
        var letter=PdfRasterPlan.FromDip(816,1056);check(letter==new PdfPageSize(612,792),"Windows PDF DIPs converted to physical points exactly");
        var a4=PdfRasterPlan.FromDip(210/25.4*96,297/25.4*96);check(Math.Abs(a4.Width-595.275590551)<1e-6&&Math.Abs(a4.Height-841.88976378)<1e-6,"A4 physical dimensions preserve inches");
        var rotatedLetter=PdfRasterPlan.Build(new[]{letter},new[]{0},800,90)[0];check(rotatedLetter.PointsWidth==792&&rotatedLetter.PointsHeight==612,"90degree Letter swaps original point dimensions");
        var sizes=new[]{new PdfPageSize(72,144),new PdfPageSize(200,100)};
        foreach(int turn in new[]{0,90,180,270})
        {
            var plan=PdfRasterPlan.Build(sizes,new[]{0,1,0},800,turn);bool swap=turn is 90 or 270;
            check(plan.Length==3&&plan[0].Width==(swap?800:400)&&plan[0].Height==(swap?400:800)&&plan[0].PointsWidth==(swap?144:72),"PDF raster geometry plus physical dimensions and repeats");
            using var output=new MemoryStream();PdfRasterExport.Write(output,plan,i=>new(plan[i].Width,plan[i].Height,new byte[plan[i].Width*plan[i].Height*4]),default);string pdf=Encoding.ASCII.GetString(output.ToArray());check(pdf.StartsWith("%PDF-1.4")&&pdf.Contains("/Count 3")&&pdf.Contains(swap?"/MediaBox [0 0 144 72]":"/MediaBox [0 0 72 144]"),"RasterPDF physical geometry serialized without vector promises");
        }
        var square=Enumerable.Repeat(new PdfPageSize(72,72),7).ToArray();check(PdfRasterPlan.Build(square,Enumerable.Range(0,6).ToArray(),4000,0).Length==6,"96MP PDF selection fits");reject(()=>PdfRasterPlan.Build(square,Enumerable.Range(0,7).ToArray(),4000,0),"112MP selection refused before render");
        foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,0,-1,0.5,14401,double.Epsilon})reject(()=>PdfRasterPlan.Build(new[]{new PdfPageSize(invalid,72)},new[]{0},800,0),"Invalid PDF dimensions refuse before arithmetic/native render");
        var tagged=new PixelImage(3,2,Enumerable.Range(0,24).Select(i=>(byte)i).ToArray());var copy=tagged.Bgra.ToArray();foreach(int turn in new[]{0,90,180,270}){var rotated=PdfRasterPlan.Rotate(tagged,turn);check(PdfRasterPlan.Rotate(rotated,(360-turn)%360).Bgra.SequenceEqual(copy)&&tagged.Bgra.SequenceEqual(copy),"PDF rotations invert with untouched input");}
        var alpha=new PixelImage(3,1,new byte[]{0,20,80,0,10,30,60,128,70,80,90,255});var white=OcrText.White(alpha);check(white.Bgra.AsSpan(0,4).SequenceEqual(new byte[]{255,255,255,255})&&white.Bgra[4]==132&&white.Bgra[7]==255&&white.Bgra[8]==70,"OCR explicit white compositing including hidden RGB");
        check(OcrText.Validate(new string('x',OcrText.MaxUnits)).Length==OcrText.MaxUnits&&OcrText.Validate("😀\r\n文字")=="😀\r\n文字","OCR complete text exactbounds and whitespace retained");foreach(string bad in new[]{"a\0b","\ud800","\udc00",new string('x',OcrText.MaxUnits+1)})reject(()=>OcrText.Validate(bad),"OCR noinvalidUnicode/NUL/truncation");
        var prefs=new Prefs();check(!prefs.IsOn(Feature.TextCapture)&&!Catalog.Available(prefs,new[]{"a.png"}).Any(x=>x.Id=="text.ocr"),"OCR independent defaultoff");prefs.Set(Feature.TextCapture,true);check(Catalog.Available(prefs,new[]{"a.png"}).Any(x=>x.Id=="text.ocr")&&!Catalog.Available(prefs,new[]{"a.png","b.png"}).Any(x=>x.Id=="text.ocr"),"OCR exactly one explicit image");check(Prefs.Parse(prefs.Serialize()).IsOn(Feature.TextCapture),"OCR optionalfeature settings roundtrip");
    }
}

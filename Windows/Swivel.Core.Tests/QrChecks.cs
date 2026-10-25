using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Swivel.Core;

internal static class QrChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        string[] examples = { "Swivel", "https://example.com/a?x=1&y=2", "你好，世界！", "繁體中文と日本語", "Swivel 🦜 😀", "first\r\nsecond\tthird", new('a',16), new('b',17), new('c',31), new('d',32), new('e',52), new('f',53), new('g',77), new('h',78), new('i',105), new('中',35),
            new('j',106), new('k',133), new('l',134), new('m',153), new('n',154), new('o',191), new('p',192), new('q',229), new('r',230), new('s',255), new('t',256), new('u',270), new('中',90), string.Concat(Enumerable.Repeat("🦜",67))+"é",
            string.Concat(Enumerable.Range(0,270).Select(i=>(char)('!'+i%94))), string.Concat(Enumerable.Repeat("line\r\n\t",38))+"done" };
        int[] versions = { 1,2,2,2,1,2,1,2,2,3,3,4,4,5,5,5,6,6,7,7,8,8,9,9,10,10,10,10,10,10,10,10 };
        // Captured before the v6–10 implementation: old payloads retain exactly
        // the same raster, mask choice and quiet zone.
        string[] originalHashes = {
            "C2D23ADBFE13A66D10ECAC1ADE210F0DE2056B26FD420C75EEF973F66815F9CA", "CB602000EC062336959E10FD7475BB9EB048C3A420C5B40E4F712CEF4D59AA58",
            "E94DCF6BC00A04D1D3FCD3ADC6D2FD11BFF1125428CD241867E60D30439F1ED7", "0DDAA745A0D1284F3AD44574AF7500DF1A0DD8DBCEED5FAFD2259BECA8278198",
            "ADAA77B58507776EFB16796013A59958087C6CAF682DC173778164CE27F61A3F", "D1C2CABA5180C9F5003523804D3D351B1C347E0367152995823F89008AFD7CF1",
            "DF76B697A9C4C52EB14D7684CCDC06572F546CBBC7630AEABE035E310CF62053", "C4D2BFB6AB70BDD14FF901435CF9FB2863CD6F43D0B0F7403DF720837C73CFD2",
            "2C4CDCBD4543E226E6770A4EC72F893947D934BCDE929C0F9C82F00DAB23C673", "F1F79EFD64709E008965102A9A2524160DC7FC6515469028F354E97A7B23D157",
            "9CFCA920923F3A3D7ECFE3AA0B3F1A90A1D8FEF6DE9E3BC8D91CA87C9DA3314E", "F4AD46BEE6A47ACB25FD8D7A0842F41D197CE5AA288F8EEDDF4F7C72DE5B7C42",
            "9513B9FA2D76054D6C13B8EED13DA73AC248BE844EFD823327DD2B59283D6C12", "A652BA8896E1D7248305B3EF8399D789DAA1480C374BC76A2AA18F809B49C8EE",
            "D89975A3DCC3ADAC002DE181405354CED901DF31014AAA8A484E206CED35B491", "7AE52E043C04E22A1E23ACA6D87229554E70FF6CEFDD0FD602AF3967FED960A7" };
        string? fixtureDirectory = Environment.GetEnvironmentVariable("SWIVEL_QR_FIXTURES");
        if (fixtureDirectory is not null) Directory.CreateDirectory(fixtureDirectory);
        for (int i = 0; i < examples.Length; i++)
        {
            var code = QrCode.Encode(examples[i]); int side = (code.Size+8)*8; var pixels = code.Raster();
            check(code.Version == versions[i], "QR capacity at UTF-8 version boundaries: " + i);
            check(pixels.Length == side*side && pixels.All(x => x is 0 or 255),"QR raster has only exact black and white pixels");
            check(pixels.Take(side*32).All(x => x == 255) && pixels.TakeLast(side*32).All(x => x == 255) && Enumerable.Range(0,side).All(y => pixels.AsSpan(y*side,32).ToArray().All(x => x == 255) && pixels.AsSpan(y*side+side-32,32).ToArray().All(x => x == 255)), "QR four-module quiet zone on all sides");
            check(code.Mask is >= 0 and <= 7 && code[8,code.Size-8],"QR format and fixed dark module");
            check(QrCode.Encode(examples[i]).Raster().SequenceEqual(pixels),"QR deterministic output");
            if (i < originalHashes.Length) check(Convert.ToHexString(SHA256.HashData(pixels)) == originalHashes[i], "QR old v1–5 output remains byte-identical: " + i);
            Inspect(code, examples[i], check);
            if (fixtureDirectory is not null)
            {
                using var file = File.Create(Path.Combine(fixtureDirectory,$"qr-{i}.pgm"));
                file.Write(Encoding.ASCII.GetBytes($"P5\n{side} {side}\n255\n")); file.Write(pixels);
            }
        }
        if (fixtureDirectory is not null) File.WriteAllText(Path.Combine(fixtureDirectory,"payloads.json"),JsonSerializer.Serialize(examples));
        foreach (string bad in new[] { "", "  \n", new string('x',QrCode.MaxBytes+1), new string('中',985), string.Concat(Enumerable.Repeat("🦜",739)), string.Concat(Enumerable.Repeat("🦜",738))+"中", "a\0b", "a\u001bb", "\ud800", "\udc00" })
            reject(() => QrCode.Encode(bad),"Invalid or oversized QR payload refused");
        int[] maximumPayloads = { 16,31,52,77,105,133,153,191,229,270 };
        var masks = new HashSet<int>();
        for (int length = 1; length <= 270; length++)
        {
            string payload = string.Concat(Enumerable.Range(0,length).Select(i=>(char)('!'+(i*37+length)%94)));
            var code = QrCode.Encode(payload); masks.Add(code.Mask);
            check(code.Version == Array.FindIndex(maximumPayloads,max=>length<=max)+1,"QR chooses the smallest version for every supported byte length");
            Inspect(code,payload,check);
        }
        int[] allMaximums = { 16,31,52,77,105,133,153,191,229,270,320,366,424,457,519,585,643,717,791,857,928,1002,1090,1170,1272,1366,1464,1527,1627,1731,1839,1951,2067,2187,2302,2430,2562,2698,2808,2952 };
        for(int version=11;version<=40;version++)
        {
            foreach(int length in new[]{allMaximums[version-2]+1,allMaximums[version-1]})
            {
                string payload=string.Concat(Enumerable.Range(0,length).Select(i=>(char)('!'+(i*31+version)%94)));
                var code=QrCode.Encode(payload);check(code.Version==version,"QR full-model exact version boundary");Inspect(code,payload,check);
            }
        }
        int[] mediumMax = { 13,25,41,61,83,105,121,151,179,212,250,286,330,361,411,449,503,559,623,665,710,778,856,910,996,1058,1124,1189,1263,1369,1451,1537,1627,1721,1808,1910,1988,2098,2212,2330 };
        for(int version=1;version<=40;version++)
        {
            foreach(int length in new[]{version==1?1:mediumMax[version-2]+1,mediumMax[version-1]})
            {
                string payload=string.Concat(Enumerable.Range(0,length).Select(i=>(char)('!'+(i*29+version)%94)));
                var code=QrCode.Encode(payload,QrCorrection.Medium);check(code.Version==version&&code.Correction==QrCorrection.Medium,"M chooses exact smallest version and preserves correction");Inspect(code,payload,check);
            }
        }
        foreach(string payload in new[]{new string('中',776)+"é",string.Concat(Enumerable.Repeat("😀",582))+"é"})
        { var code=QrCode.Encode(payload,QrCorrection.Medium);check(code.Version==40&&QrCode.Payload(payload,QrCorrection.Medium).Length==2330,"M byte ceiling includes UTF8 ECI and Unicode pairs");Inspect(code,payload,check); }
        reject(()=>QrCode.Encode(new string('x',2331),QrCorrection.Medium),"M over-capacity is refused without truncation");
        check(QrCode.Encode(new string('x',2331)).Correction==QrCorrection.Low,"Old single-argument encoder retains larger L capacity");
        foreach(var invalid in new[]{(QrCorrection)(-1),(QrCorrection)4,(QrCorrection)int.MaxValue})
        { reject(()=>QrCode.Encode("test",invalid),"Undefined correction cannot encode");reject(()=>QrCode.Payload("test",invalid),"Undefined correction cannot validate"); }
        check(masks.Count == 8,"QR structural checks cover all selected masks");
        check(QrCode.Payload(new string('中',984)).Length == QrCode.MaxBytes && QrCode.Payload(string.Concat(Enumerable.Repeat("🦜",738))).Length == QrCode.MaxBytes,"QR limit uses UTF-8 bytes, including surrogate pairs");
        var sample = QrCode.Encode("test"); reject(() => sample.Raster(0),"QR zero scale refused"); reject(() => sample.Raster(33),"QR raster memory budget");
        var prefs = new Prefs(); check(!prefs.IsOn(Feature.QrCodes),"QR defaults off"); prefs.Set(Feature.Wheel,false);
        check(!Catalog.Available(prefs,Array.Empty<string>()).Any(x => x.Id == "qr.create"),"Disabled QR hidden without files"); prefs.Set(Feature.QrCodes,true);
        check(Catalog.Available(prefs,Array.Empty<string>()).Select(x=>x.Id).Order().SequenceEqual(new[]{"qr.create","qr.export","qr.exportMacImages","qr.import","qr.importMac","qr.readflat","qr.recoverflat"}),"Typed QR available without files or Wheel");
        var entry = new QrEntry(Guid.NewGuid().ToString("N"),"Demo","你好 🦜"); prefs.SaveQr(entry); prefs.SaveQr(entry with { Name = "Changed", Text = "new text" });
        var loaded = Prefs.Parse(prefs.Serialize()); check(loaded.QrCodes.Count == 1 && loaded.QrCodes[0].Id == entry.Id && loaded.QrCodes[0].Text == "new text" && loaded.IsOn(Feature.QrCodes),"QR settings round trip and stable entry update");
        reject(() => prefs.SaveQr(new(Guid.NewGuid().ToString("N"),"changed","different")),"QR duplicate names refused");
        reject(() => prefs.SaveQr(entry with { Name = "hidden\u202e" }),"QR name hidden formatting refused");
        prefs.SaveQr(entry with { Text = new('中',90) });
        check(Prefs.Parse(prefs.Serialize()).QrCodes.Single().Text == new string('中',90),"Maximum UTF-8 QR survives settings serialization without truncation");
        reject(() => prefs.SaveQr(entry with { Text = new('x',QrCode.MaxBytes+1) }),"Saved QR payload bounds");
        for (int i = 1; i < 32; i++) prefs.SaveQr(new(Guid.NewGuid().ToString("N"),"Code " + i,"value"));
        reject(() => prefs.SaveQr(new(Guid.NewGuid().ToString("N"),"Overflow","value")),"QR library count budget");
        prefs.RemoveQr(entry.Id); check(prefs.QrCodes.Count == 31 && !prefs.QrCodes.Any(x => x.Id == entry.Id),"QR library delete does not retain payload");
        check(Prefs.Parse("{\"Version\":1,\"Language\":\"en\",\"Enabled\":[\"Wheel\"]}").QrCodes.Count == 0,"Legacy settings have an empty QR library");
    }

    // Read modules back using explicit standard layout tables. This checks the
    // wire representation, including block boundaries and parity syndromes,
    // without accessing any private encoder helper or reconstructing its writer.
    internal static void Inspect(QrCode code, string expected, Action<bool,string> check)
    {
        int size = code.Size, version = code.Version;
        int[][] lengths = { new[]{19},new[]{34},new[]{55},new[]{80},new[]{108},new[]{68,68},new[]{78,78},new[]{97,97},new[]{116,116},new[]{68,68,69,69},new[]{81,81,81,81},new[]{92,92,93,93},new[]{107,107,107,107},new[]{115,115,115,116},new[]{87,87,87,87,87,88},new[]{98,98,98,98,98,99},new[]{107,108,108,108,108,108},new[]{120,120,120,120,120,121},new[]{113,113,113,114,114,114,114},new[]{107,107,107,108,108,108,108,108},new[]{116,116,116,116,117,117,117,117},new[]{111,111,112,112,112,112,112,112,112},new[]{121,121,121,121,122,122,122,122,122},new[]{117,117,117,117,117,117,118,118,118,118},new[]{106,106,106,106,106,106,106,106,107,107,107,107},new[]{114,114,114,114,114,114,114,114,114,114,115,115},new[]{122,122,122,122,122,122,122,122,123,123,123,123},new[]{117,117,117,118,118,118,118,118,118,118,118,118,118},new[]{116,116,116,116,116,116,116,117,117,117,117,117,117,117},new[]{115,115,115,115,115,116,116,116,116,116,116,116,116,116,116},new[]{115,115,115,115,115,115,115,115,115,115,115,115,115,116,116,116},new[]{115,115,115,115,115,115,115,115,115,115,115,115,115,115,115,115,115},new[]{115,115,115,115,115,115,115,115,115,115,115,115,115,115,115,115,115,116},new[]{115,115,115,115,115,115,115,115,115,115,115,115,115,116,116,116,116,116,116},new[]{121,121,121,121,121,121,121,121,121,121,121,121,122,122,122,122,122,122,122},new[]{121,121,121,121,121,121,122,122,122,122,122,122,122,122,122,122,122,122,122,122},new[]{122,122,122,122,122,122,122,122,122,122,122,122,122,122,122,122,122,123,123,123,123},new[]{122,122,122,122,123,123,123,123,123,123,123,123,123,123,123,123,123,123,123,123,123,123},new[]{117,117,117,117,117,117,117,117,117,117,117,117,117,117,117,117,117,117,117,117,118,118,118,118},new[]{118,118,118,118,118,118,118,118,118,118,118,118,118,118,118,118,118,118,118,119,119,119,119,119,119} };
        int[] parityLengths = { 7,10,15,20,26,18,20,24,30,18,20,24,26,30,22,24,28,30,28,28,28,28,30,30,26,28,30,30,30,30,30,30,30,30,30,30,30,30,30,30 };
        int[][] alignments = { Array.Empty<int>(),new[]{6,18},new[]{6,22},new[]{6,26},new[]{6,30},new[]{6,34},new[]{6,22,38},new[]{6,24,42},new[]{6,26,46},new[]{6,28,50},new[]{6,30,54},new[]{6,32,58},new[]{6,34,62},new[]{6,26,46,66},new[]{6,26,48,70},new[]{6,26,50,74},new[]{6,30,54,78},new[]{6,30,56,82},new[]{6,30,58,86},new[]{6,34,62,90},new[]{6,28,50,72,94},new[]{6,26,50,74,98},new[]{6,30,54,78,102},new[]{6,28,54,80,106},new[]{6,32,58,84,110},new[]{6,30,58,86,114},new[]{6,34,62,90,118},new[]{6,26,50,74,98,122},new[]{6,30,54,78,102,126},new[]{6,26,52,78,104,130},new[]{6,30,56,82,108,134},new[]{6,34,60,86,112,138},new[]{6,30,58,86,114,142},new[]{6,34,62,90,118,146},new[]{6,30,54,78,102,126,150},new[]{6,24,50,76,102,128,154},new[]{6,28,54,80,106,132,158},new[]{6,32,58,84,110,136,162},new[]{6,26,54,82,110,138,166},new[]{6,30,58,86,114,142,170} };
        int[] blockLengths = lengths[version-1]; int parity = parityLengths[version-1];
        if(code.Correction==QrCorrection.Medium)
        {
            int[][] mediumLengths = { new[]{16},new[]{28},new[]{44},new[]{32,32},new[]{43,43},new[]{27,27,27,27},new[]{31,31,31,31},new[]{38,38,39,39},new[]{36,36,36,37,37},new[]{43,43,43,43,44},new[]{50,51,51,51,51},new[]{36,36,36,36,36,36,37,37},new[]{37,37,37,37,37,37,37,37,38},new[]{40,40,40,40,41,41,41,41,41},new[]{41,41,41,41,41,42,42,42,42,42},new[]{45,45,45,45,45,45,45,46,46,46},new[]{46,46,46,46,46,46,46,46,46,46,47},new[]{43,43,43,43,43,43,43,43,43,44,44,44,44},new[]{44,44,44,45,45,45,45,45,45,45,45,45,45,45},new[]{41,41,41,42,42,42,42,42,42,42,42,42,42,42,42,42},new[]{42,42,42,42,42,42,42,42,42,42,42,42,42,42,42,42,42},new[]{46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46},new[]{47,47,47,47,48,48,48,48,48,48,48,48,48,48,48,48,48,48},new[]{45,45,45,45,45,45,46,46,46,46,46,46,46,46,46,46,46,46,46,46},new[]{47,47,47,47,47,47,47,47,48,48,48,48,48,48,48,48,48,48,48,48,48},new[]{46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,47,47,47,47},new[]{45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,46,46,46},new[]{45,45,45,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46},new[]{45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,45,46,46,46,46,46,46,46},new[]{47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,48,48,48,48,48,48,48,48,48,48},new[]{46,46,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47},new[]{46,46,46,46,46,46,46,46,46,46,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47},new[]{46,46,46,46,46,46,46,46,46,46,46,46,46,46,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47},new[]{46,46,46,46,46,46,46,46,46,46,46,46,46,46,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47},new[]{47,47,47,47,47,47,47,47,47,47,47,47,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48},new[]{47,47,47,47,47,47,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48},new[]{46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,46,47,47,47,47,47,47,47,47,47,47,47,47,47,47},new[]{46,46,46,46,46,46,46,46,46,46,46,46,46,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47},new[]{47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,48,48,48,48,48,48,48},new[]{47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,47,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48,48} };
            int[] mediumParity = { 10,16,26,18,24,16,18,22,22,26,30,22,22,24,24,28,28,26,26,26,26,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28 };
            blockLengths=mediumLengths[version-1];parity=mediumParity[version-1];
        }
        if(code.Correction is QrCorrection.Quartile or QrCorrection.High)
        {var facts=QrAllLevelsChecks.Facts(code.Correction,version);blockLengths=facts.Blocks;parity=facts.Ecc;}
        var occupied = new bool[size,size];
        void Reserve(int x,int y,int width,int height) { for(int dy=0;dy<height;dy++)for(int dx=0;dx<width;dx++) occupied[x+dx,y+dy]=true; }
        Reserve(0,0,9,9); Reserve(size-8,0,8,9); Reserve(0,size-8,9,8);
        Reserve(6,9,1,size-17); Reserve(9,6,size-17,1);
        foreach(int y in alignments[version-1]) foreach(int x in alignments[version-1])
        {
            if ((x==6&&y==6)||(x==6&&y==size-7)||(x==size-7&&y==6)) continue;
            Reserve(x-2,y-2,5,5);
            bool matches=true;
            for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=2;dx++)
                matches &= code[x+dx,y+dy] == (Math.Abs(dx)==2 || Math.Abs(dy)==2 || dx==0&&dy==0);
            check(matches,"QR alignment centers including timing intersections");
        }
        if(version>=7)
        {
            Reserve(size-11,0,3,6); Reserve(0,size-11,6,3);
            int first=0,second=0;
            for(int bit=0;bit<18;bit++) { if(code[size-11+bit%3,bit/3])first|=1<<bit; if(code[bit/3,size-11+bit%3])second|=1<<bit; }
            int[] expectedVersion = { 0x7c94,0x85bc,0x9a99,0xa4d3,0xbbf6,0xc762,0xd847,0xe60d,0xf928,0x10b78,0x1145d,0x12a17,0x13532,0x149a6,0x15683,0x168c9,0x177ec,0x18ec4,0x191e1,0x1afab,0x1b08e,0x1cc1a,0x1d33f,0x1ed75,0x1f250,0x209d5,0x216f0,0x228ba,0x2379f,0x24b0b,0x2542e,0x26a64,0x27541,0x28c69 };
            check(first==expectedVersion[version-7]&&second==first,"QR both version BCH copies match the standard");
        }
        int[] formats = code.Correction==QrCorrection.Low?new[]{ 0x77c4,0x72f3,0x7daa,0x789d,0x662f,0x6318,0x6c41,0x6976 }:new[]{0x5412,0x5125,0x5e7c,0x5b4b,0x45f9,0x40ce,0x4f97,0x4aa0};
        if(code.Correction is QrCorrection.Quartile or QrCorrection.High)formats=QrAllLevelsChecks.Formats(code.Correction);
        var formatA = new List<bool>(); var formatB = new List<bool>();
        for(int y=0;y<=5;y++)formatA.Add(code[8,y]); formatA.Add(code[8,7]); formatA.Add(code[8,8]);formatA.Add(code[7,8]);for(int x=5;x>=0;x--)formatA.Add(code[x,8]);
        for(int x=size-1;x>=size-8;x--)formatB.Add(code[x,8]); for(int y=size-7;y<size;y++)formatB.Add(code[8,y]);
        int ReadLittle(List<bool> values) { int result=0; for(int i=0;i<values.Count;i++)if(values[i])result|=1<<i;return result; }
        check(ReadLittle(formatA)==formats[code.Mask]&&ReadLittle(formatB)==formats[code.Mask],"QR both format BCH copies match the chosen correction level and mask");
        bool Unmask(int x,int y)
        {
            int sum=x+y,product=x*y;
            bool invert=code.Mask switch { 0=>sum%2==0,1=>y%2==0,2=>x%3==0,3=>sum%3==0,4=>(y/2+x/3)%2==0,5=>product%2+product%3==0,6=>(product%2+product%3)%2==0,_=>(sum%2+product%3)%2==0 };
            return code[x,y]!=invert;
        }
        var stream = new List<bool>(); int band=0;
        for(int x=size-1;x>0;x-=2)
        {
            if(x==6)x--;
            for(int row=0;row<size;row++)
            {
                int y=(band%2==0)?size-1-row:row;
                if(!occupied[x,y])stream.Add(Unmask(x,y));
                if(!occupied[x-1,y])stream.Add(Unmask(x-1,y));
            }
            band++;
        }
        int total=blockLengths.Sum()+parity*blockLengths.Length;
        check(stream.Count==total*8+(version switch { >=2 and <=6=>7,>=14 and <=20=>3,>=21 and <=27=>4,>=28 and <=34=>3,_=>0 })&&stream.Skip(total*8).All(b=>!b),"QR data region and zero remainder bit count");
        byte[] words=new byte[total];
        for(int i=0;i<total*8;i++)if(stream[i])words[i/8]|=(byte)(128>>(i%8));
        byte[][] blocks=blockLengths.Select(n=>new byte[n+parity]).ToArray(); int cursor=0;
        for(int column=0;column<blockLengths.Max();column++)for(int block=0;block<blocks.Length;block++)
            if(column<blockLengths[block])blocks[block][column]=words[cursor++];
        for(int column=0;column<parity;column++)for(int block=0;block<blocks.Length;block++)blocks[block][blockLengths[block]+column]=words[cursor++];
        // Independent log/antilog field arithmetic verifies all RS roots, rather
        // than calling the encoder's polynomial long-division implementation.
        int[] powers=new int[510],logs=new int[256];int element=1;
        for(int i=0;i<255;i++){powers[i]=powers[i+255]=element;logs[element]=i;element<<=1;if(element>=256)element^=0x11d;}
        foreach(byte[] block in blocks)
        {
            bool valid=true;
            for(int root=0;root<parity;root++)
            {
                int syndrome=0;
                foreach(byte b in block)syndrome=(syndrome==0?0:powers[logs[syndrome]+root])^b;
                valid &= syndrome==0;
            }
            check(valid,"QR every Reed–Solomon block has zero syndromes at all required roots");
        }
        byte[] data=blocks.SelectMany((block,i)=>block.Take(blockLengths[i])).ToArray();int position=0;
        int Read(int count){int value=0;for(int i=0;i<count;i++,position++)value=(value<<1)|((data[position/8]>>(7-position%8))&1);return value;}
        check(Read(4)==7&&Read(8)==26&&Read(4)==4,"QR wire payload declares UTF-8 ECI 26 and byte mode");
        int length=Read(version<10?8:16);byte[] expectedBytes=Encoding.UTF8.GetBytes(expected);
        check(length==expectedBytes.Length,"QR length field survives v10 16-bit transition");
        byte[] decoded=new byte[length];for(int i=0;i<length;i++)decoded[i]=(byte)Read(8);
        check(decoded.SequenceEqual(expectedBytes),"QR deinterleaved content matches every original UTF-8 byte");
        int terminator=Math.Min(4,data.Length*8-position);check(Read(terminator)==0,"QR terminator or exact-fill ending");
        int alignment=(8-position%8)%8;check(Read(alignment)==0,"QR data byte alignment is zero");
        int pad=0;bool padding=true;while(position<data.Length*8)padding&=Read(8)==(pad++%2==0?0xec:0x11);
        check(padding,"QR padding follows the byte-aligned terminator");
    }
}

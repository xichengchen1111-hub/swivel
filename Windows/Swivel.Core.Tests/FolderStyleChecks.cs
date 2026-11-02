using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using Swivel.Core;

internal static class FolderStyleChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check(Enum.GetValues<FolderSurface>().Length == 29, "Every Mac material has a Windows surface, retaining legacy extras");
        string icon = FolderStyles.NewIconName();
        check(FolderStyles.IsIconName(icon) && !FolderStyles.IsIconName(".swivel-folder-ABCDEF12.ico") && !FolderStyles.IsIconName("x.ico") && !FolderStyles.IsIconName(icon + "\\..\\x"), "Swivel icon names are generated and recognised strictly");
        foreach (bool flag in new[] { false, true })
        {
            var ini = new SwivelFolderIni(icon, flag);
            byte[] bytes = FolderStyles.IniBytes(ini);
            check(bytes[0] == 0xFF && bytes[1] == 0xFE && FolderStyles.ParseOurs(bytes) == ini && FolderStyles.Classify(bytes) == FolderStyleState.Swivel, "Swivel desktop.ini round trip: ReadOnlyAdded " + flag);
            string text = FolderStyles.IniText(ini);
            check(text.Contains("[.ShellClassInfo]\r\nIconResource=" + icon + ",0\r\n", StringComparison.Ordinal), "desktop.ini names the icon for Explorer");
            // Any change at all makes it someone else's file.
            foreach (string altered in new[] { text + "\r\n", text.Replace("Version=1", "Version=2"), text.Replace("[.ShellClassInfo]", "[.ShellClassInfo]\r\nInfoTip=x"), text.ToUpperInvariant(), " " + text })
                check(FolderStyles.ParseOurs(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(altered)).ToArray()) is null, "Altered desktop.ini is not treated as Swivel's");
        }
        byte[] valid = FolderStyles.IniBytes(new SwivelFolderIni(icon, false));
        check(FolderStyles.ParseOurs(valid.Skip(2).ToArray()) is null, "desktop.ini without a byte-order mark is not Swivel's");
        check(FolderStyles.ParseOurs(Encoding.UTF8.GetBytes(FolderStyles.IniText(new SwivelFolderIni(icon, false)))) is null, "UTF-8 desktop.ini is not Swivel's");
        check(FolderStyles.ParseOurs(valid.Take(valid.Length - 1).ToArray()) is null, "Odd-length desktop.ini is not Swivel's");
        var lone = (byte[])valid.Clone(); lone[^2] = 0x00; lone[^1] = 0xD8; // unpaired surrogate
        check(FolderStyles.ParseOurs(lone) is null, "desktop.ini with invalid UTF-16 is not Swivel's");
        string other = "[.ShellClassInfo]\r\nIconResource=" + icon + ",0\r\n[Swivel]\r\nVersion=1\r\nIcon=" + icon + "\r\nReadOnlyAdded=1\r\n";
        check(FolderStyles.Classify(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(other)).ToArray()) == FolderStyleState.Other, "A look-alike without the exact header is someone else's");
        check(FolderStyles.Classify(null) == FolderStyleState.None && FolderStyles.Classify(new byte[FolderStyles.MaxIniBytes + 1]) == FolderStyleState.Other, "No desktop.ini, and an oversized one, classified");
        reject(() => FolderStyles.IniText(new SwivelFolderIni("..\\evil.ico", false)), "desktop.ini never names a foreign icon");

        foreach (string badge in new[] { "", "A", "7z", "旅", "工作", "🦜", "👩‍💻", "❤️" }) FolderStyles.ValidateBadge(badge);
        check(true, "Short badges, CJK and emoji sequences accepted");
        foreach (string badge in new[] { "ABC", " A", "A ", "​", "a‮b", "\t", "", "\uD800", new string('x', 17) })
            reject(() => FolderStyles.ValidateBadge(badge), "Badge refused: " + string.Join(" ", badge.Select(c => ((int)c).ToString("X4"))));
        reject(() => new FolderStyle(FolderStyles.Colors.Count).Validate(), "Unknown colour refused");
        reject(() => new FolderStyle(-1).Validate(), "Negative colour refused");
        new FolderStyle(0, "A").Validate();

        foreach (string emoji in new[] { "😀", "👩‍💻", "👨‍👩‍👧‍👦", "🇨🇳", "👍🏽", "👩🏽‍🚀", "❤️", "1️⃣", "🏳️‍🌈" })
        {
            FolderStyles.ValidateEmoji(emoji);
            new FolderStyle(0, Emoji: emoji, Surface: FolderSurface.Linen).Validate();
            check(true, "Single emoji accepted: " + emoji);
        }
        foreach (string emoji in new[] { "", "hello", "中", "A", "1", "#", "😀😀", "🇨", "🏻", "😀\n", "\u200D", "A‍😀", "😀‍A", "😀\u200B", "😀\uFE0E", "\uD800", "😀🏻", "👍🏽🏽", "😀‍", "😀\u0301", "\U0001F6F1", "\U0001F6F2", "\U0001FA7F", "😀️️", "\t", "\n", "\0", "\u202E" })
            reject(() => FolderStyles.ValidateEmoji(emoji), "Invalid emoji refused: " + emoji);
        reject(() => new FolderStyle(0, "A", "😀").Validate(), "Text and emoji cannot be combined");
        reject(() => new FolderStyle(0, Surface: (FolderSurface)99).Validate(), "Unknown surface refused");
        foreach (var (surface, expected) in new (FolderSurface, string)[] {
            (FolderSurface.Plain, "5F70BF18A086007016E948B04AED3B82103A36BEA41755B6CDDFAF10ACE3C6EF,E61F41D57DB208C5F92A35C4CE7198570924A3FC87EEBA83441FCEEE5D6A2865,CFC335996CFAE29FDF14FD2DB7EA853366D350A5D387AA3C322616E81DACD56B,AD7FACB2586FC6E966C004D7D1D16B024F5805FF7CB47C7A85DABD8B48892CA7,56A43EF88DDFCD0F56F7DD973312C0E73D62F59655C01D7B4E59AAA3BE8B3FB6,2D07A41AE992770085117E9815300BFD0730745883E60B24AAAD5E69DFC087AE,4FE7B59AF6DE3B665B67788CC2F99892AB827EFAE3A467342B3BB4E3BC8E5BFE,8A39D2ABD3999AB73C34DB2476849CDDF303CE389B35826850F9A700589B4A90"),
            (FolderSurface.Linen, "1CF95563D80227F7C6A2CD77C906369EF3D0A0EF36B609EFF66B43C91C54FEAF,2B370B62EAB0EA60ECCCD2064A80FB8CCDB29C4859FBB5E96D8A93B8BD481F97,BC53EF7C96EDAF5301B02A7636B85084F8F12E1879D8758F70F280DD664CB4F9,28D34FA2478772B5123E272000BEFA18A3A51A653461D3374C0C260527835671,DABBD7D2C1F7D1DFF695F27D8AA14671D78FDA5499AAEE30F30D015C4A3F9BFD,9C91B9CE032F881A82A7C0F152973B68F3C3AAAEA2E74127B4F01100AA8CAF79,CC60AFF95CC55B3DC0C4D23EC1D45F45CBB3EE45309BFD3A4A29B77E18C17514,319B62025FCF74A1B6F6B16FF711C9C4459CF803FF4D8FE6B8BD19F05980F472"),
            (FolderSurface.PaperGrain, "E72B1CBA054E3F62C4F272CAA4E65B8A1DBE6EDD0700B3A5266A8FD0AB3A9572,055470F7742CD1DF0867EB32E926AB692BEB8EEF4874CAA402917901F4B5DF33,17DA7BE4D114E574EDCD4E287226E5DCE0AAE139E6FC4AB6AC6E5C965EFD69BF,8FFA0CE7CAE2A87A1C6777CE52D9F412AAA4402933B55753E0D05E09932B2093,57F8094246C79676117DCA0A9B188F73BC7ED9CC1E26800E19264FBB035783F8,094FB0B3C244C5E24C75ADD4C49FC309328A19D36D07C6CD043BCF2DDFB770E7,243333259BCE8DDCA7D93BFB66C6F1FF4E97B45EB759265455C0BACDF9078FAB,F2877D81C35B9914162135FBF66EF862CB0B0DE9650F369A2E94E2461BFC380A"),
            (FolderSurface.WoodStripes, "13FBD30F68915FCBFBC7E068F3417743070F58FDB7E3B84B225F4ADCB2E4633E,75123AEAED61E25E3755DBA62E9B515E6F614C7E088D52B2CDC5627641FC9496,31202975ED3BB14D0A83D2918D7815EC01D7BDAE4BA80DC67AA9D0024138E75E,4D256F7E03C9A9895A44DDEFD2B428D4B550B6CEADE24323C0DC59CFF4FFBE31,239F0D8206C36FFEB8184DB254C38279B15811F70E4214D5651BC0650E7E1247,A275E84204B70720D2C11F9F8D4668F8BF45464249B00D566A3329D0E855CA97,47CF1140E3091C198722867954EF8E2299B58CA0692F2986AA4F28F6AB7AB237,297593D3BB270139829D52ACF5F72627157F7924ED64B0883D52EA5799150CC3"),
            (FolderSurface.Dots, "0DE7F0AEEBC27C1B766057B9C65659A071FA17C17D067BA8166EA563B8CC23E4,839A685E95631DC12A01F1FA4C07F7CD5E5762106B28C4B8ABC2785961F64C33,31BCE816D365D2A300EAFF34B0A1AA7E911E2EAD00AF48366D15D4D9F141035C,F1EAE6F423E630E5F8C8A33721CF8115FB58FAC7C0232C460A5265E3CF116FE1,D1172E2B9103BAAB09CF50D56D3257B649C3EAEFCC91495BED61FCDFC5CA4299,2C0A37E500C94E25D2C965B5E585699ECC398BC008610B806B24D384EAFA990E,CE000564AF888940FE7A0BA8030F3AF6254166F4C3B961BD979CBF1CCDFF1B45,B056F426AB8EB1BA570F3814827994110CF95B52E106E6A09C1903B7C96D6CF1"),
            (FolderSurface.Grid, "2FB81F947BBBE3C732364BA3C3A658D0A756D8CA91B5956ABFF5BB0E393BA0C9,9B2AAAAE026E622004541E836A236F8E2ED4A458E82EA723B8C20435E0B4C56D,5E160A0A21CEF0CC67A3DD5143CC41A9D443F0F9AEE1912B74C9FC1584C84C5A,10F1634BF21919418C004AE9B57D4BF76FF997F4FF166D03753CA47BA3E4D774,31AF31EEBA9072F4EB26A7112F93BBE668683115DAD00D30275A5BDD6D46E430,D90DCF5406BA54B6EE6A5535E542E69D9F842FB0175849450B6AB550402A498D,F9D9F691F3121F2EC9CBABD60DBBCA18D0E8E140B58831FE509571A8F9FE8AA1,4A951F84A8EEEE919F1E1241480CA63B88BD189C99B2BA2EF31DD1C60676EBFF"),
            (FolderSurface.Gradient, "D2DE4A64007ED07D5D9E2FA01063EF6533DA89A29470735B34D1CF4CFC46CDEE,3C0866ACA18E6324D15CE4D3F32691C358C51917067B675EAD5B497BBDC6CD94,E9052B4CAFC2DDF7A57EAF35EDBFB987ECC2BC7F2A01917274FE0B4C90753F44,E0D38870058BCF68C427F8823C2362D9D4E9A47782E00BAD41B13F348DA824EB,C9EFE070D0C2BF80F6BA5684546DCB388521CE2E21532DB99940556558CB9D95,C17331962DF08B9A9AADB7A083DD114880826A11CF5B2F48914CFE84730AF620,484A37E7F469CBC057A27F05606FE606A3709C15F93D90D7E077ABB7A24E3A69,F3C55C0144FBF3EC20FB60630DD5B448220A1C099E272C4A13E1EEA99B0BD74B"),
            (FolderSurface.BrushedMetal, "0352DC2F011768E3887317520A48E72A8B806F55E7DACFE68E4158BA37A46037,031CF4D0F003A9D04A567ECB953CD8AA905E8C41CE4D36C497154348D181D43A,CFB6467D90289154BA5CEF126251F5DD5A0205D0AC7CD3573A0AC62CE3374D50,73ECF3108A62E2E5E6C9CD3F49C05DE0957776E24F67AA5FA3390FE8D246A45A,7ED777FD9B82F9421AB6602FD851DB367CB5066DE7B69A3059A073AC18396765,434A1C9FB1B598F452FDE885D88D643DE630C2D5CB1881BE0AC8FC0EF1C0B568,C83A20D908F962A9A93B386F79B08C9C56EBF43D41052E276523BDC3456E33C8,75FF1FCAF8717C48ABDA3961DF759461963581AFA307DFD1F47EC80082310B6A"),
            (FolderSurface.Stars, "E67D351264C1A1ACA4C2E3953213C93A61D1C0E29B218E9469DC6E760751A3AD,A9FF4ADE30636C1E6DEA336C16A1FEBCE108DE679846DB4BB60D36E09528B27B,2F23CC78D86C755683BA1620B9F4569A3DF01B7A7EE2E5E561C589C0E77786B7,867C2B20E6809CF5098E6EE27250F9D3E4AD64A6E6BAADA059772A37DC8DF221,A00F0FA58570F92340D4377F576908058756DD7CEF563FE9DDCE78F6A8736F44,0F3F308A3A30599ECA71752B70D3F4D2E9C9DA54FAA4CA47A01A474084528A74,E4EEE8733F910D71F879D15C642DC38892FC09656AE2C8E9EA6E4DCDDD30F3B0,10C6B489EC06D3D39572506FEE28B12DDE9D40AD8F7B5F17BA6289281DC46068"),
        })
            check(string.Join(",", FolderStyles.IconSizes.Select(n => Convert.ToHexString(SHA256.HashData(FolderSurfaces.Pixels(surface, n, 123))))) == expected,
                "All legacy surface bytes unchanged: " + surface);
        check(FolderSurfaces.Names.Count == Enum.GetValues<FolderSurface>().Length && FolderSurfaces.Names.Distinct().Count() == FolderSurfaces.Names.Count, "Every surface has one distinct picker name");
        foreach (string name in FolderSurfaces.Names)
            check(Strings.Table.TryGetValue(name, out var translations) && translations.Length == 3 && translations.All(t => !string.IsNullOrWhiteSpace(t)), "Surface name translated: " + name);
        foreach (int size in FolderStyles.IconSizes)
        {
            var hashes = new System.Collections.Generic.HashSet<string>();
            foreach (FolderSurface surface in Enum.GetValues<FolderSurface>())
            {
                byte[] pixels = FolderSurfaces.Pixels(surface, size, 123);
                string hash = Convert.ToHexString(SHA256.HashData(pixels));
                check(pixels.Length == size * size * 4 && hash == Convert.ToHexString(SHA256.HashData(FolderSurfaces.Pixels(surface, size, 123))), "Surface hash repeatable: " + surface + " / " + size);
                check(hashes.Add(hash), "Surface pixels differ: " + surface + " / " + size);
                if ((int)surface <= 8)
                    check(pixels.Where((_, i) => i % 4 == 3).All(a => a <= 40), "Legacy surface stays subtle: " + surface);
                else
                    check(pixels.Where((_, i) => i % 4 == 3).All(a => a == 255), "New material is opaque: " + surface);
                check(Enumerable.Range(0, size * size).All(i => pixels[i * 4] <= pixels[i * 4 + 3] && pixels[i * 4 + 1] <= pixels[i * 4 + 3] && pixels[i * 4 + 2] <= pixels[i * 4 + 3]), "Valid premultiplied surface pixels: " + surface);
            }
            check(FolderSurfaces.Pixels(FolderSurface.Plain, size, 123).All(b => b == 0), "Plain surface has no overlay");
            check(!FolderSurfaces.Pixels(FolderSurface.PaperGrain, size, 123).SequenceEqual(FolderSurfaces.Pixels(FolderSurface.PaperGrain, size, 124)), "Seed changes grain");
        }
        // Inspect only the visible face: variation elsewhere cannot rescue a missing material.
        foreach (int size in FolderStyles.IconSizes.Append(96))
        {
            foreach (FolderSurface surface in Enum.GetValues<FolderSurface>().Where(FolderSurfaces.IsMaterial))
            {
                byte[] pixels = FolderSurfaces.Pixels(surface, size);
                var colors = new System.Collections.Generic.HashSet<uint>();
                for (int y = (int)Math.Ceiling(size * 0.38); y < size * 0.82; y++)
                for (int x = (int)Math.Ceiling(size * 0.08); x < size * 0.92; x++)
                {
                    int i = (y * size + x) * 4;
                    colors.Add((uint)(pixels[i + 2] << 16 | pixels[i + 1] << 8 | pixels[i]));
                }
                check(colors.Count > 1, "Material face is not flat: " + surface + "/" + size);
                uint[] features = surface switch
                {
                    FolderSurface.Notebook => new uint[] { 0x92B9CE, 0xCC8F8F }, // ruling and margin
                    FolderSurface.Leather => new uint[] { 0xE1BB89 }, // stitching
                    FolderSurface.NeonGrid => new uint[] { 0x4DE2E0, 0xF466D3 }, // grid and diagonal
                    FolderSurface.Blueprint => new uint[] { 0x709BC8, 0xE1EFF5 }, // grid and drawing
                    FolderSurface.Banknote => new uint[] { 0x486E51 }, // frame
                    FolderSurface.FilmStrip => new uint[] { 0xE6DFCA }, // sprocket holes
                    FolderSurface.Galaxy => new uint[] { 0xF7F3D8 }, // stars
                    FolderSurface.EmojiPattern => new uint[] { 0xFFDB70, 0x4C433B }, // faces and expression
                    _ => Array.Empty<uint>(),
                };
                foreach (uint feature in features)
                    check(colors.Contains(feature), "Material defining feature survives: " + surface + "/" + size + "/" + feature.ToString("X6"));
            }
        }
        reject(() => FolderSurfaces.Pixels(FolderSurface.Linen, 0, 1), "Zero surface size refused");
        reject(() => FolderSurfaces.Pixels(FolderSurface.Linen, 257, 1), "Oversize surface refused");

        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        byte[] atLimit = new byte[FolderStyles.MaxIconBytes - 22];
        png.CopyTo(atLimit, 0);
        check(FolderStyles.Icon(new[] { (16, atLimit) }).Length == FolderStyles.MaxIconBytes, "ICO exact byte budget accepted");
        reject(() => FolderStyles.Icon(new[] { (16, atLimit), (32, png) }), "ICO over byte budget refused before publish");
        byte[] ico = FolderStyles.Icon(new[] { (16, png), (256, png) });
        check(Convert.ToHexString(SHA256.HashData(ico)) == "F96DF06C02668680AFDEC2A9AF417C3D5034430AF942C2C5C882404816862635", "Existing ICO bytes are unchanged");
        check(BitConverter.ToUInt16(ico, 0) == 0 && BitConverter.ToUInt16(ico, 2) == 1 && BitConverter.ToUInt16(ico, 4) == 2, "ICO header: reserved, type 1, two images");
        check(ico[6] == 16 && ico[22] == 0 && BitConverter.ToUInt16(ico, 12) == 32 && BitConverter.ToUInt32(ico, 14) == png.Length && BitConverter.ToUInt32(ico, 18) == 38 && BitConverter.ToUInt32(ico, 34) == 38 + png.Length, "ICO entries: sizes (256 stored as 0), 32 bpp, lengths and offsets");
        check(ico.Length == 38 + 2 * png.Length && ico.Skip(38).Take(png.Length).SequenceEqual(png), "ICO image data follows the directory");
        reject(() => FolderStyles.Icon(new[] { (16, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) }), "ICO accepts PNG images only");
        reject(() => FolderStyles.Icon(new[] { (257, png) }), "ICO refuses sizes above 256");
    }
}

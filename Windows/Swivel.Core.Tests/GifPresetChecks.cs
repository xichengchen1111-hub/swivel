using System;
using System.Linq;
using System.Text;
using Swivel.Core;

internal static class GifPresetChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        check(ImageRecipe.Formats.SequenceEqual(new[]{"png","jpg","bmp","tiff","gif"}),"GIF preset appends format without reindexing existingfour");
        string id="0123456789abcdef0123456789abcdef";var recipe=new ImageRecipe("gif",512,17,new PhotoSettings(Exposure:0.25),true);var preset=new ImagePreset(id,"GIF colors",recipe);
        preset.Validate();var prefs=new Prefs();prefs.SavePreset(preset);
        check(!prefs.IsOn(Feature.ImagePresets)&&!Catalog.Available(prefs,new[]{"image.png"}).Any(x=>x.Id=="preset."+id),"GIF presets remain independentdefaultoff");
        prefs.Set(Feature.ImagePresets,true);prefs.Set(Feature.MyWheel,true);prefs.SetCustomActions(new[]{"preset."+id});string settings=prefs.Serialize();var restored=Prefs.Parse(settings);
        check(restored.Presets.Single()==preset&&restored.CustomActionIds.Single()=="preset."+id&&restored.IsOn(Feature.ImagePresets),"GIF settings preserve allrecipevalues/IDs/switch/menu");
        restored.SavePreset(preset with{Name="GIF renamed"});check(restored.Presets.Single().Id==id&&restored.CustomActionIds.Single()=="preset."+id,"GIF rename retainsstableactionID");
        check(ImageOutputInfo.Format("preset."+id,recipe)=="gif"&&!PresetLibrary.Describe(recipe).Contains('%'),"GIF preset outputsharedformat and nofakeJPEGqualitypercent");
        byte[] backup=PresetLibrary.Encode(new[]{preset});var imported=PresetLibrary.Decode(backup);check(imported.Compatible.Single().Recipe==recipe&&imported.Compatible.Single().Id!=id&&!imported.FromMac,"GIF JSONbackupallfields and newlocalID");
        var before=restored.Serialize();int changes=0;restored.Changed+=()=>changes++;
        reject(()=>restored.ImportPresets(new[]{preset with{Id=Guid.NewGuid().ToString("N"),Name="GIF renamed",Recipe=recipe with{Quality=18}}}),"GIFqualityvalue conflict retained evenifJPEGonlyoutputsetting");
        check(restored.Serialize()==before&&changes==0,"GIF conflicting import no partialmutation/notification");
        restored.ImportPresets(new[]{preset with{Id=Guid.NewGuid().ToString("N"),Name="GIF renamed"}});check(restored.Serialize()==before&&changes==0,"GIFsamepresetimport skipswithoutmutatingexistingIDs");
        foreach(int quality in new[]{1,100})new ImageRecipe("gif",Quality:quality).Validate();foreach(int quality in new[]{0,101})reject(()=>new ImageRecipe("gif",Quality:quality).Validate(),"GIFstoredquality remainsbounded");
        foreach(string format in new[]{"GIF","image/gif","gif/../png","webp","heic"})reject(()=>new ImageRecipe(format).Validate(),"GIFnewclosedformat doesnotopenotherencodings");
        byte[] exact=new byte[PresetLibrary.MaxBytes];backup.CopyTo(exact,0);exact.AsSpan(backup.Length).Fill(32);check(PresetLibrary.Decode(exact).Compatible.Single().Recipe==recipe,"GIFpresetbackup exact64KiB whitespace envelope");reject(()=>PresetLibrary.Decode(exact.Append((byte)32).ToArray()),"GIFbackup64KiB+1 refused");
        string exactSettings=settings+new string(' ',Prefs.MaxJsonLength-settings.Length);check(Prefs.Parse(exactSettings).Presets.Single()==preset,"GIFprefs exact1MiUTF16 envelope");reject(()=>Prefs.Parse(exactSettings+" "),"GIFprefs1MiUTF16+1 refused");
        byte[] fakeMac=Encoding.UTF8.GetBytes("[{\"id\":\"mac-gif\",\"name\":\"GIF\",\"longEdge\":0,\"format\":\"gif\",\"quality\":85,\"stripMetadata\":true}]");reject(()=>PresetLibrary.Decode(fakeMac),"GIFnot falsely acceptedasMacpresetformat");
        var legacy=Prefs.Parse("{\"Version\":1,\"Language\":\"en\",\"Enabled\":[\"Wheel\"]}");check(!legacy.IsOn(Feature.ImagePresets)&&legacy.Presets.Count==0,"GIFaddition preservesv1migrationanddefaultoff");
        var full=new Prefs();for(int i=0;i<24;i++)full.SavePreset(new(Guid.NewGuid().ToString("N"),"GIF "+i,new("gif")));reject(()=>full.SavePreset(new(Guid.NewGuid().ToString("N"),"Extra",new("gif"))),"GIFpresets keep24maximum");
    }
}

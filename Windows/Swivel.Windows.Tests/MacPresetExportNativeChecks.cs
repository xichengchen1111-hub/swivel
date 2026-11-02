using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class MacPresetExportNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string folder = Path.Combine(directory, "Mac preset exports"); Directory.CreateDirectory(folder);
        var preset = new ImagePreset("11111111111111111111111111111111", "Windows", new("jpg", 1600, 80));
        var plan = MacPresetExport.Prepare(new[] { preset });
        string occupied = Path.Combine(folder, "swivel-mac-presets.json"); byte[] sentinel = { 4, 3, 2, 1 }; File.WriteAllBytes(occupied, sentinel);
        string output = MacPresetExportFile.Save(plan, folder, default);
        check(output == Path.Combine(folder, "swivel-mac-presets 2.json") && File.ReadAllBytes(occupied).SequenceEqual(sentinel), "Mac preset export exclusively publishes without replacing occupied name");
        check(File.ReadAllBytes(output).SequenceEqual(plan.Encode()) && PresetLibrary.Decode(File.ReadAllBytes(output)).Compatible.Single().Recipe == preset.Recipe, "Mac preset native output equals reviewed bytes and existing importer roundtrips");
        int before = Directory.GetFiles(folder).Length;
        reject(() => MacPresetExportFile.Save(plan, folder, new CancellationToken(true)), "Mac preset pre-cancel refuses publication");
        check(Directory.GetFiles(folder).Length == before, "Mac preset cancellation creates no file");
        reject(() => MacPresetExportFile.Save(MacPresetExport.Prepare(Array.Empty<ImagePreset>()), folder, default), "Mac preset empty plan cannot publish destructive empty replacement");
        check(Directory.GetFiles(folder).Length == before && !Directory.GetFiles(folder, ".swivel-*.tmp").Any(), "Mac preset failure/cancel leaves no private stages");
        string source = Path.Combine(folder, "Mac current.json");
        byte[] mac = Encoding.UTF8.GetBytes("[{\"id\":\"mac\",\"name\":\"Mac\",\"nameKey\":\"Localized\",\"longEdge\":0,\"format\":\"heic\",\"quality\":92,\"stripMetadata\":false}]");
        File.WriteAllBytes(source, mac);
        using (var held = MacPresetExportFile.Open(new[] { preset }, source, default))
        {
            reject(() => Directory.Move(folder, folder + " moved"), "Mac preset merge holds input ancestors throughout preview/export");
            reject(() => File.WriteAllBytes(source, new byte[] { 0 }), "Mac preset merge holds source against writes");
            string merged = MacPresetExportFile.Save(held.Plan, folder, default);
            check(held.Plan.Count == 2 && Encoding.UTF8.GetString(File.ReadAllBytes(merged)).StartsWith("[" + Encoding.UTF8.GetString(mac)[1..^1] + ",", StringComparison.Ordinal), "Mac preset native merged output preserves Mac raw row first");
        }
        reject(() => MacPresetExportFile.Open(new[] { preset }, source, new CancellationToken(true)), "Mac preset merge pre-cancel refuses open");
        File.WriteAllText(source, "{}"); reject(() => MacPresetExportFile.Open(new[] { preset }, source, default), "Mac preset invalid merge source refused");
        File.WriteAllBytes(source, mac); check(File.ReadAllBytes(source).SequenceEqual(mac), "Mac preset invalid source releases file lease");
        var app = Application.Current as App ?? new App();
        var preview = new MacPresetExportDialog(app, plan, null, default);
        var root = (DockPanel)preview.Content; var footer = root.Children.OfType<WrapPanel>().Single();
        var body = (StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;
        check(DockPanel.GetDock(footer) == Dock.Bottom && footer.Children.OfType<Button>().Count() == 4, "Mac preset preview fixed save/merge/copy/cancel footer");
        check(body.Children.OfType<TextBlock>().Any(t => t.Text == app.L(MacPresetExport.Replace)) && body.Children.OfType<TextBox>().Single().Text.Contains(MacPresetExport.Command), "Mac preset preview shows replacement warning and exact fixed install steps before writing");
        check(body.Children.OfType<TextBlock>().Any(t => t.Text == app.L("Recipe values preserved for Mac")), "Mac preset preview labels exact recipe row"); preview.Close();
        var refused = MacPresetExport.Prepare(new[] { preset with { Recipe = new("gif", AutoPerImage: true) } });
        var blocked = new MacPresetExportDialog(app, refused, null, default);
        var blockedRoot = (DockPanel)blocked.Content;
        check(!blockedRoot.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().First().IsEnabled, "Mac preset all-refused preview disables export");
        var refusedBody = (StackPanel)blockedRoot.Children.OfType<ScrollViewer>().Single().Content;
        check(refusedBody.Children.OfType<TextBlock>().Any(t => t.Text == app.L(MacPresetExport.Format)) && refusedBody.Children.OfType<TextBlock>().Any(t => t.Text == app.L(MacPresetExport.Photo)), "Mac preset preview lists every row refusal reason"); blocked.Close();
        var result = new MacPresetInstallDialog(app, output, default); var resultRoot = (DockPanel)result.Content;
        var resultBody = (StackPanel)resultRoot.Children.OfType<ScrollViewer>().Single().Content;
        check(resultBody.Children.OfType<TextBox>().All(box => box.IsReadOnly) && resultBody.Children.OfType<TextBox>().Any(box => box.Text == output) && resultBody.Children.OfType<TextBox>().Any(box => box.Text.Contains(MacPresetExport.Command)), "Mac preset result preserves actual output path and selectable fixed installation steps"); result.Close();
        Directory.Move(folder, folder + " released"); check(Directory.Exists(folder + " released"), "Mac preset all source/ancestor/output leases released");
    }
}

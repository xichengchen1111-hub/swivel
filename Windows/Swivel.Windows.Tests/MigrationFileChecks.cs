using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using Swivel.Core;
using Swivel.Windows;

internal static class MigrationFileChecks
{
    public static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        var preset = new ImagePreset(Guid.NewGuid().ToString("N"), "Backup", new("png", 800));
        string first = PresetLibraryFile.Save(new[] { preset }, directory, CancellationToken.None);
        string second = PresetLibraryFile.Save(new[] { preset }, directory, CancellationToken.None);
        byte[] original = File.ReadAllBytes(first);
        check(first != second && File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)), "Preset backups do not replace existing files");
        using (var library = PresetLibraryFile.Open(first, CancellationToken.None))
        {
            check(library.Import.Compatible.Single().Recipe == preset.Recipe, "Native preset backup/import round trip");
            reject(() => File.WriteAllText(first, "changed"), "Import source cannot change during preview");
            reject(() => Directory.Move(directory, directory + ".moved"), "Import parent stays pinned during preview");
        }
        check(File.ReadAllBytes(first).SequenceEqual(original), "Import never modifies source");
        string macQr = Path.Combine(directory, "Mac QR library.json");
        File.WriteAllText(macQr, JsonSerializer.Serialize(new[] { new { id = Guid.NewGuid().ToString("D"), name = "Imported", value = "QR content" } }));
        byte[] macBytes = File.ReadAllBytes(macQr);
        using (var library = MacQrLibraryFile.Open(macQr, CancellationToken.None))
        {
            check(library.Import.Compatible.Single().Text == "QR content", "Native Mac QR source read");
            reject(() => File.Move(macQr, macQr + ".moved"), "Mac QR source pinned through review");
        }
        check(File.ReadAllBytes(macQr).SequenceEqual(macBytes), "Mac QR source remains byte-identical");
        reject(() => MacQrLibraryFile.Open(macQr, new CancellationToken(true)), "Mac QR cancelled before reading");
        int count = Directory.GetFiles(directory).Length;
        reject(() => PresetLibraryFile.Save(new[] { preset }, directory, new CancellationToken(true)), "Cancelled preset backup rejected");
        reject(() => PresetLibraryFile.Open(first, new CancellationToken(true)), "Cancelled preset load rejected");
        check(Directory.GetFiles(directory).Length == count && !Directory.GetFiles(directory, ".swivel-*.tmp").Any(), "Cancelled migration leaves no output or staging");
    }
}

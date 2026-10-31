using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

public sealed partial class App
{
    private readonly Dictionary<string, ShelfReference> shelfReferences = new(StringComparer.OrdinalIgnoreCase);
    internal string[] ValidateShelf(IEnumerable<string> paths, CancellationToken token = default)
        => ShelfReferences.Inspect(paths, NativeFiles.InspectShelfReference, token).Select(x => x.Path).ToArray();
    internal string ShelfLabel(string path)
    {
        if (!shelfReferences.TryGetValue(path, out var reference)) return path;
        return path + "\n" + L(reference.IsFolder ? "Folder reference" : "File reference") +
            (reference.IsOnlineOnly ? " · " + L("Online only") : "");
    }
    private void ShelfReferencesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Renames/moves keep the session's metadata label. No row refresh reads contents.
        if (e.Action == NotifyCollectionChangedAction.Replace && e.OldItems?[0] is string oldPath && e.NewItems?[0] is string newPath &&
            shelfReferences.TryGetValue(oldPath, out var reference)) shelfReferences[newPath] = reference with { Path = newPath };
        foreach (string removed in shelfReferences.Keys.Where(x => !Shelf.Contains(x, StringComparer.OrdinalIgnoreCase)).ToArray()) shelfReferences.Remove(removed);
    }
    internal Task PreviewShelfSystemAsync(string[] files, Window owner, Action<string> report)
        => RunActionAsync(new("shelf.systempreview", "System Preview…", Feature.Shelf, SingleFile: true), files, owner, report);

    private async Task ShowShelfSystemPreviewAsync(string[] files, Window owner, Action<string> report, CancellationTokenSource cancellation)
    {
        if (!Prefs.IsOn(Feature.Shelf)) throw new UserError("Enable File Shelf in Settings first.");
        workFeature = Feature.Shelf; var token = cancellation.Token;
        if (files.Length != 1) throw new UserError("Select exactly one file for this action.");
        string path = files[0];
        if (!Shelf.Contains(path, StringComparer.OrdinalIgnoreCase)) throw new UserError("This reference is no longer on the Shelf.");
        void Changed(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (Shelf.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            cancellation.Cancel(); operationDialog?.Close();
        }
        Shelf.CollectionChanged += Changed;
        try
        {
            report(L("Looking for a cached system thumbnail…"));
            var bitmap = await OnSta(() => ShelfSystemThumbnail.Load(path, token));
            token.ThrowIfCancellationRequested();
            if (bitmap is null) { report(L("No cached system thumbnail is available. The reference is unchanged.")); return; }
            var dialog = new ShelfSystemPreviewDialog(this, path, bitmap);
            try { ShowOperationDialog(dialog, owner, token); }
            finally { dialog.ReleaseImage(); }
            report(L("System preview closed. The reference is unchanged."));
        }
        finally { Shelf.CollectionChanged -= Changed; }
    }
}

internal sealed class ShelfLabelConverter(App app) : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => value is string path ? app.ShelfLabel(path) : "";
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

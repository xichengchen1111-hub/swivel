using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public enum BatchState { Saved, Failed, Cancelled, NotStarted }
public sealed record ImageBatchEntry(string Input, BatchState State, string? Output = null, Exception? Error = null);

public static class ImageBatch
{
    // Stop on the first error, preserving completed outputs and reporting every
    // remaining input. Never retry implicitly or roll back already published files.
    public static IReadOnlyList<ImageBatchEntry> Run(IReadOnlyList<string> inputs,
        Func<string, string> convert, CancellationToken cancellation, Action<int, int>? progress = null)
    {
        var entries = new List<ImageBatchEntry>();
        bool stopped = false;
        for (int index = 0; index < inputs.Count; index++)
        {
            string input = inputs[index];
            if (stopped) { entries.Add(new(input, BatchState.NotStarted)); continue; }
            try
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Invoke(index + 1, inputs.Count);
                cancellation.ThrowIfCancellationRequested();
                entries.Add(new(input, BatchState.Saved, convert(input)));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { entries.Add(new(input, BatchState.Cancelled)); stopped = true; }
            catch (Exception error)
            { entries.Add(new(input, BatchState.Failed, Error: error)); stopped = true; }
        }
        return entries;
    }
}

public static class ImageOutputInfo
{
    public static string Format(string action, ImageRecipe? recipe = null)
    {
        if (action.StartsWith("preset.", StringComparison.Ordinal))
        {
            if (recipe is null) throw new ArgumentException("Missing preset recipe.");
            recipe.Validate(); return recipe.Format;
        }
        return action switch {
            "convert.jpg" or "image.compress" => "jpg", "convert.bmp" => "bmp",
            "convert.tiff" => "tiff", "convert.gif" => "gif", "convert.ico" => "ico", _ => "png"
        };
    }
}

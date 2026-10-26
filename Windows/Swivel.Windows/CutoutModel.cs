using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// The pinned U²-Net background model.
internal static class CutoutModel
{
    private static readonly PinnedModel Model = new(Cutout.ModelFile, Cutout.ModelBytes, Cutout.ModelSha256,
        new long[] { 1, 3, Cutout.Side, Cutout.Side }, Cutout.Side * Cutout.Side,
        "The background model is not included in this copy of Swivel. Use a complete package.",
        "The background model in this copy of Swivel is damaged. Use a complete package.",
        "Windows ML could not run the background model on this PC.");

    /// Tests point this at their own copy; the app uses the Models folder beside Swivel.exe.
    internal static string ModelPath { get => Model.Path; set => Model.Path = value; }

    public static float[] Predict(float[] input, CancellationToken token) => Model.Predict(input, token);
}

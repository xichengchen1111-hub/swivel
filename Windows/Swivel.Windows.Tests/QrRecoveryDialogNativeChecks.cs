using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Swivel.Core;
using Swivel.Windows;

internal static class QrRecoveryDialogNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T value) yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var descendant in Find<T>(child)) yield return descendant;
    }

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var app = Application.Current as App ?? throw new InvalidOperationException("An isolated validation App is required.");
        if (!app.IsValidationHost) throw new InvalidOperationException("Do not run this fixture in a normal product App.");
        // Display completed synthetic results only. The copy callback is an
        // in-memory recorder, not the system clipboard. No image is opened,
        // encoded, repaired, saved or added to a QR library by these tests.
        string previous = app.Prefs.Language;
        const string path = "C:\\Fixture\\source\u202e.png";
        const string text = "https://example.invalid/?text=\"quoted\"\r\n日本語 😀 é\u202e\u2028[U+202E]";
        try
        {
            foreach (string language in new[] { "en", "zh-Hans", "zh-Hant", "ja" })
            {
                app.Prefs.SetLanguage(language);
                foreach (QrCorrection level in Enum.GetValues<QrCorrection>())
                foreach (int rotation in new[] { 0, 90, 180, 270 })
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    var result = new QrReadResult(text, 40, level, 7, 2) { ClockwiseOrientationDegrees = rotation };
                    QrDataRecoveryResult? recovery = mode == 0 ? null : new(result, mode == 1 ? 0 : 17, mode == 1 ? 0 : 3);
                    int copies = 0; string? copied = null;
                    var dialog = new QrReadDialog(app, path, result, recovery, value => { copies++; copied = value; });
                    try
                    {
                        var root = (DockPanel)dialog.Content;
                        var footer = root.Children.OfType<WrapPanel>().Single();
                        var scroll = root.Children.OfType<ScrollViewer>().Single();
                        var body = (StackPanel)scroll.Content;
                        var buttons = footer.Children.OfType<Button>().ToArray();
                        var copy = buttons.Single(button => Equals(button.Content, app.L("Copy Decoded Text")));
                        var close = buttons.Single(button => Equals(button.Content, app.L("Close")));
                        var boxes = body.Children.OfType<TextBox>().ToArray();
                        var preview = boxes.Single(box => AutomationProperties.GetName(box) == app.L(recovery is null ? "Decoded QR Text" : "Recovered QR Text Candidate"));
                        var visible = body.Children.OfType<TextBlock>().Select(block => block.Text).ToArray();
                        check(dialog.Title == app.L(mode == 0 ? "Read QR Image" : "Recover Flat QR Text"),
                            "QR result title distinguishes explicit recovery from unchanged strict reading");
                        check(buttons.Length == 2 && close.IsCancel && buttons.All(button => !button.IsDefault) && copies == 0,
                            "QR result offers only explicit Copy/Close, no default action, implicit copy or launch");
                        check(DockPanel.GetDock(footer) == Dock.Bottom && root.Children.IndexOf(footer) < root.Children.IndexOf(scroll),
                            "QR copy and close remain outside the scrolling result and scope");
                        check(boxes.Length == 2 && boxes.All(box => box.IsReadOnly) && boxes.Any(box => box.Text == DoctorFacts.VisibleBounded(path, 512)),
                            "Both QR modes retain the old read-only escaped source path presentation");
                        check(preview.Text == QrFlatRead.VisibleText(text) && preview.Text != text && preview.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                            "Untrusted decoded controls are visibly escaped in the scrollable read-only preview");
                        check(visible.Contains(string.Format(app.L("QR version {0} · level {1} · {2} pixels/module · {3} UTF-16 units"), 40, QrCode.Level(level), 2, text.Length)) &&
                            visible.Contains(string.Format(app.L("Image orientation: {0}° clockwise from upright."), rotation)),
                            "Version, correction level, scale, length and all four orientations remain unchanged in both modes");
                        check(visible.Contains(app.L(QrReadDialog.CopyNotice)), "Both QR modes retain the exact original-text copy and no-automatic-actions notice");
                        if (recovery is null)
                        {
                            check(visible.Contains(app.L(QrPhotoRead.Scope)) && !visible.Contains(app.L(QrFlatRead.Scope)) && !visible.Contains(app.L("Data Recovery Mode")) && !Find<Expander>(dialog).Any(),
                                "Reading shows the combined flat-then-photo scope and has no recovery count or mode claim");
                        }
                        else
                        {
                            check(visible.Contains(app.L("Data Recovery Mode")) && visible.Contains(app.L(QrReadDialog.RecoveryNotice)) &&
                                visible.Contains(string.Format(app.L("Changed encoded bytes: {0} · affected error-correction blocks: {1}"), recovery.CorrectedCodewords, recovery.CorrectedBlocks)),
                                "Recovery shows exact accepted-candidate byte/block differences and explicitly disclaims damage or correctness proof");
                            var scope = Find<Expander>(dialog).Single();
                            check(!scope.IsExpanded && Equals(scope.Header, app.L("Recovery Scope and Limitations")) &&
                                ((TextBlock)scope.Content).Text == app.L(QrFlatRead.RecoveryScope) && !visible.Contains(app.L(QrFlatRead.Scope)) && !visible.Contains(app.L(QrPhotoRead.Scope)),
                                "Recovery keeps its full distinct limits in a native collapsed expander without claiming parity-only reading");
                        }
                        copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        check(copies == 1 && copied == text && copied != preview.Text,
                            "Only an explicit Copy passes the complete original accepted text, never the escaped preview or recovery metadata");
                        copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        check(copies == 2 && copied == text && preview.Text == QrFlatRead.VisibleText(text),
                            "Repeated explicit copies preserve the exact result without changing its displayed data");
                    }
                    finally { dialog.Close(); }
                }
            }
            foreach (string language in new[] { "en", "zh-Hans", "zh-Hant", "ja" })
            foreach (string value in new[] { "", new string('a', 2952), "literal [U+0000]", "leading\u0000middle\u0000end" })
            foreach (bool recover in new[] { false, true })
            {
                app.Prefs.SetLanguage(language);
                string? received = null; int copies = 0;
                var result = new QrReadResult(value, 40, QrCorrection.Low, 0, 1);
                var dialog = new QrReadDialog(app, path, result, recover ? new QrDataRecoveryResult(result, 0, 0) : null, raw => { received = raw; copies++; });
                try
                {
                    var copy = Find<Button>(dialog).Single(button => Equals(button.Content, app.L("Copy Decoded Text")));
                    copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    if (value.Contains('\0'))
                    {
                        check(!copy.IsEnabled && copies == 0 && received is null,
                            "Both modes disable NUL copying and guard the callback even when a stale click event is raised");
                        check(Find<TextBlock>(dialog).Any(block => block.Text == app.L(QrReadDialog.NulCopyNotice)) &&
                            Find<TextBox>(dialog).Any(box => box.Text == "leading[U+0000]middle[U+0000]end"),
                            "NUL clipboard incompatibility is explicit while the complete escaped text remains visible without substitute copies");
                    }
                    else check(copy.IsEnabled && copies == 1 && received == value,
                        "The UI copy handoff preserves empty, maximum-size and literal-marker non-NUL text; this does not test the system clipboard");
                }
                finally { dialog.Close(); }
            }
            foreach (string language in new[] { "en", "zh-Hans", "zh-Hant", "ja" })
            {
                app.Prefs.SetLanguage(language);
                var photo = new QrReadResult("photo text", 8, QrCorrection.Medium, 5, 4) { ClockwiseOrientationDegrees = 133, Photo = true, CorrectedCodewords = 6, CorrectedBlocks = 2 };
                var dialog = new QrReadDialog(app, path, photo, null, _ => { });
                try
                {
                    var visible = Find<TextBlock>(dialog).Select(block => block.Text).ToArray();
                    check(dialog.Title == app.L("Read QR Image") &&
                        visible.Contains(string.Format(app.L("Read from a photo or screenshot: QR version {0} · level {1} · about {2} pixels/module · {3} UTF-16 units"), 8, QrCode.Level(QrCorrection.Medium), 4, 10)) &&
                        visible.Contains(string.Format(app.L("Code orientation: about {0}° clockwise from upright."), 133)) &&
                        visible.Contains(string.Format(app.L("Error correction changed {0} encoded bytes in {1} blocks. Zero does not prove the photo was perfect, and beyond the correction capacity another valid message could be accepted."), 6, 2)) &&
                        visible.Contains(app.L(QrPhotoRead.Scope)) && !visible.Contains(string.Format(app.L("Image orientation: {0}° clockwise from upright."), 133)),
                        "Photo results state estimated size and angle, the corrected-byte count and the combined scope in four languages");
                }
                finally { dialog.Close(); }
            }
        }
        finally { app.Prefs.SetLanguage(previous); }
    }
}

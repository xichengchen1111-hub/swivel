using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

/// Full review before anything is written: every folder and file the archive will create.
/// Encrypted ZIP members are named with their method; the password is asked only here.
internal sealed class ZipExtractDialog : Window
{
    /// The typed password for an encrypted ZIP, after it matched the first encrypted member.
    /// The caller owns and disposes it; null when nothing is encrypted or the dialog was cancelled.
    public ZipPassword? Password { get; private set; }

    public ZipExtractDialog(App app, ZipExtractWork work)
    {
        var encrypted = work.Encrypted;
        PasswordBox? box = null; TextBlock? status = null;
        Title = app.L("Extract Archive…");
        MoveFilesLayout.Configure(this);
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel();
        var extract = UI.Button(app.L("Extract"), () =>
        {
            if (box is null) { DialogResult = true; return; }
            ZipPassword? typed = null;
            try
            {
                typed = Read(box);
                if (typed is null) { status!.Text = app.L("Type the archive's password."); box.Focus(); return; }
                if (!work.PasswordMatchesFirst(typed, CancellationToken.None))
                {
                    status!.Text = app.L("The password is not correct. Nothing was extracted."); box.Clear(); box.Focus(); return;
                }
                Password = typed; typed = null; box.Clear(); DialogResult = true;
            }
            catch (UserError error) { status!.Text = app.L(error.Message); }
            catch (System.IO.IOException) { status!.Text = app.L("The archive could not be read. Nothing was extracted."); }
            finally { typed?.Dispose(); }
        });
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false);
        cancel.IsCancel = true; extract.IsDefault = true;
        buttons.Children.Add(extract); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

        var top = new StackPanel();
        top.Children.Add(UI.Text(work.Path));
        top.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture, app.L("{0} file(s) and {1} folder(s), {2} MB after extraction."),
            work.Plan.Files.Count, work.Plan.Directories.Count, (work.Plan.TotalBytes / 1_000_000.0).ToString("0.##", CultureInfo.CurrentCulture)), 18));
        top.Children.Add(UI.Text(app.L(work.Tar is null
            ? "Files are extracted into a new folder beside the archive, named after it. Existing files and folders are never overwritten. Each file is checked against its size and CRC-32 before it appears."
            : "Files are extracted into a new folder beside the archive, named after it. Existing files and folders are never overwritten. TAR stores no file checksums, so each file is checked against its size and the CRC-32 Swivel recorded while reviewing the archive; a compressed archive's own checksum is checked too."), 12));
        top.Children.Add(UI.Text(app.L(work.Zone == 4
            ? "The archive is marked as Restricted, so every extracted file is marked Restricted too."
            : "Every extracted file is marked as downloaded from the Internet (Mark of the Web), even if the archive itself was not."), 12));
        top.Children.Add(UI.Text(app.L(work.Tar is null
            ? "Modified times from the archive are kept. Permissions, attributes, comments and other data stored in the archive are not restored. If extraction stops, files already extracted stay in the new folder; it is not removed automatically."
            : "Modified times from the archive are kept. Owners, permissions, extended attributes and other data stored in the archive are not restored. If extraction stops, files already extracted stay in the new folder; it is not removed automatically."), 12));
        if (encrypted.Count > 0)
        {
            string methods = string.Join(", ", encrypted.Select(file => MethodName(file.Encryption)).Distinct());
            top.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture, app.L("{0} of {1} file(s) are encrypted ({2}). Type the archive's password to extract them."),
                encrypted.Count, work.Plan.Files.Count, methods), 12));
            if (encrypted.Any(file => file.Encryption == ZipEncryption.ZipCrypto))
                top.Children.Add(UI.Text(app.L("Some files use ZipCrypto, the old PKWARE encryption. It is weak and can be broken, so do not rely on it to protect anything. Swivel only opens it and never creates it."), 12));
            if (encrypted.Any(file => file.Encryption != ZipEncryption.ZipCrypto))
                top.Children.Add(UI.Text(app.L("Each AES-encrypted file is checked against its authentication code before it is decrypted, and appears only if that check passes."), 12));
            top.Children.Add(UI.Text(app.L("If some files use a different password, they are left out and listed after extraction. Strong PKWARE encryption is not supported."), 12));
            top.Children.Add(UI.Text(app.L("The password stays in memory for this extraction only. It is never saved, logged or sent anywhere. Windows cannot guarantee that every copy in memory is erased afterwards."), 12));
            box = new PasswordBox { MinHeight = 30, MaxLength = ZipPassword.MaxBytes, Margin = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetName(box, app.L("Archive password"));
            top.Children.Add(UI.Text(app.L("Archive password"), 12)); top.Children.Add(box);
            status = UI.Text("", 12); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); top.Children.Add(status);
            Closed += (_, _) => box.Clear();
        }
        if (work.Plan.SkippedAppleDouble > 0)
            top.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture, app.L("{0} macOS attribute file(s) (._ names) are left out."), work.Plan.SkippedAppleDouble), 12));
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

        var list = new ListBox { Margin = new Thickness(0, 8, 0, 12) };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(list, app.L("Paths in the new folder"));
        list.ItemsSource = work.Plan.Directories.Select(path => path + "\\")
            .Concat(work.Plan.Files.Select(file => file.Encryption == ZipEncryption.None ? file.Path
                : string.Format(CultureInfo.CurrentCulture, app.L("{0} (encrypted, {1})"), file.Path, MethodName(file.Encryption)))).ToArray();
        root.Children.Add(list);
        Content = root;
        Loaded += (_, _) => { if (box is not null) box.Focus(); else cancel.Focus(); };
    }

    private static string MethodName(ZipEncryption kind) => kind switch
    {
        ZipEncryption.Aes128 => "AES-128", ZipEncryption.Aes192 => "AES-192", ZipEncryption.Aes256 => "AES-256", _ => "ZipCrypto",
    };

    // From the box's protected copy, without making a managed string; the unmanaged and
    // char copies are zeroed. Null when nothing was typed.
    private static ZipPassword? Read(PasswordBox box)
    {
        using var secure = box.SecurePassword;
        if (secure.Length == 0) return null;
        IntPtr unmanaged = Marshal.SecureStringToGlobalAllocUnicode(secure);
        char[] chars = new char[secure.Length];
        try { Marshal.Copy(unmanaged, chars, 0, chars.Length); return new ZipPassword(chars); }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(unmanaged); Array.Clear(chars); }
    }
}

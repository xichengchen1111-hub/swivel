using System;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class WebRouteDialog : Window
{
    public WebRouteDialog(App app, AppRoute? existing = null)
    {
        Title = app.L(existing is null ? "Add Web Page…" : "Edit Web Page…");
        Width = 650; Height = 700; MinWidth = 450; MinHeight = 380; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) };
        var actions = new WrapPanel(); DockPanel.SetDock(actions,Dock.Bottom); root.Children.Add(actions);
        var panel = new StackPanel(); root.Children.Add(UI.Scroll(panel)); Content = root;
        TextBox Field(string key, string value)
        {
            panel.Children.Add(UI.Text(app.L(key))); var field = new TextBox { Text = value, MinHeight = 30 };
            AutomationProperties.SetName(field,app.L(key)); panel.Children.Add(field); return field;
        }
        var name = Field("Action Name",existing?.Name ?? ""); name.MaxLength = 48;
        var address = Field("Web Address",existing?.WebAddress ?? "");
        address.AcceptsReturn = true; address.TextWrapping = TextWrapping.Wrap; address.MaxHeight = 110;
        address.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        panel.Children.Add(UI.Text(app.L("Use an explicit HTTPS address, or HTTP to localhost, 127.0.0.1 or [::1]. Saving does not open the page. Up to 2,048 characters; no credentials or hidden characters."),12));
        panel.Children.Add(UI.Text(app.L("International domain names must use ASCII (xn--) form. Unicode paths and query text are supported."),12));
        panel.Children.Add(UI.Text(app.L("Address to Open")));
        var resolved = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(resolved,app.L("Address to Open")); panel.Children.Add(resolved);
        var customBrowser = new CheckBox { Content = app.L("Use a selected browser application"), IsChecked = existing?.BrowserExecutable is not null, Margin = new Thickness(0,12,0,8) }; panel.Children.Add(customBrowser);
        var browser = Field("Browser Application",existing?.BrowserExecutable ?? ""); browser.IsReadOnly = true; browser.TextWrapping = TextWrapping.Wrap;
        var chooseBrowser = UI.Button(app.L("Choose Browser EXE…"),() => {
            var picker = new OpenFileDialog { Filter = app.L("Windows applications (*.exe)|*.exe"), CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(this) == true) browser.Text = picker.FileName;
        }); panel.Children.Add(chooseBrowser);
        panel.Children.Add(UI.Text(app.L("Choose a trusted local browser EXE that accepts one web address as an argument. No extra switches are sent. Swivel checks the executable format, not the browser identity or publisher."),12));
        var copy = new CheckBox { Content = app.L("Copy file references before opening"), IsChecked = existing?.CopyToClipboard ?? false, Margin = new Thickness(0,12,0,8) }; panel.Children.Add(copy);
        var types = Field("File extensions (optional)",existing?.Extensions ?? ""); types.MaxLength = 256;
        panel.Children.Add(UI.Text(app.L("Enter up to 16 extensions separated by commas, such as png,jpg. Leave blank for any file."),12));
        panel.Children.Add(UI.Text(app.L("Only the saved address is sent to the chosen browser. File names and contents are never added to the address. The browser and website control subsequent network activity."),12));
        panel.Children.Add(UI.Text(app.L("Optional copying replaces the clipboard with local file references after confirmation. Nothing is uploaded or pasted automatically. Some websites do not accept these references; choose the files in the website yourself."),12));
        var error = UI.Text(""); panel.Children.Add(error);
        var save = UI.Button(app.L("Save Web Page"), () => {
            try {
                string url = WebAddress.Normalize(address.Text);
                var route = new AppRoute(existing?.Id ?? Guid.NewGuid().ToString("N"),name.Text.Trim(),"",copy.IsChecked == true ? types.Text.Trim() : "",copy.IsChecked == true,url,customBrowser.IsChecked == true ? browser.Text : null);
                app.Prefs.SaveApp(route); DialogResult = true;
            } catch (UserError ex) { error.Text = app.L(ex.Message); }
        }); actions.Children.Add(save);
        var cancel = UI.Button(app.L("Cancel"),() => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        void Preview()
        {
            resolved.Text = ""; save.IsEnabled = false;
            try { resolved.Text = WebAddress.Normalize(address.Text); error.Text = ""; save.IsEnabled = true; }
            catch (UserError ex) { error.Text = app.L(ex.Message); }
        }
        void BrowserChoice() { browser.IsEnabled = chooseBrowser.IsEnabled = customBrowser.IsChecked == true; }
        customBrowser.Checked += (_,_) => BrowserChoice(); customBrowser.Unchecked += (_,_) => BrowserChoice(); BrowserChoice();
        void Delivery() { types.IsEnabled = copy.IsChecked == true; }
        address.TextChanged += (_,_) => Preview(); copy.Checked += (_,_) => Delivery(); copy.Unchecked += (_,_) => Delivery();
        Delivery(); Preview();
        SourceInitialized += (_,_) => WebRouteLayout.Fit(this,Owner ?? this); DpiChanged += (_,_) => WebRouteLayout.Fit(this,this);
    }
}

internal sealed class WebLaunchDialog : Window
{
    public WebLaunchDialog(App app, WebLaunch launch)
    {
        Title = app.L("Review Web Page Action"); Width = 700; Height = 540; MinWidth = 450; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) };
        var actions = new WrapPanel(); DockPanel.SetDock(actions,Dock.Bottom); root.Children.Add(actions);
        var panel = new StackPanel(); root.Children.Add(UI.Scroll(panel)); Content = root;
        panel.Children.Add(UI.Text(launch.Route.Name,20)); panel.Children.Add(UI.Text(app.L("Address to Open")));
        var address = new TextBox { Text = launch.Address, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(address,app.L("Address to Open")); panel.Children.Add(address);
        panel.Children.Add(UI.Text(app.L("Browser Application")));
        var browser = new TextBox { Text = launch.Route.BrowserExecutable is null ? app.L("Default browser") : DoctorFacts.VisibleBounded(launch.Route.BrowserExecutable,512), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(browser,app.L("Browser Application")); panel.Children.Add(browser);
        panel.Children.Add(UI.Text(app.L("Only the saved address is sent to the chosen browser. File names and contents are never added to the address. The browser and website control subsequent network activity.")));
        if (launch.Route.CopyToClipboard)
        {
            panel.Children.Add(UI.Text(app.L("These local file references will replace the clipboard. Paste or choose the files yourself on the website. If the browser fails to open, the references remain on the clipboard.")));
            foreach (string file in launch.Files) panel.Children.Add(UI.Text(file));
        }
        else panel.Children.Add(UI.Text(app.L("This action does not read selected files or change the clipboard.")));
        actions.Children.Add(UI.Button(app.L(launch.Route.CopyToClipboard ? "Copy Files and Open Web Page" : "Open Web Page"),() => DialogResult = true));
        var cancel = UI.Button(app.L("Cancel"),() => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        SourceInitialized += (_,_) => WebRouteLayout.Fit(this,Owner ?? this); DpiChanged += (_,_) => WebRouteLayout.Fit(this,this);
    }
}

internal static class WebRouteLayout
{
    public static void Fit(Window window, Window reference)
    {
        var area=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(reference).Handle).WorkingArea;
        var dpi=VisualTreeHelper.GetDpi(reference);
        double height=Math.Max(1,area.Height/dpi.DpiScaleY-16), width=Math.Max(1,area.Width/dpi.DpiScaleX-16);
        window.MinHeight=Math.Min(320,height); window.MinWidth=Math.Min(450,width);
        window.MaxHeight=height; window.MaxWidth=width;
        window.Height=Math.Min(window.Height,height); window.Width=Math.Min(window.Width,width);
    }
}

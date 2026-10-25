using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Swivel.Core;
using Swivel.Windows;

internal static class RecipeControlChecks
{
    public static void Run(Action<bool,string> check)
    {
        // Construct controls only. Never call App.Run/OnStartup, so no tray, settings
        // storage, shortcuts, file operation or actual window is started by this check.
        var app = Application.Current as App ?? new App();
        var navigation = new NavigationShell(app.L("Tools"));
        var first = new TextBox { Text = "unfinished input" }; var second = new StackPanel();
        navigation.Add("Files", first); navigation.Add("Settings", second);
        navigation.SelectedIndex = 1;
        check(ReferenceEquals(navigation.SelectedPage, second), "Navigation shows selected page");
        navigation.SelectedIndex = 0;
        check(ReferenceEquals(navigation.SelectedPage, first) && first.Text == "unfinished input", "Navigation preserves unfinished input");
        navigation.SelectedIndex = 99;
        check(navigation.SelectedIndex == 1, "Stale navigation index is bounded");
        check(app.Resources.MergedDictionaries.Any(x => x.Source?.OriginalString.Contains("PresentationFramework.Fluent", StringComparison.Ordinal) == true), "Native Fluent resources loaded");
        var original = new PhotoSettings(Exposure:1.25,Highlights:-.4,Shadows:.2,Vibrance:.35,Contrast:.1,Look:PhotoLook.Warm);
        var control = new PhotoRecipeControl(app,original);
        void Toggle(PhotoRecipeControl target,bool enabled)
        {
            var box = Descendants(target).OfType<CheckBox>().Single(x => Equals(x.Content,app.L("Recalculate Auto for each image")));
            box.IsChecked = enabled; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        Toggle(control,true);
        var recipe = new ImageRecipe("png",Adjustments:control.Read(),AutoPerImage:control.AutoPerImage);
        var prefs = new Prefs(); prefs.SavePreset(new(Guid.NewGuid().ToString("N"),"Saved Auto",recipe));
        var reloaded = Prefs.Parse(prefs.Serialize()).Presets.Single().Recipe;
        check(reloaded.AutoPerImage && reloaded.Adjustments == original,"Enabling Auto saves all original manual values without replacing them");
        var reopened = new PhotoRecipeControl(app,reloaded.Adjustments,reloaded.AutoPerImage); Toggle(reopened,false);
        check(!reopened.AutoPerImage && reopened.Read() == original,"Disable Auto after save/reload restores every manual field");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}

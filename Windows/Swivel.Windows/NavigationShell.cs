using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Swivel.Windows;

// Standard ListBox selection supplies arrow-key navigation, focus and UI Automation.
// Retain each page instance so navigation does not discard an unfinished form.
internal sealed class NavigationShell : Grid
{
    private readonly ListBox navigation = new() {
        BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 12, 0),
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly ContentControl content = new() {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private readonly List<UIElement> pages = new();
    public int SelectedIndex {
        get => navigation.SelectedIndex;
        set => navigation.SelectedIndex = pages.Count == 0 ? -1 : Math.Clamp(value, 0, pages.Count - 1);
    }
    internal UIElement? SelectedPage => content.Content as UIElement;

    public NavigationShell(string accessibleName)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(174) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetName(navigation, accessibleName);
        ScrollViewer.SetHorizontalScrollBarVisibility(navigation, ScrollBarVisibility.Disabled);
        navigation.SelectionChanged += (_, _) => {
            content.Content = SelectedIndex >= 0 ? pages[SelectedIndex] : null;
        };
        Children.Add(navigation); SetColumn(content, 1); Children.Add(content);
    }
    public void Add(string title, UIElement page)
    {
        pages.Add(page);
        navigation.Items.Add(new ListBoxItem {
            Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap },
            Padding = new Thickness(10, 9, 10, 9), MinHeight = 40
        });
        if (SelectedIndex < 0) SelectedIndex = 0;
    }
}

using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class MetadataSelectionPanel : StackPanel
{
    private sealed class Row : INotifyPropertyChanged
    {
        internal readonly MetadataField Field;private readonly App app;private bool remove;
        internal Row(App app,MetadataField field){this.app=app;Field=field;}
        public string Group=>app.L(Field.Group);
        public string Name
        {
            get
            {
                int suffix=Field.Name.IndexOf(" 0x",StringComparison.Ordinal);
                if(suffix<0&&Field.Name.Length>=5&&Field.Name[4]==' '&&Field.Name[..4] is "tEXt" or "zTXt" or "iTXt" or "tIME")suffix=4;
                return suffix<0?app.L(Field.Name):app.L(Field.Name[..suffix])+Field.Name[suffix..];
            }
        }
        public string Directory=>Field.Directory;
        public string AccessibleName=>Name+" "+Directory;
        public string Value=>Field.Preview;
        public int Bytes=>Field.Bytes;
        private bool inherited;
        public bool Inherited
        {
            get=>inherited;set{if(inherited==value)return;inherited=value;PropertyChanged?.Invoke(this,new(nameof(Result)));PropertyChanged?.Invoke(this,new(nameof(CanRemove)));PropertyChanged?.Invoke(this,new(nameof(Checked)));}
        }
        public bool CanRemove=>Field.CanRemove&&!Inherited;
        public bool Remove{get=>remove;set{if(remove==value)return;remove=value;PropertyChanged?.Invoke(this,new(nameof(Remove)));PropertyChanged?.Invoke(this,new(nameof(Result)));PropertyChanged?.Invoke(this,new(nameof(Checked)));}}
        public bool Checked{get=>remove||Inherited;set{if(!Inherited)Remove=value;}}
        public string Result=>app.L(remove||Inherited?"Remove":"Keep");
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly Row[] rows;
    internal int[] SelectedIds=>rows.Where(r=>r.Remove).Select(r=>r.Field.Id).ToArray();
    internal event Action? Changed;
    internal MetadataSelectionPanel(App app,MetadataSelection selection)
    {
        rows=selection.Items.OrderBy(f=>f.Group,StringComparer.Ordinal).ThenBy(f=>f.Directory,StringComparer.Ordinal).ThenBy(f=>f.Id).Select(f=>new Row(app,f)).ToArray();
        var byId=rows.ToDictionary(r=>r.Field.Id);
        Children.Add(UI.Text(app.L(MetadataSelection.Notice),12));
        Children.Add(UI.Text(app.L("Removing Thumbnail removes its whole directory and embedded image, including its displayed child rows. Descriptive thumbnail tags can also be removed individually. A retained thumbnail may contain its own metadata."),12));
        Children.Add(UI.Text(app.L("Preview: ticked rows will be removed; unchecked rows stay. Value previews show at most 96 bytes (32 for binary values). Directory and tag identify each occurrence; structural image and color rows cannot be removed."),12));
        var group=new ComboBox{ItemsSource=rows.Where(r=>r.CanRemove).Select(r=>r.Group).Distinct().ToArray(),MinWidth=180,SelectedIndex=0};AutomationProperties.SetName(group,app.L("Metadata group"));
        var groupButtons=new WrapPanel();groupButtons.Children.Add(group);
        var warning=UI.Text(app.L(MetadataSelection.DuplicateWarning),12);
        var orientation=UI.Text(app.L("Orientation is selected for removal. This may change rotation or mirroring in other apps; pixels are unchanged."),12);
        bool changingGroup=false;
        void SetGroup(bool remove)
        {
            changingGroup=true;foreach(var row in rows)if(row.CanRemove&&row.Group==group.SelectedItem as string)row.Remove=remove;
            changingGroup=false;Update();
        }
        groupButtons.Children.Add(UI.Button(app.L("Remove group"),()=>SetGroup(true)));groupButtons.Children.Add(UI.Button(app.L("Keep group"),()=>SetGroup(false)));Children.Add(groupButtons);
        var list=new ListView{Height=290,ItemsSource=rows};AutomationProperties.SetName(list,app.L("Selective metadata preview"));
        VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);
        var grid=new GridView();var check=new FrameworkElementFactory(typeof(CheckBox));check.SetBinding(CheckBox.IsCheckedProperty,new Binding(nameof(Row.Checked)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});check.SetBinding(IsEnabledProperty,new Binding(nameof(Row.CanRemove)));check.SetBinding(AutomationProperties.NameProperty,new Binding(nameof(Row.AccessibleName)));
        grid.Columns.Add(new GridViewColumn{Header=app.L("Remove"),CellTemplate=new DataTemplate{VisualTree=check},Width=64});
        foreach(var col in new[]{("Metadata group","Group",150d),("Tag / block","Name",170d),("Directory / page","Directory",150d),("Value preview","Value",220d),("Declared value bytes","Bytes",100d),("Result","Result",70d)})grid.Columns.Add(new GridViewColumn{Header=app.L(col.Item1),DisplayMemberBinding=new Binding(col.Item2),Width=col.Item3});
        list.View=grid;Children.Add(list);
        Children.Add(warning);
        Children.Add(orientation);
        void Update()
        {
            if(changingGroup)return;
            foreach(var row in rows)if(row.Field.RemovalParent is int parent)row.Inherited=byId[parent].Remove;
            warning.Visibility=selection.MayDuplicate(SelectedIds)?Visibility.Visible:Visibility.Collapsed;
            orientation.Visibility=rows.Any(r=>r.Remove&&r.Field.Group=="Orientation")?Visibility.Visible:Visibility.Collapsed;Changed?.Invoke();
        }
        foreach(var row in rows)row.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(Row.Remove))Update();};Update();
    }
}

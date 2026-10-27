using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal static class ExecutableStructureNotices
{
    internal const string Scope="Header declarations only. Processor, PE32/PE32+, subsystem and CLR-directory fields do not prove this file can run on this PC. Managed AnyCPU, hybrid images and emulation need additional checks. This tool does not load or run the file, inspect runtime metadata, resolve dependencies, check a manifest, checksum, SmartScreen, permissions or Windows loader acceptance.";
    internal const string Certificates="Certificate information covers only directory bounds and up to 64 record headers with declared offsets, lengths, revisions and types. No certificate payload or certificate padding is read, no signature/hash/certificate chain/trust/revocation is verified, and no certificate store or network is used. A PKCS#7 type header is not a valid-signature verdict; an absent table is not an unsigned verdict because external catalogs are not checked. Partial results are not a security judgment.";
    internal const string Copy="Copy Report is explicit and includes the displayed source path, header facts, recorded certificate-header values and these limits. No report is saved or sent automatically. Keep a pre-upgrade settings backup before downgrading if this new action is saved in My Wheel; Windows 0.59 and earlier can reject its unknown action ID and fall back to defaults.";
}

internal sealed class ExecutableStructureDialog:Window
{
    internal string CurrentReport{get;}="";
    internal ExecutableStructureDialog(App app,ExecutableStructureFile file)
    {
        Title=app.L("EXE Structure");Width=920;Height=850;MinWidth=500;MinHeight=380;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        string path=DoctorFacts.VisibleBounded(file.Path,512);body.Children.Add(new TextBox{Text=path,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=80,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var info=file.Info;var rows=new List<Row>();
        void Add(string label,string value)=>rows.Add(new(app.L(label),value));
        string Number(long value)=>value.ToString("N0",CultureInfo.CurrentCulture);
        string machine=info.Machine switch{0x14c=>"I386 / x86",0x8664=>"AMD64 / x64",0xaa64=>"ARM64",0xa641=>"ARM64EC",0xa64e=>"ARM64X",0x1c4=>"ARMNT",0x200=>"IA64",_=>app.L("Other Header Code")};
        string subsystem=info.Subsystem switch{1=>app.L("Native Subsystem"),2=>app.L("Windows GUI"),3=>app.L("Windows Console"),_=>app.L("Other Header Code")};
        Add("File Size (bytes)",Number(info.FileBytes));Add("PE Header Format",info.Pe32Plus?"PE32+":"PE32");
        Add("Declared Processor",machine+" (0x"+info.Machine.ToString("X4",CultureInfo.InvariantCulture)+")");Add("Declared Subsystem",subsystem+" (0x"+info.Subsystem.ToString("X4",CultureInfo.InvariantCulture)+")");
        Add("Declared Subsystem Version",info.SubsystemMajor.ToString(CultureInfo.InvariantCulture)+"."+info.SubsystemMinor.ToString(CultureInfo.InvariantCulture));
        Add("Raw Image Flags","0x"+info.Characteristics.ToString("X4",CultureInfo.InvariantCulture));Add("EXE Image Flag",app.L((info.Characteristics&2)!=0?"Yes":"No"));Add("DLL Flag",app.L((info.Characteristics&0x2000)!=0?"Yes":"No"));
        Add("Declared Sections",Number(info.Sections));Add("CLR Runtime Directory",app.L(info.ClrDirectory switch{PeDirectoryDeclaration.NotDeclared=>"Not Declared",PeDirectoryDeclaration.Declared=>"Declared",_=>"Inconsistent Declaration"}));
        Add("Certificate Table File Offset",Number(info.Certificates.Offset));Add("Certificate Table Bytes",Number(info.Certificates.Bytes));Add("Recorded Certificate Headers",Number(info.Certificates.Headers.Count));
        Add("Certificate Header Walk",app.L(info.Certificates.State switch{
            PeCertificateState.NotDeclared=>"Certificate table not declared (not an unsigned verdict).",
            PeCertificateState.HeadersCoverTable=>"Record header lengths cover the declared table (not verified).",
            PeCertificateState.Inconsistent=>"Certificate header declarations are inconsistent (partial).",
            PeCertificateState.OutOfBounds=>"Certificate table is outside the file (partial).",
            PeCertificateState.OverlapsImage=>"Certificate table overlaps image headers or raw sections (partial).",
            _=>"Certificate header limit reached (partial)."}));
        var facts=List(350);facts.View=Columns(app,("Field","Name",270),("Value","Value",530));facts.ItemsSource=rows;AutomationProperties.SetName(facts,app.L("EXE Structure"));body.Children.Add(facts);
        var headers=info.Certificates.Headers.Select((h,i)=>new HeaderRow(i+1,Number(h.Offset),Number(h.Length),"0x"+h.Revision.ToString("X4",CultureInfo.InvariantCulture),"0x"+h.Type.ToString("X4",CultureInfo.InvariantCulture))).ToArray();
        body.Children.Add(UI.Text(app.L("Recorded Certificate Headers"),14));var records=List(210);records.View=Columns(app,("Number","Index",70),("File Offset","Offset",170),("Declared Length","Length",170),("Revision","Revision",140),("Type","Type",140));records.ItemsSource=headers;AutomationProperties.SetName(records,app.L("Recorded Certificate Headers"));body.Children.Add(records);
        foreach(string note in new[]{ExecutableStructureNotices.Scope,ExecutableStructureNotices.Certificates,PeInspection.Limits,ExecutableStructureNotices.Copy})body.Children.Add(UI.Text(app.L(note),12));
        var report=new StringBuilder();report.AppendLine(path);foreach(var row in rows)report.AppendLine(row.Name+": "+row.Value);
        if(headers.Length>0)report.AppendLine(string.Join(" · ",new[]{app.L("Number"),app.L("File Offset"),app.L("Declared Length"),app.L("Revision"),app.L("Type")}));
        foreach(var h in headers)report.AppendLine(h.Index.ToString(CultureInfo.InvariantCulture)+" · "+h.Offset+" · "+h.Length+" · "+h.Revision+" · "+h.Type);
        foreach(string note in new[]{ExecutableStructureNotices.Scope,ExecutableStructureNotices.Certificates,PeInspection.Limits,ExecutableStructureNotices.Copy})report.AppendLine(app.L(note));CurrentReport=report.ToString();
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private static ListView List(int height){var view=new ListView{Height=height,SelectionMode=SelectionMode.Single};VirtualizingPanel.SetIsVirtualizing(view,true);VirtualizingPanel.SetVirtualizationMode(view,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(view,true);return view;}
    private static GridView Columns(App app,params(string Label,string Property,double Width)[] columns){var grid=new GridView();foreach(var c in columns)grid.Columns.Add(new GridViewColumn{Header=app.L(c.Label),DisplayMemberBinding=new Binding(c.Property),Width=c.Width});return grid;}
    private sealed record Row(string Name,string Value);
    private sealed record HeaderRow(int Index,string Offset,string Length,string Revision,string Type);
}

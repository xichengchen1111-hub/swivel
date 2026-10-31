using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Swivel.Core;

namespace Swivel.Windows;

internal static class QrGardenMesh
{
    internal const int MaxMeshPositions=20000;
    internal static Model3DGroup Build(QrGarden garden,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(garden);token.ThrowIfCancellationRequested();
        var scene=new Model3DGroup();
        foreach(var boxes in new[]{garden.Ground,garden.Cover,garden.Structure})scene.Children.Add(Layer(boxes,token));
        scene.Freeze();token.ThrowIfCancellationRequested();return scene;
    }
    private static Model3DGroup Layer(IReadOnlyList<GardenBox> boxes,CancellationToken token)
    {
        var layer=new Model3DGroup();var meshes=new Dictionary<uint,MeshGeometry3D>();var materials=new Dictionary<uint,MaterialGroup>();
        void Publish(uint color,MeshGeometry3D mesh)
        {
            token.ThrowIfCancellationRequested();
            if(!materials.TryGetValue(color,out var material))
            {
                // Black diffuse carries opacity in the software path; emission supplies all RGB.
                material=new();material.Children.Add(new DiffuseMaterial(Brushes.Black));
                material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb((byte)(color>>16),(byte)(color>>8),(byte)color))));
                material.Freeze();materials.Add(color,material);
            }
            mesh.Freeze();var model=new GeometryModel3D(mesh,material);model.Freeze();layer.Children.Add(model);
        }
        void Face(uint color,Vector3D normal,Point3D a,Point3D b,Point3D c,Point3D d)
        {
            if(!meshes.TryGetValue(color,out var mesh)||mesh.Positions.Count==MaxMeshPositions)
            {if(mesh is not null)Publish(color,mesh);mesh=new();meshes[color]=mesh;}
            int start=mesh.Positions.Count;
            mesh.Positions.Add(a);mesh.Positions.Add(b);mesh.Positions.Add(c);mesh.Positions.Add(d);
            for(int i=0;i<4;i++)mesh.Normals.Add(normal);
            mesh.TriangleIndices.Add(start);mesh.TriangleIndices.Add(start+1);mesh.TriangleIndices.Add(start+2);
            mesh.TriangleIndices.Add(start);mesh.TriangleIndices.Add(start+2);mesh.TriangleIndices.Add(start+3);
        }
        static uint Shade(uint color,int percent)=>((color>>16&255)*(uint)percent/100<<16)|((color>>8&255)*(uint)percent/100<<8)|((color&255)*(uint)percent/100);
        foreach(var box in boxes)
        {
            token.ThrowIfCancellationRequested();double x0=box.X-box.Width/2,x1=box.X+box.Width/2,z0=box.Z-box.Depth/2,z1=box.Z+box.Depth/2,y0=box.Bottom,y1=box.Top;
            // Exact top RGB; directional shades are baked once, never camera-dependent lighting.
            Face(box.Color,new(0,1,0),new(x0,y1,z0),new(x0,y1,z1),new(x1,y1,z1),new(x1,y1,z0));
            Face(Shade(box.Side,78),new(-1,0,0),new(x0,y0,z0),new(x0,y0,z1),new(x0,y1,z1),new(x0,y1,z0));
            Face(Shade(box.Side,70),new(1,0,0),new(x1,y0,z1),new(x1,y0,z0),new(x1,y1,z0),new(x1,y1,z1));
            Face(Shade(box.Side,90),new(0,0,-1),new(x1,y0,z0),new(x0,y0,z0),new(x0,y1,z0),new(x1,y1,z0));
            Face(Shade(box.Side,86),new(0,0,1),new(x0,y0,z1),new(x1,y0,z1),new(x1,y1,z1),new(x0,y1,z1));
        }
        foreach(var pair in meshes)Publish(pair.Key,pair.Value);
        layer.Freeze();return layer;
    }
}

// Reuse all frozen geometry. Only the small structure material set is mutable.
internal sealed class QrGardenPresentation
{
    internal Model3DGroup Root{get;}=new();
    private readonly Model3DGroup structure=new();private readonly List<Brush> fades=new();
    internal QrGardenPresentation(Model3DGroup scene)
    {
        Root.Children.Add(scene.Children[0]);Root.Children.Add(scene.Children[1]);
        var materials=new Dictionary<Material,MaterialGroup>();
        foreach(GeometryModel3D model in ((Model3DGroup)scene.Children[2]).Children)
        {
            if(!materials.TryGetValue(model.Material,out var material))
            {
                material=((MaterialGroup)model.Material).Clone();materials.Add(model.Material,material);
                foreach(var part in material.Children)fades.Add(part is EmissiveMaterial emission?emission.Brush:((DiffuseMaterial)part).Brush);
            }
            structure.Children.Add(new GeometryModel3D(model.Geometry,material));
        }
        Root.Children.Add(structure);
    }
    internal void SetOpacity(double value)
    {
        if(!double.IsFinite(value)||value is <0 or >1)throw new ArgumentOutOfRangeException(nameof(value));
        // Remove fully transparent geometry so it cannot write depth over a code module.
        if(value==0){Root.Children.Remove(structure);return;}
        if(!Root.Children.Contains(structure))Root.Children.Add(structure);
        foreach(var brush in fades)brush.Opacity=value;
    }
}

internal sealed class QrGardenDialog:Window
{
    public const string Scope="Voxel Tree, Bouquet and Island gardens turn into the code from above. Trunks, stems, paper, boats and filler foliage fade during the turn. Screen resolution, viewing angle and dense codes can prevent scanning. Use the flat QR image for copying, sharing and PNG export; the 3D view has not been verified for scanning.";
    private readonly App app;private readonly CancellationTokenSource cancellation=new();private Task pending=Task.CompletedTask;private Task? finishing;
    private readonly QrGardenMotion motion=new();private readonly OrthographicCamera camera=new();private readonly Viewport3D viewport;
    private readonly ModelVisual3D visual=new();private readonly Slider yaw,elevation;
    private QrGardenPresentation? presentation;private Rect3D bounds;private bool closed,started,applying,listening;private long last;
    private bool Reduced=>!app.Prefs.AnimateMenus||!SystemParameters.ClientAreaAnimation;
    internal bool Animating=>listening;
    internal QrGardenDialog(App app,QrCode code,QrGardenTemplate template,QrGardenSeason season)
    {
        ArgumentNullException.ThrowIfNull(code);QrGarden.Validate(template,season);this.app=app;
        Title=app.L("QR Garden");Width=800;Height=810;MinWidth=450;MinHeight=380;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        bounds=new(-(code.Size+8)/2.0,0,-(code.Size+8)/2.0,code.Size+8,code.Size*.7,code.Size+8);
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();var status=UI.Text(app.L("Building the local 3D scene…"));footer.Children.Add(status);var row=new WrapPanel();footer.Children.Add(row);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var body=new StackPanel();body.Children.Add(UI.Text(app.L(QrGarden.TemplateName(template))+" · "+app.L(QrGarden.SeasonName(season)),20));body.Children.Add(UI.Text(app.L(Scope),12));
        viewport=new Viewport3D{Height=420,ClipToBounds=true,IsHitTestVisible=false,Camera=camera};AutomationProperties.SetName(viewport,app.L("QR Garden"));viewport.Children.Add(visual);
        body.Children.Add(new Border{Child=viewport,Background=SystemColors.ControlBrush,Margin=new Thickness(0,8,0,8)});
        yaw=new Slider{Minimum=0,Maximum=360,Value=45,TickFrequency=15};elevation=new Slider{Minimum=20,Maximum=90,Value=32,TickFrequency=5};
        body.Children.Add(UI.Text(app.L("Rotation")));AutomationProperties.SetName(yaw,app.L("Rotation"));body.Children.Add(yaw);body.Children.Add(UI.Text(app.L("Viewing Angle")));AutomationProperties.SetName(elevation,app.L("Viewing Angle"));body.Children.Add(elevation);
        void Manual(){if(applying||closed)return;StopFrames();motion.SetView(yaw.Value,elevation.Value);Apply();}
        yaw.ValueChanged+=(_,_)=>Manual();elevation.ValueChanged+=(_,_)=>Manual();viewport.SizeChanged+=(_,_)=>Apply();
        row.Children.Add(UI.Button(app.L("Top View"),()=>Turn(true)));row.Children.Add(UI.Button(app.L("Reset View"),()=>Turn(false)));
        row.Children.Add(UI.Button(app.L("Stop"),()=>{StopFrames();motion.Stop();Apply();cancellation.Cancel();}));var close=UI.Button(app.L("Close"),Close);close.IsCancel=true;row.Children.Add(close);
        root.Children.Add(UI.Scroll(body));Content=root;Apply();
        async Task Build()
        {
            try
            {
                var model=await Task.Run(()=>QrGardenMesh.Build(QrGarden.Build(code,template,season,cancellation.Token),cancellation.Token),cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if(!closed){bounds=model.Bounds;presentation=new(model);visual.Content=presentation.Root;Apply();status.Text=app.L("3D scene ready. Use the flat QR image for copying and PNG export.");}
            }
            catch(OperationCanceledException){if(!closed)status.Text=app.L("Cancelled.");}
            catch(Exception){if(!closed)status.Text=app.L("The 3D scene could not be prepared. The flat QR image is still available.");}
        }
        Loaded+=(_,_)=>{if(!closed&&!started){started=true;pending=Build();}};
        Closed+=(_,_)=>{StopFrames();motion.Stop();if(!closed){closed=true;cancellation.Cancel();}visual.Content=null;presentation=null;};
        IsVisibleChanged+=(_,_)=>{if(!IsVisible){StopFrames();motion.Stop();Apply();}};
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized){StopFrames();motion.Stop();Apply();}};
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private void Turn(bool top)
    {
        if(closed)return;motion.Turn(top,Reduced||!IsVisible||WindowState==WindowState.Minimized);Apply();if(!motion.Moving)StopFrames();
        if(motion.Moving&&!listening&&IsVisible&&WindowState!=WindowState.Minimized){listening=true;last=0;CompositionTarget.Rendering+=Frame;}
    }
    private void Frame(object? sender,EventArgs args)
    {long now=Stopwatch.GetTimestamp();double dt=last==0?0:(now-last)/(double)Stopwatch.Frequency;last=now;AdvanceTurn(dt);}
    internal void AdvanceTurn(double seconds)
    {if(closed)return;motion.Advance(seconds,Reduced);Apply();if(!motion.Moving)StopFrames();}
    private void StopFrames(){if(!listening)return;CompositionTarget.Rendering-=Frame;listening=false;last=0;}
    private void Apply()
    {
        applying=true;try{yaw.Value=motion.Yaw;elevation.Value=motion.Elevation;}finally{applying=false;}
        double a=motion.Yaw*Math.PI/180,e=motion.Elevation*Math.PI/180,sa=Math.Sin(a),ca=Math.Cos(a),se=Math.Sin(e),ce=Math.Cos(e);
        var target=new Point3D(bounds.X+bounds.SizeX/2,bounds.Y+bounds.SizeY/2,bounds.Z+bounds.SizeZ/2);
        double distance=4*Math.Max(bounds.SizeX,Math.Max(bounds.SizeY,bounds.SizeZ));camera.Position=target+new Vector3D(sa*ce*distance,se*distance,ca*ce*distance);camera.LookDirection=target-camera.Position;camera.UpDirection=new(-sa*se,ce,-ca*se);
        double spanR=Math.Abs(ca)*bounds.SizeX+Math.Abs(sa)*bounds.SizeZ,spanU=Math.Abs(sa*se)*bounds.SizeX+Math.Abs(ce)*bounds.SizeY+Math.Abs(ca*se)*bounds.SizeZ;
        camera.Width=Math.Max(spanR,spanU*Math.Max(1,viewport.ActualWidth)/Math.Max(1,viewport.ActualHeight))*1.08;
        presentation?.SetOpacity(motion.StructureOpacity);
    }
    internal void RequestClose(){if(!closed)Close();}
    internal Task FinishAsync()=>finishing??=FinishCoreAsync();
    private async Task FinishCoreAsync()
    {closed=true;StopFrames();motion.Stop();cancellation.Cancel();visual.Content=null;presentation=null;try{await pending;}finally{cancellation.Dispose();}}
}

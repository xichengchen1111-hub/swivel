using System;
using System.Collections.Generic;
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
    internal static Model3DGroup Build(QrGarden garden,CancellationToken token)
    {
        var meshes=new Dictionary<uint,MeshGeometry3D>();
        MeshGeometry3D Mesh(uint color){if(!meshes.TryGetValue(color,out var m))meshes.Add(color,m=new());return m;}
        void Face(uint color,Point3D a,Point3D b,Point3D c,Point3D d)
        {
            var m=Mesh(color);int start=m.Positions.Count;var normal=Vector3D.CrossProduct(b-a,c-a);normal.Normalize();
            foreach(var p in new[]{a,b,c,d}){m.Positions.Add(p);m.Normals.Add(normal);}
            foreach(int i in new[]{0,1,2,0,2,3})m.TriangleIndices.Add(start+i);
        }
        foreach(var box in garden.Boxes)
        {
            token.ThrowIfCancellationRequested();double x0=box.X-box.Width/2,x1=box.X+box.Width/2,z0=box.Z-box.Depth/2,z1=box.Z+box.Depth/2,y0=box.Bottom,y1=box.Top;
            Face(box.Color,new(x0,y1,z0),new(x0,y1,z1),new(x1,y1,z1),new(x1,y1,z0));
            Face(box.Side,new(x0,y0,z1),new(x0,y0,z0),new(x1,y0,z0),new(x1,y0,z1));
            Face(box.Side,new(x0,y0,z0),new(x0,y0,z1),new(x0,y1,z1),new(x0,y1,z0));
            Face(box.Side,new(x1,y0,z1),new(x1,y0,z0),new(x1,y1,z0),new(x1,y1,z1));
            Face(box.Side,new(x1,y0,z0),new(x0,y0,z0),new(x0,y1,z0),new(x1,y1,z0));
            Face(box.Side,new(x0,y0,z1),new(x1,y0,z1),new(x1,y1,z1),new(x0,y1,z1));
        }
        var group=new Model3DGroup();group.Children.Add(new AmbientLight(Colors.White));
        foreach(var pair in meshes)
        {
            token.ThrowIfCancellationRequested();var color=Color.FromRgb((byte)(pair.Key>>16),(byte)(pair.Key>>8),(byte)pair.Key);
            var model=new GeometryModel3D(pair.Value,new DiffuseMaterial(new SolidColorBrush(color)));model.Freeze();group.Children.Add(model);
        }
        group.Freeze();token.ThrowIfCancellationRequested();return group;
    }
}

internal sealed class QrGardenDialog:Window
{
    public const string Scope="Original Windows QR relief scenes: Tree, Bouquet and Island, with Spring, Summer and Autumn palettes. The dark module squares and four-module white border are retained in the top projection. Lighting, viewing angle, screen resolution and dense codes can prevent scanning. Use the existing flat QR image for sharing or printing; this scene is not a scan guarantee or a 3D model export.";
    private readonly CancellationTokenSource cancellation=new();private Task pending=Task.CompletedTask;private bool closed,started;
    internal QrGardenDialog(App app,QrCode code,QrGardenTemplate template,QrGardenSeason season)
    {
        QrGarden.Validate(template,season);Title=app.L("QR Garden");Width=800;Height=810;MinWidth=450;MinHeight=380;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();var status=UI.Text(app.L("Building the local 3D scene…"));footer.Children.Add(status);var row=new WrapPanel();footer.Children.Add(row);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var body=new StackPanel();body.Children.Add(UI.Text(app.L(QrGarden.TemplateName(template))+" · "+app.L(QrGarden.SeasonName(season)),20));body.Children.Add(UI.Text(app.L(Scope),12));
        var viewport=new Viewport3D{Height=420,ClipToBounds=true};AutomationProperties.SetName(viewport,app.L("QR Garden"));body.Children.Add(new Border{Child=viewport,Background=SystemColors.ControlBrush,Margin=new Thickness(0,8,0,8)});
        var camera=new OrthographicCamera();viewport.Camera=camera;var visual=new ModelVisual3D();viewport.Children.Add(visual);
        var yaw=new Slider{Minimum=0,Maximum=360,Value=0,TickFrequency=15};var elevation=new Slider{Minimum=20,Maximum=90,Value=40,TickFrequency=5};
        body.Children.Add(UI.Text(app.L("Rotation")));AutomationProperties.SetName(yaw,app.L("Rotation"));body.Children.Add(yaw);body.Children.Add(UI.Text(app.L("Viewing Angle")));AutomationProperties.SetName(elevation,app.L("Viewing Angle"));body.Children.Add(elevation);
        void Camera(){double a=yaw.Value*Math.PI/180,e=elevation.Value*Math.PI/180,d=(code.Size+8)*3;var target=new Point3D(0,0,0);camera.Position=new(Math.Sin(a)*Math.Cos(e)*d,Math.Sin(e)*d,Math.Cos(a)*Math.Cos(e)*d);camera.LookDirection=target-camera.Position;camera.UpDirection=new(-Math.Sin(a)*Math.Sin(e),Math.Cos(e),-Math.Cos(a)*Math.Sin(e));camera.Width=QrGarden.ViewWidth(code.Size,Math.Max(1,viewport.ActualWidth),Math.Max(1,viewport.ActualHeight));}
        yaw.ValueChanged+=(_,_)=>Camera();elevation.ValueChanged+=(_,_)=>Camera();Camera();
        viewport.SizeChanged+=(_,_)=>Camera();
        row.Children.Add(UI.Button(app.L("Top View"),()=>{yaw.Value=0;elevation.Value=90;}));row.Children.Add(UI.Button(app.L("Reset View"),()=>{yaw.Value=0;elevation.Value=40;}));row.Children.Add(UI.Button(app.L("Stop"),()=>cancellation.Cancel()));var close=UI.Button(app.L("Close"),Close);close.IsCancel=true;row.Children.Add(close);
        root.Children.Add(UI.Scroll(body));Content=root;
        async Task Build()
        {
            try{var model=await Task.Run(()=>QrGardenMesh.Build(QrGarden.Build(code,template,season,cancellation.Token),cancellation.Token),cancellation.Token);cancellation.Token.ThrowIfCancellationRequested();if(!closed){visual.Content=model;status.Text=app.L("3D scene ready. Use the flat QR image for copying and PNG export.");}}
            catch(OperationCanceledException){if(!closed)status.Text=app.L("Cancelled.");}
            catch(Exception){if(!closed)status.Text=app.L("The 3D scene could not be prepared. The flat QR image is still available.");}
        }
        Loaded+=(_,_)=>{if(!closed&&!started){started=true;pending=Build();}};Closed+=(_,_)=>{closed=true;cancellation.Cancel();visual.Content=null;};SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    internal void RequestClose(){if(!closed)Close();}
    internal async Task FinishAsync(){cancellation.Cancel();try{await pending;}finally{cancellation.Dispose();}}
}

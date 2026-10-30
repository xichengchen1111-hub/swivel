using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class QrGardenNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject root)where T:DependencyObject
    {if(root is T value)yield return value;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Find<T>(child))yield return item;}
    private static void Pump(Func<bool> done)
    {
        var frame=new DispatcherFrame();var clock=Stopwatch.StartNew();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};
        timer.Tick+=(_,_)=>{if(done()||clock.Elapsed>TimeSpan.FromSeconds(30))frame.Continue=false;};timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}if(!done())throw new TimeoutException("QR garden did not finish.");
    }
    private static void Finish(QrGardenDialog dialog)
    {var finish=dialog.FinishAsync();if(!finish.IsCompleted)Pump(()=>finish.IsCompleted);finish.GetAwaiter().GetResult();}
    private static byte[] Png(QrCode code)
    {using var output=new MemoryStream();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(QrTools.Render(code)));encoder.Save(output);return output.ToArray();}
    private static ModelVisual3D Visual(QrGardenDialog dialog)=>Find<Viewport3D>(dialog).Single().Children.OfType<ModelVisual3D>().Single();
    private static void Click(QrGardenDialog dialog,App app,string key)=>Find<Button>(dialog).Single(b=>Equals(b.Content,app.L(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static EmissiveMaterial Emission(GeometryModel3D model)=>((MaterialGroup)model.Material).Children.OfType<EmissiveMaterial>().Single();
    private static void CheckRenderPipeline(Action<bool,string> check)
    {
        // Independent XY quad: no garden geometry, top camera, fading or layout assumptions.
        foreach(bool emissive in new[]{false,true})
        {
            var color=Color.FromRgb(240,234,224);var brush=new SolidColorBrush(color);var material=new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(emissive?Brushes.Black:brush));if(emissive)material.Children.Add(new EmissiveMaterial(brush));
            var mesh=new MeshGeometry3D{Positions=new(){new(-1,-1,0),new(1,-1,0),new(1,1,0),new(-1,1,0)},TriangleIndices=new(){0,1,2,0,2,3}};
            var scene=new Model3DGroup();scene.Children.Add(new GeometryModel3D(mesh,material));if(!emissive)scene.Children.Add(new AmbientLight(Colors.White));
            var view=new Viewport3D{Width=32,Height=32,Camera=new OrthographicCamera(new(0,0,10),new(0,0,-1),new(0,1,0),4){NearPlaneDistance=1,FarPlaneDistance=20}};
            view.Children.Add(new ModelVisual3D{Content=scene});view.Measure(new(32,32));view.Arrange(new(0,0,32,32));view.UpdateLayout();
            var bitmap=new RenderTargetBitmap(32,32,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(16,16,1,1),pixel,4,0);
            string values=$"Independent WPF {(emissive?"unlit emissive":"ambient diffuse")} quad: BGRA=({pixel[0]},{pixel[1]},{pixel[2]},{pixel[3]}), expected=(224,234,240,255); tier={RenderCapability.Tier>>16}";
            Console.WriteLine(values);
            check(pixel[3]==255&&Math.Abs(pixel[0]-224)<=1&&Math.Abs(pixel[1]-234)<=1&&Math.Abs(pixel[2]-240)<=1,values);
        }
    }
    private static void CheckTopRender(Model3DGroup scene,QrCode code,string label,Action<bool,string> check)
    {
        int extent=code.Size+8,side=extent*8;var top=new Model3DGroup();top.Children.Add(scene.Children[0]);top.Children.Add(scene.Children[1]);
        var view=new Viewport3D{Width=side,Height=side,Camera=new OrthographicCamera(new Point3D(0,extent*3,0),new Vector3D(0,-1,0),new Vector3D(0,0,-1),extent)};
        view.Children.Add(new ModelVisual3D{Content=top});view.Measure(new Size(side,side));view.Arrange(new Rect(0,0,side,side));view.UpdateLayout();
        var bitmap=new RenderTargetBitmap(side,side,96,96,PixelFormats.Pbgra32);bitmap.Render(view);byte[] pixels=new byte[side*side*4];bitmap.CopyPixels(pixels,side*4,0);
        int failed=0,nonopaque=0,alphaMin=255,alphaMax=0;string first="none";
        var ranges=new Dictionary<string,(int Count,double Min,double Max)>{["dark"]=(0,1,0),["light"]=(0,1,0),["quiet"]=(0,1,0)};
        for(int r=-4;r<code.Size+4;r++)for(int c=-4;c<code.Size+4;c++)
        {
            int i=(((r+4)*8+4)*side+(c+4)*8+4)*4;double luma=(.2126*pixels[i+2]+.7152*pixels[i+1]+.0722*pixels[i])/255;
            bool inside=c>=0&&r>=0&&c<code.Size&&r<code.Size,dark=inside&&code[c,r];
            string kind=dark?"dark":inside?"light":"quiet";var range=ranges[kind];ranges[kind]=(range.Count+1,Math.Min(range.Min,luma),Math.Max(range.Max,luma));
            int alpha=pixels[i+3];alphaMin=Math.Min(alphaMin,alpha);alphaMax=Math.Max(alphaMax,alpha);if(alpha!=255)nonopaque++;
            if(alpha!=255||!(dark?luma<.60:luma>.82))
            {
                if(failed++==0)first=$"module({c},{r}) pixel({(c+4)*8+4},{(r+4)*8+4}) {kind} BGRA=({pixels[i]},{pixels[i+1]},{pixels[i+2]},{alpha}) luma={luma:F6}";
            }
        }
        int painted=Enumerable.Range(0,side*side).Count(i=>pixels[i*4+3]!=0);
        string stats=string.Join("; ",ranges.Select(x=>$"{x.Key}[n={x.Value.Count},luma={x.Value.Min:F6}..{x.Value.Max:F6}]"));
        var dpi=VisualTreeHelper.GetDpi(view);
        check(failed==0,$"Native emissive top render has source module contrast and pale quiet zone without lights: {label}; failed={failed}/{extent*extent}; nonopaque={nonopaque}; alpha={alphaMin}..{alphaMax}; painted={painted}/{side*side}; {stats}; first={first}; viewport={view.ActualWidth}x{view.ActualHeight} DIP, dpi={dpi.PixelsPerInchX}x{dpi.PixelsPerInchY}, target={side}x{side}@96; bounds={top.Bounds}");
    }
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var prior=SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            CheckRenderPipeline(check);
            foreach(var template in Enum.GetValues<QrGardenTemplate>())foreach(var season in Enum.GetValues<QrGardenSeason>())
            {
                var code=QrCode.Encode("Native garden");byte[] png=Png(code);var source=QrGarden.Build(code,template,season);var scene=QrGardenMesh.Build(source,default);
                check(scene.IsFrozen&&scene.Children.Count==3&&scene.Children.All(x=>x is Model3DGroup),"Three frozen native layers without any runtime lights");
                var layers=new[]{source.Ground,source.Cover,source.Structure};
                for(int layer=0;layer<3;layer++)
                {
                    var tops=new Dictionary<(uint Color,double X0,double X1,double Z0,double Z1,double Top),int>();
                    foreach(var b in layers[layer])
                    {var key=(b.Color,b.X-b.Width/2,b.X+b.Width/2,b.Z-b.Depth/2,b.Z+b.Depth/2,b.Top);tops.TryGetValue(key,out int count);tops[key]=count+1;}
                    int points=0,indices=0;
                    foreach(GeometryModel3D model in ((Model3DGroup)scene.Children[layer]).Children)
                    {
                        var mesh=(MeshGeometry3D)model.Geometry;points+=mesh.Positions.Count;indices+=mesh.TriangleIndices.Count;
                        check(model.Material is MaterialGroup material&&material.Children.Count==2&&material.Children[0] is DiffuseMaterial black&&((SolidColorBrush)black.Brush).Color==Colors.Black&&material.Children[1] is EmissiveMaterial&&model.BackMaterial is null,"Black opacity carrier plus flat emission only; no camera-dependent colour contribution");
                        var brush=(SolidColorBrush)Emission(model).Brush;uint rgb=(uint)(brush.Color.R<<16|brush.Color.G<<8|brush.Color.B);
                        check(model.IsFrozen&&mesh.IsFrozen&&mesh.Positions.Count<=QrGardenMesh.MaxMeshPositions&&mesh.Normals.Count==mesh.Positions.Count&&mesh.TriangleIndices.All(i=>i>=0&&i<mesh.Positions.Count),"Batched frozen mesh indices, normals and per-mesh size bound");
                        for(int i=0;i<mesh.Positions.Count;i+=4)
                        {
                            var a=mesh.Positions[i];var b=mesh.Positions[i+1];var c=mesh.Positions[i+2];var d=mesh.Positions[i+3];
                            var normal=mesh.Normals[i];check(Math.Abs(normal.Length-1)<1e-12&&Vector3D.DotProduct(Vector3D.CrossProduct(b-a,c-a),normal)>0,"Baked quad winding agrees with outward normal");
                            if(normal.Y!=1)continue;
                            var key=(rgb,a.X,d.X,a.Z,b.Z,a.Y);check(tops.TryGetValue(key,out int count)&&count>0,"Every native top quad keeps the exact core RGB and rectangle");tops[key]=count-1;
                        }
                    }
                    check(points==layers[layer].Count*20&&indices==layers[layer].Count*30&&tops.Values.All(x=>x==0),"Exactly five quads per voxel and no missing/recoloured tops");
                }
                CheckTopRender(scene,code,$"{template}/{season}",check);
                var live=new QrGardenPresentation(scene);var structure=(Model3DGroup)live.Root.Children[2];live.SetOpacity(.4);
                check(structure.Children.OfType<GeometryModel3D>().All(x=>x.Geometry.IsFrozen&&Math.Abs(Emission(x).Brush.Opacity-.4)<1e-12&&Math.Abs(((DiffuseMaterial)((MaterialGroup)x.Material).Children[0]).Brush.Opacity-.4)<1e-12),"Structure fades together without copying geometry");
                check(ReferenceEquals(live.Root.Children[0],scene.Children[0])&&ReferenceEquals(live.Root.Children[1],scene.Children[1]),"Opaque code surfaces stay frozen and untouched during fade");
                live.SetOpacity(0);check(live.Root.Children.Count==2,"Fully faded structure removed from depth rendering");live.SetOpacity(1);check(live.Root.Children.Count==3&&scene.IsFrozen,"Reversing fade restores structure without changing frozen scene");
                reject(()=>live.SetOpacity(double.NaN),"Nonfinite structure opacity refuses");reject(()=>live.SetOpacity(1.1),"Out-of-range structure opacity refuses");
                check(Png(code).SequenceEqual(png),"Garden generation and fades keep flat PNG bytes identical");
            }
            reject(()=>QrGardenMesh.Build(QrGarden.Build(QrCode.Encode("cancel"),QrGardenTemplate.Tree,QrGardenSeason.Summer),new CancellationToken(true)),"Native mesh precancel");
            var large=QrGarden.Build(QrCode.Encode(new string('x',QrCode.MaxBytes)),QrGardenTemplate.Bouquet,QrGardenSeason.Autumn);
            var largeWatch=Stopwatch.StartNew();var largeMesh=QrGardenMesh.Build(large,default);largeWatch.Stop();
            check(largeMesh.Children.Cast<Model3DGroup>().SelectMany(x=>x.Children).Cast<GeometryModel3D>().All(x=>((MeshGeometry3D)x.Geometry).Positions.Count<=QrGardenMesh.MaxMeshPositions),"Largest QR mesh respects chunk limit too");
            Console.WriteLine($"QR garden v40 native mesh: {large.FaceCount} quads, {largeWatch.Elapsed.TotalMilliseconds:F1} ms (this Windows host only)");
            using(var cancel=new CancellationTokenSource())
            {
                // Frozen core data crosses threads; cancellation during WPF allocation must
                // finish with no partial scene delivered. Accept completion if it beats cancellation.
                var task=Task.Run(()=>QrGardenMesh.Build(large,cancel.Token));cancel.Cancel();
                try{check(task.GetAwaiter().GetResult().IsFrozen,"A completed cancellation race only delivers a complete frozen scene");}catch(OperationCanceledException){check(true,"Native mesh cancellation joined");}
            }
            var app=Application.Current as App??throw new InvalidOperationException("Validation host required.");if(!app.IsValidationHost)throw new InvalidOperationException("Do not pump a product App.");
            bool oldAnimate=app.Prefs.AnimateMenus;
            var dialog=new QrGardenDialog(app,QrCode.Encode("Ready"),QrGardenTemplate.Tree,QrGardenSeason.Spring){ShowInTaskbar=false};
            try
            {
                app.Prefs.SetMenuPresentation(app.Prefs.MenuTemplate,true);dialog.Show();var viewport=Find<Viewport3D>(dialog).Single();var visual=Visual(dialog);Pump(()=>visual.Content is not null);
                check(((Model3DGroup)visual.Content!).Children.Take(2).All(x=>x.IsFrozen),"Frozen opaque layers delivered to dispatcher");
                var root=(DockPanel)dialog.Content;check(root.Children.OfType<ScrollViewer>().Count()==1&&root.Children.OfType<StackPanel>().Any(x=>DockPanel.GetDock(x)==Dock.Bottom),"Native garden fixed footer and scroll body");
                Click(dialog,app,"Top View");
                if(SystemParameters.ClientAreaAnimation)
                {
                    check(dialog.Animating,"Turn attaches frame driver");dialog.AdvanceTurn(.2);
                    var sliders=Find<Slider>(dialog).ToArray();check(sliders[1].Value is >32 and <90,"Turn has an intermediate native camera pose");
                    Click(dialog,app,"Reset View");check(dialog.Animating,"Rapid reverse retains one frame subscription");dialog.AdvanceTurn(3);
                    Click(dialog,app,"Top View");dialog.AdvanceTurn(3);
                }
                check(!dialog.Animating&&((Model3DGroup)visual.Content!).Children.Count==2&&Math.Abs(((OrthographicCamera)viewport.Camera).LookDirection.X)<1e-10&&((OrthographicCamera)viewport.Camera).UpDirection.Z<-.99,"Top view is north-up, structure absent and driver unhooked");
                app.Prefs.SetMenuPresentation(app.Prefs.MenuTemplate,false);Click(dialog,app,"Reset View");check(!dialog.Animating&&Find<Slider>(dialog).Last().Value==32&&((Model3DGroup)visual.Content!).Children.Count==3,"Reduced motion instantly restores scene and structure");
                Click(dialog,app,"Top View");check(!dialog.Animating&&Find<Slider>(dialog).Last().Value==90,"Reduced motion instantly reaches code");
                // Bounds-based orthographic framing must include all eight corners at every angle/aspect.
                var sceneBounds=visual.Content!.Bounds;
                foreach(double width in new[]{460.0,800.0,1800.0})foreach(double angle in new[]{20.0,32.0,60.0,90.0})foreach(double yaw in new[]{0.0,45.0,135.0,270.0})
                {
                    dialog.Width=width;dialog.UpdateLayout();var sliders=Find<Slider>(dialog).ToArray();sliders[0].Value=yaw;sliders[1].Value=angle;
                    var camera=(OrthographicCamera)viewport.Camera;var forward=camera.LookDirection;forward.Normalize();var up=camera.UpDirection;up.Normalize();var right=Vector3D.CrossProduct(forward,up);right.Normalize();
                    var target=new Point3D(sceneBounds.X+sceneBounds.SizeX/2,sceneBounds.Y+sceneBounds.SizeY/2,sceneBounds.Z+sceneBounds.SizeZ/2);
                    foreach(int x in new[]{0,1})foreach(int y in new[]{0,1})foreach(int z in new[]{0,1})
                    {
                        var offset=new Point3D(sceneBounds.X+sceneBounds.SizeX*x,sceneBounds.Y+sceneBounds.SizeY*y,sceneBounds.Z+sceneBounds.SizeZ*z)-target;
                        check(Math.Abs(Vector3D.DotProduct(offset,right))<camera.Width/2&&Math.Abs(Vector3D.DotProduct(offset,up))<camera.Width/2*viewport.ActualHeight/viewport.ActualWidth,"Scene bounds fit at arbitrary yaw/elevation/window aspect");
                    }
                }
                app.Prefs.SetMenuPresentation(app.Prefs.MenuTemplate,true);Click(dialog,app,"Reset View");
                check(!SystemParameters.ClientAreaAnimation||dialog.Animating,"Closing fixture really has an active turn when system animation is enabled");dialog.Close();Finish(dialog);check(visual.Content is null&&!dialog.Animating,"Closing active animation unhooks rendering and drops geometry");
                check(ReferenceEquals(dialog.FinishAsync(),dialog.FinishAsync()),"Finish is idempotent after cancellation source disposal");
            }
            finally{dialog.RequestClose();Finish(dialog);app.Prefs.SetMenuPresentation(app.Prefs.MenuTemplate,oldAnimate);}
            var late=new QrGardenDialog(app,QrCode.Encode(new string('x',QrCode.MaxBytes)),QrGardenTemplate.Bouquet,QrGardenSeason.Autumn);
            late.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));late.Close();Finish(late);check(Visual(late).Content is null&&!late.Animating,"Close during largest generation joins without late delivery");
            var stopped=new QrGardenDialog(app,QrCode.Encode(new string('x',QrCode.MaxBytes)),QrGardenTemplate.Island,QrGardenSeason.Summer);
            stopped.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));Click(stopped,app,"Stop");Finish(stopped);check(Visual(stopped).Content is null&&!stopped.Animating,"Stop joins generation without late publication");stopped.Close();
            check(app.Home is null&&app.Awake is null,"Native garden fixture does not start tray/shortcuts/settings services");
        }
        finally{SynchronizationContext.SetSynchronizationContext(prior);}
    }
}

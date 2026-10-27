using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var prior=SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            foreach(var template in Enum.GetValues<QrGardenTemplate>())foreach(var season in Enum.GetValues<QrGardenSeason>())
            {
                var source=QrGarden.Build(QrCode.Encode("Native garden"),template,season);var scene=QrGardenMesh.Build(source,default);check(scene.IsFrozen,"Native garden freezes complete cross-thread scene");
                int points=0,indices=0;foreach(var model in scene.Children.OfType<GeometryModel3D>())
                {
                    var mesh=(MeshGeometry3D)model.Geometry;points+=mesh.Positions.Count;indices+=mesh.TriangleIndices.Count;
                    check(model.IsFrozen&&mesh.IsFrozen&&mesh.Normals.Count==mesh.Positions.Count&&mesh.TriangleIndices.All(i=>i>=0&&i<mesh.Positions.Count),"Native garden mesh has valid frozen indices/normals");
                    check(mesh.Normals.All(n=>Math.Abs(n.Length-1)<1e-12),"Native garden face normals normalized");
                }
                check(points==source.Boxes.Count*24&&indices==source.Boxes.Count*36,"Native garden exactly six quads per bounded cuboid");
            }
            reject(()=>QrGardenMesh.Build(QrGarden.Build(QrCode.Encode("cancel"),QrGardenTemplate.Tree,QrGardenSeason.Summer),new CancellationToken(true)),"Native mesh precancel");
            var app=Application.Current as App??throw new InvalidOperationException("Validation host required.");if(!app.IsValidationHost)throw new InvalidOperationException("Do not pump a product App.");
            var dialog=new QrGardenDialog(app,QrCode.Encode("Ready"),QrGardenTemplate.Tree,QrGardenSeason.Spring);var viewport=Find<Viewport3D>(dialog).Single();var visual=viewport.Children.OfType<ModelVisual3D>().Single();
            dialog.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));Pump(()=>visual.Content is not null);check(visual.Content!.IsFrozen,"Native prepared scene delivered frozen to dispatcher");
            var root=(DockPanel)dialog.Content;check(root.Children.OfType<ScrollViewer>().Count()==1&&root.Children.OfType<StackPanel>().Any(x=>DockPanel.GetDock(x)==Dock.Bottom),"Native garden fixed footer and scroll body");
            Find<Button>(dialog).Single(b=>Equals(b.Content,app.L("Top View"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(Math.Abs(((OrthographicCamera)viewport.Camera).LookDirection.X)<1e-10&&((OrthographicCamera)viewport.Camera).UpDirection.Z<-.99,"Native top view oriented QR north up");
            dialog.Close();var finish=dialog.FinishAsync();if(!finish.IsCompleted)Pump(()=>finish.IsCompleted);finish.GetAwaiter().GetResult();check(visual.Content is null,"Native close drops scene content");
            var late=new QrGardenDialog(app,QrCode.Encode(new string('x',QrCode.MaxBytes)),QrGardenTemplate.Bouquet,QrGardenSeason.Autumn);late.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));late.Close();finish=late.FinishAsync();if(!finish.IsCompleted)Pump(()=>finish.IsCompleted);finish.GetAwaiter().GetResult();check(Find<Viewport3D>(late).Single().Children.OfType<ModelVisual3D>().Single().Content is null,"Native close joins largest garden without late delivery");
            check(app.Home is null&&app.Awake is null,"Native garden fixture does not start tray/shortcuts/settings services");
        }
        finally{SynchronizationContext.SetSynchronizationContext(prior);}
    }
}

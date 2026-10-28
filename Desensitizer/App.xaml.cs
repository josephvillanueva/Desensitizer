using System;
using System.Windows;

namespace Desensitizer
{
 public partial class App : Application
 {
  private RuntimeCrashGuard? crashGuard;

  protected override void OnStartup(StartupEventArgs e)
  {
   // Workers (llama-server, Python analyzers) join one job object that Windows
   // closes if this process dies, and workers left by an earlier crash are ended.
   // Failures here must never block startup.
   try{RuntimeJob.Shared.IsActive.ToString();}catch{}
   try{RuntimeOrphanSweep.Sweep(AppContext.BaseDirectory);}catch{}
   crashGuard=new RuntimeCrashGuard(
    message=>MessageBox.Show(message,"Unexpected problem",MessageBoxButton.OK,MessageBoxImage.Error),
    code=>Environment.Exit(code));
   DispatcherUnhandledException+=(sender,args)=>{args.Handled=true;crashGuard.HandleFatal(args.Exception);};
   AppDomain.CurrentDomain.UnhandledException+=(sender,args)=>crashGuard.HandleFatal(args.ExceptionObject as Exception);
   crashGuard.RegisterFlush(UiCrashFlush.Run);
   // An unobserved task fault is observed and dropped: it is not fatal and nothing is logged (UI-5).
   System.Threading.Tasks.TaskScheduler.UnobservedTaskException+=(sender,args)=>crashGuard.HandleUnobserved(args.SetObserved);
   TextScaling.SetPercent(AppSettingsStore.Load().TextScalePercent);
   EventManager.RegisterClassHandler(typeof(FrameworkElement),FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,args)=>TextScaling.ApplyElement(sender as DependencyObject)));
   base.OnStartup(e);
  }

  protected override void OnExit(ExitEventArgs e)
  {
   try{ResourceUsage.StopAllWorkers();}catch{}
   // Closing the job handle ends anything still in it.
   try{RuntimeJob.Shared.Dispose();}catch{}
   base.OnExit(e);
  }
 }
}

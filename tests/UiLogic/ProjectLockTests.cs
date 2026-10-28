using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Desensitizer;

static class ProjectLockTests
{
    private delegate bool WindowCallback(IntPtr handle,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId,WindowCallback callback,IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle,StringBuilder title,int length);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle,uint message,IntPtr wParam,IntPtr lParam);
    private static object? Call(MainWindow window,string method,params object?[] args)=>typeof(MainWindow).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,args);
    private static object? Field(MainWindow window,string field)=>typeof(MainWindow).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window);
    private static void Set(MainWindow window,string field,object? value)=>typeof(MainWindow).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,value);

    public static void RunWindow(MainWindow window,Action<string,bool,string?> check,string work)
    {
        const string pass="Violet-Harbor-Lantern-42";
        var path=Path.Combine(work,"window-lock.dsproj");
        var project=Project.New("Fictional window lock",pass);ProjectStore.Save(path,project);
        void Open(string password,bool cancel=false)
        {
            var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(30) };
            timer.Tick+=(_,_)=>
            {
                foreach(var dialog in Application.Current.Windows.Cast<Window>().ToArray())
                {
                    if(dialog is PasswordDialog)
                    {
                        if(cancel){dialog.DialogResult=false;continue;}
                        var body=(StackPanel)dialog.Content;
                        body.Children.OfType<PasswordBox>().Single().Password=password;
                        body.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if(dialog is IssueDialog)dialog.DialogResult=false;
                }
            };
            timer.Start();
            try{Call(window,"OpenProjectFile",path,null);window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);}
            finally{timer.Stop();}
        }
        Open(pass);
        check("project lock window: successful open retains project ownership",Field(window,"_project")!=null&&Field(window,"_projectLock")!=null,null);
        using(var blocked=UiProjectLock.TryAcquire(path,out _))check("project lock window: an opened project blocks a second window",blocked==null,null);
        Call(window,"BackToProjects_Click",window,new RoutedEventArgs());
        using(var back=UiProjectLock.TryAcquire(path,out _))check("project lock window: Back clears the session and releases ownership",Field(window,"_project")==null&&back!=null,null);
        Open(pass,true);
        using(var canceled=UiProjectLock.TryAcquire(path,out _))check("project lock window: canceled unlock releases provisional ownership",Field(window,"_project")==null&&canceled!=null,null);
        Open("incorrect-fictional-passphrase");
        using(var failed=UiProjectLock.TryAcquire(path,out _))check("project lock window: failed unlock clears session and releases ownership",Field(window,"_project")==null&&failed!=null,null);
        var catalog=ProjectCatalogStorage.CatalogPath(ProjectCatalog.Root);
        var backup=catalog+".fixture";File.Move(catalog,backup);Directory.CreateDirectory(catalog);
        try
        {
            Open(pass);
            using var failed=UiProjectLock.TryAcquire(path,out _);
            check("project lock window: failed catalog setup clears partial session and releases ownership",Field(window,"_project")==null&&Field(window,"_activeEntry")==null&&failed!=null,null);
        }
        finally{Directory.Delete(catalog);File.Move(backup,catalog);}
        Open(pass);
        var older=(Project)Field(window,"_project")!;
        older.LoadedFormat=ProjectFormat.Dsp1;
        var upgradeSeen=false;var unsavedSeen=false;
        var threadId=GetCurrentThreadId();
        var replyTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(30) };
        replyTimer.Tick+=(_,_)=>EnumThreadWindows(threadId,(handle,_)=>
        {
            var title=new StringBuilder(128);GetWindowText(handle,title,title.Capacity);
            if(title.ToString()=="Upgrade project format"){upgradeSeen=true;PostMessage(handle,0x111,(IntPtr)2,IntPtr.Zero);}
            if(title.ToString()=="Changes not saved"){unsavedSeen=true;PostMessage(handle,0x111,(IntPtr)7,IntPtr.Zero);}
            return true;
        },IntPtr.Zero);
        replyTimer.Start();
        try
        {
            Call(window,"SaveProjectNow",false);
            check("project save: declining upgrade without edits does not invent unsaved changes",upgradeSeen&&!(bool)Field(window,"_declinedChanges")!,null);
            ((UiUpgradeGate)Field(window,"_upgradeGate")!).Reset();
            upgradeSeen=false;Call(window,"RequestSave");
            Call(window,"BackToProjects_Click",window,new RoutedEventArgs());
            check("project save: first dirty upgrade decline requires leave confirmation",upgradeSeen&&unsavedSeen,null);
            using var blocked=UiProjectLock.TryAcquire(path,out _);
            check("project save: refusing leave after upgrade Cancel preserves project and ownership",Field(window,"_project")!=null&&(bool)Field(window,"_declinedChanges")!&&blocked==null,null);
        }
        finally{replyTimer.Stop();older.LoadedFormat=ProjectFormat.Dsp2;((UiUpgradeGate)Field(window,"_upgradeGate")!).Reset();}
        var blockedSavePath=Path.Combine(work,"blocked-save.dsproj");Directory.CreateDirectory(blockedSavePath);
        Set(window,"_path",blockedSavePath);Call(window,"RequestSave");
        Call(window,"BackToProjects_Click",window,new RoutedEventArgs());
        using(var blocked=UiProjectLock.TryAcquire(path,out _))check("project lock window: failed save keeps the project and lock open",Field(window,"_project")!=null&&blocked==null,null);
        Set(window,"_path",path);Call(window,"BackToProjects_Click",window,new RoutedEventArgs());
        using(var released=UiProjectLock.TryAcquire(path,out _))check("project lock window: corrected save permits leaving safely",Field(window,"_project")==null&&released!=null,null);
        var closing=new MainWindow { ShowInTaskbar=false,WindowState=WindowState.Minimized };
        Set(closing,"_project",project);Set(closing,"_path",path);Set(closing,"_projectLock",UiProjectLock.TryAcquire(path,out _));
        closing.Show();closing.Close();
        using(var released=UiProjectLock.TryAcquire(path,out _))check("project lock window: closing the window releases ownership",released!=null,null);
    }

    public static void Run(CheckFn check,string work)
    {
        var folder=Path.Combine(work,"project-lock");Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"fictional.dsproj");
        var project=Project.New("Fictional lock project","Violet-Harbor-Lantern-42");
        ProjectStore.Save(path,project);
        UiProjectLock? owner=null;
        try
        {
            owner=UiProjectLock.TryAcquire(path,out var empty);
            check("project lock: first owner acquires an unlocked project",owner!=null&&empty=="");
            using var other=UiProjectLock.TryAcquire(path,out var holder);
            check("project lock: second window is refused",other==null);
            using(var stream=new FileStream(UiProjectLock.LockPathFor(path),FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
            using(var reader=new StreamReader(stream,Encoding.ASCII))
                check("project lock: sidecar stores only a process identifier",holder==Environment.ProcessId.ToString()&&reader.ReadToEnd()==holder);
            using var canonical=UiProjectLock.TryAcquire(Path.Combine(folder,".","fictional.dsproj"),out _);
            check("project lock: normalized path cannot bypass ownership",canonical==null);
            using var caseVariant=UiProjectLock.TryAcquire(path.ToUpperInvariant(),out _);
            check("project lock: Windows path casing cannot bypass ownership",caseVariant==null);
            using var independent=UiProjectLock.TryAcquire(Path.Combine(folder,"other.dsproj"),out _);
            check("project lock: another project remains usable",independent!=null);
            var info=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true };
            if(Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            info.ArgumentList.Add("--lock-probe");info.ArgumentList.Add(path);
            using var child=Process.Start(info)!;
            var stopped=child.WaitForExit(15000);
            if(!stopped)child.Kill(true);
            check("project lock: another process is refused",stopped&&child.ExitCode==37);
            ProjectStore.Save(path,project);
            using var duringSave=UiProjectLock.TryAcquire(path,out _);
            check("project lock: atomic project saves do not release ownership",duringSave==null&&ProjectStore.Load(path,project.Passphrase).Name==project.Name);
        }
        finally { owner?.Dispose(); }
        owner?.Dispose();
        using(var reopened=UiProjectLock.TryAcquire(path,out _))check("project lock: release and repeated dispose permit reopening",reopened!=null);
        File.WriteAllText(UiProjectLock.LockPathFor(path),"99999999",Encoding.ASCII);
        using(var stale=UiProjectLock.TryAcquire(path,out _))check("project lock: a crash-left sidecar never blocks reopening",stale!=null);
        try
        {
            using var failed=UiProjectLock.TryAcquire(path,out _);
            ProjectStore.Load(path,"incorrect-fictional-passphrase");
            check("project lock: wrong passphrase was refused",false);
        }
        catch(InvalidDataException) { check("project lock: wrong passphrase was refused",true); }
        using(var retry=UiProjectLock.TryAcquire(path,out _))check("project lock: failed unlock releases the provisional lock",retry!=null);
        var missing=Path.Combine(folder,"missing-folder","project.dsproj");
        var storageFailed=false;
        try { using var inaccessible=UiProjectLock.TryAcquire(missing,out _); }
        catch(DirectoryNotFoundException) { storageFailed=true; }
        check("project lock: missing storage is not reported as already open",storageFailed);
        var directoryPath=Path.Combine(folder,"directory.dsproj");Directory.CreateDirectory(UiProjectLock.LockPathFor(directoryPath));
        var accessFailed=false;
        try { using var inaccessible=UiProjectLock.TryAcquire(directoryPath,out _); }
        catch(UnauthorizedAccessException) { accessFailed=true; }
        check("project lock: inaccessible sidecar is a storage error",accessFailed);
        check("project lock: nonsharing IO errors are not lock conflicts",!UiProjectLock.IsLockConflict(new IOException("fictional storage failure")));
        check("project lock: conflict recovery explains both apps and forbids deleting the lock",UiIssues.For(IssueKind.ProjectAlreadyOpen).NextStep.Contains("AI Desensitizer")&&UiIssues.For(IssueKind.ProjectAlreadyOpen).NextStep.Contains("Do not delete"));
        check("project lock: storage recovery does not invent another owner",UiIssues.For(IssueKind.ProjectAccessFailed).Explanation.Contains("not proof"));
        check("project lock: spotlight teaches one window per project",TutorialGuide.Steps[0].Description.Contains("Only one window"));
    }
}

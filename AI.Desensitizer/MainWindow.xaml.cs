using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows.Input;

namespace Desensitizer
{
 public partial class MainWindow : Window
 {
  private Project? _project; private string? _path; private ProjectEntry? _activeEntry; private bool _isBusy; private CancellationTokenSource? _analysisCancellation; private AppSettings _settings=AppSettingsStore.Load(); private string _mappingFilter="Review"; private ICollectionView? _mappingView; private readonly ObservableCollection<FileRecord> _files = new ObservableCollection<FileRecord>(); private readonly ObservableCollection<ProjectEntry> _projects = new ObservableCollection<ProjectEntry>();
  private string _analysisWorkflow=""; private string[] _analysisStages=Array.Empty<string>(); private int _analysisStage=-1;
  private readonly DispatcherTimer _resourceTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};
  private readonly UiUpgradeGate _upgradeGate=new UiUpgradeGate();
  // The places where the two apps differ. Everything else about scanning and exporting is shared (Shared/UiMainWindow.cs).
  private static readonly string[] ExpectedReviewers=new[]{"Qwen","NuExtract","Gemma"};
  private static AnalysisProgress FinalAiProgress()=>new AnalysisProgress{Step=1,Total=1,Name="Final AI consensus review",Detail="Checking the protected copy with three distinct local AI reviewers."};
  private static LocalReviewResult FinalAiReview(string text,Action<AnalysisProgress> progress,CancellationToken token,ExportReviewCache cache,List<Candidate> detectorFindings,TableReviewContext? tables)=>LocalAiReview.FindDetailed(text,progress,token,cache:cache,detectorFindings:detectorFindings,tableContext:tables);
  private static string DescribeLeakSource(Candidate x)=>x.Source.StartsWith("AI",StringComparison.OrdinalIgnoreCase)?"local AI finding":Math.Round(x.Confidence*100)+"%";
  public MainWindow() { InitializeComponent(); FooterVersion.Text=ProductVersion.Label; RefreshResourceUsage();_resourceTimer.Tick+=(_,_)=>RefreshResourceUsage();_resourceTimer.Start();Closed+=(_,_)=>{_resourceTimer.Stop();ProjectScope.Unbind();}; FilesList.ItemsSource = _files; ProjectsList.ItemsSource=_projects; LoadProjectList(); ApplySettings(); StartupHousekeeping(); UpdateUiState(); InitializeTutorial(); }
  private void LoadProjectList()
  {
   foreach(var item in ProjectCatalog.Load())_projects.Add(item);
   // A project file in the managed folder that lost its catalog row (damaged catalog) is listed again with a neutral label.
   try{var recovered=UiProjectSession.RecoverUncataloged(_projects.ToList());if(recovered.Count>0){foreach(var item in recovered)_projects.Add(item);SaveCatalog();}}catch(Exception){}
  }
  private void RefreshResourceUsage(){try{FooterMemory.Text=ResourceUsage.Label();}catch{FooterMemory.Text="Memory usage unavailable";}}
  private void ApplySettings(){AutoDeepReviewToggle.IsChecked=_settings.AutomaticDeepReview;AutoDeepReviewToggle.Content=_settings.AutomaticDeepReview?"On":"Off";MappingsGrid.RowHeight=_settings.CompactTable?32:40;TextScaling.SetPercent(_settings.TextScalePercent);Dispatcher.BeginInvoke(()=>TextScaling.ApplyTree(this),DispatcherPriority.Loaded);}
  private void AutoDeepReview_Click(object s,RoutedEventArgs e){_settings.AutomaticDeepReview=AutoDeepReviewToggle.IsChecked==true;AutoDeepReviewToggle.Content=_settings.AutomaticDeepReview?"On":"Off";AppSettingsStore.Save(_settings);StatusText.Text=_settings.AutomaticDeepReview?"Automatic local deep check enabled.":"Automatic deep check disabled. Export validation remains required.";UpdateUiState();}
  private void Settings_Click(object s,RoutedEventArgs e)
  {
   Action<string,string>? changePassphrase=_project!=null&&_path!=null?(current,next)=>
   {
    if(_upgradeGate.Decide(_project.LoadedFormat,true)==UiSaveAction.AskUpgrade)
    {
     if(!ConfirmUpgrade()){_upgradeGate.Decline();throw new InvalidOperationException("The passphrase was not changed because the project upgrade was not accepted.");}
     _upgradeGate.Approve();
    }
    lock(_saveGate){var sequence=++_saveSequence;ProjectStore.ChangePassphrase(_path,_project,current,next);_lastWrittenSequence=sequence;}
    _project.LoadedFormat=ProjectFormat.Dsp2;
    StatusText.Text="Project passphrase changed and encrypted mapping saved.";
   }:null;
   var dialog=new SettingsDialog(_settings,changePassphrase,_project?.AutoScanOnAdd,_project?.Name,ReviewerCount){Owner=this};
   var saved=dialog.ShowDialog();
   if(dialog.TutorialRequested){StartTutorial();return;}
   if(saved!=true)return;
   _settings=dialog.Result;AppSettingsStore.Save(_settings);if(_project!=null&&dialog.AutoScanOnAddResult.HasValue){_project.AutoScanOnAdd=dialog.AutoScanOnAddResult.Value;SaveCurrentProjectSilently();}ApplySettings();StatusText.Text="Settings saved locally.";
  }
  private void About_Click(object s,RoutedEventArgs e)=>new AboutDialog(()=>SupportCollector.CollectText(_project,Title,ReviewerCount)){Owner=this}.ShowDialog();
  private bool Ready() { if (_project != null) return true; MessageBox.Show("Create or open a project first.", "Desensitizer"); return false; }
  private void NewProject_Click(object s, RoutedEventArgs e)
  {
   if(Busy||_project!=null)return;
   var dialog = new ProjectDialog("Create project",_projects.Select(p=>p.Label)) { Owner=this };
   if (dialog.ShowDialog() != true) return;
   UiProjectLock? pending=null;ProjectEntry? addedEntry=null;
   try
   {
    var created=Project.New(dialog.ProjectName, dialog.Passphrase);
    var path=ProjectCatalog.NewProjectPath();
    pending=UiProjectLock.TryAcquire(path,out _);
    if(pending==null){QueueIssue(UiIssues.For(IssueKind.ProjectAlreadyOpen));return;}
    ProjectStore.Save(path,created);
    created.LoadedFormat=ProjectFormat.Dsp2;
    var entry=new ProjectEntry{FilePath=path,Label=dialog.ListLabel};
    UiProjectSession.Adopt(entry,created);
    _project=created;_path=path;_activeEntry=entry;_upgradeGate.Reset();
    _projects.Insert(0,entry);addedEntry=entry;SaveCatalog();
    _projectLock=pending;pending=null;
    _files.Clear();_mappingFilter=_settings.OpenOnReview?"Review":"All";StatusText.Text="Encrypted project created.";RefreshMappings();UpdateUiState();
   }
   catch(Exception ex){if(addedEntry!=null)_projects.Remove(addedEntry);LeaveProject();UpdateUiState();QueueIssue(UiIssues.Failure(IssueKind.ProjectCreateFailed,ex));}
   finally{pending?.Dispose();}
  }
  private void OpenProject_Click(object s, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Desensitizer projects (*.dsproj)|*.dsproj" }; if (d.ShowDialog() != true) return; OpenProjectFile(d.FileName,null); }
  private void SaveProject_Click(object s, RoutedEventArgs e) { if (!Ready()) return; if (_path == null) { var d = new SaveFileDialog { Filter="Desensitizer projects (*.dsproj)|*.dsproj", FileName=_project!.Name + ".dsproj" }; if(d.ShowDialog()!=true)return; _path=d.FileName; } if(SaveProjectNow(true)){StatusText.Text="Encrypted project saved.";UpdateUiState();} }
  private void OpenProjectFile(string filePath,ProjectEntry? entry)
  {
   if(Busy||_project!=null)return;
   entry??=_projects.FirstOrDefault(x=>string.Equals(x.FilePath,filePath,StringComparison.OrdinalIgnoreCase));
   UiProjectLock? pending=null;ProjectEntry? addedEntry=null;
   try
   {
    try{pending=UiProjectLock.TryAcquire(filePath,out _);}
    catch(IOException){QueueIssue(UiIssues.For(IssueKind.ProjectAccessFailed));return;}
    catch(UnauthorizedAccessException){QueueIssue(UiIssues.For(IssueKind.ProjectAccessFailed));return;}
    if(pending==null){QueueIssue(UiIssues.For(IssueKind.ProjectAlreadyOpen));return;}
    var p = PasswordDialog.Ask("Unlock project", "Enter the passphrase for "+(entry?.Name??"this project")+":"); if (p == null) return;
    var known=entry!=null;entry??=new ProjectEntry{FilePath=filePath};
    _project=UiProjectSession.Open(entry,filePath,p);
    _path=filePath;_activeEntry=entry;_upgradeGate.Reset();
    if(!known){_projects.Insert(0,entry);addedEntry=entry;}
    SaveCatalog();_files.Clear();foreach(var f in _project.Files)_files.Add(f);_mappingFilter=_settings.OpenOnReview?"Review":"All";
    _projectLock=pending;pending=null;
    AfterProjectOpened();
    RefreshMappings();StatusText.Text="Project unlocked.";UpdateUiState();
   }
   catch(Exception ex){if(addedEntry!=null)_projects.Remove(addedEntry);LeaveProject();UpdateUiState();QueueIssue(UiIssues.Failure(IssueKind.ProjectOpenFailed,ex));}
   finally{pending?.Dispose();}
  }
  private void OpenSelectedProject_Click(object s,RoutedEventArgs e){if(ProjectsList.SelectedItem is ProjectEntry entry)OpenProjectFile(entry.FilePath,entry);else MessageBox.Show("Select a project to open.","Desensitizer");}
  private void ProjectsList_MouseDoubleClick(object s,System.Windows.Input.MouseButtonEventArgs e){if(ProjectsList.SelectedItem is ProjectEntry entry)OpenProjectFile(entry.FilePath,entry);}
  private void ProjectsList_SelectionChanged(object s,SelectionChangedEventArgs e){var selected=ProjectsList.SelectedItem!=null;RenameProjectButton.IsEnabled=selected;OpenSelectedProjectButton.IsEnabled=selected;}
  private void ProjectMenu_Click(object s,RoutedEventArgs e)
  {
   if(Busy||s is not Button button||button.DataContext is not ProjectEntry project)return;
   ProjectsList.SelectedItem=project;
   if(button.ContextMenu!=null){button.ContextMenu.PlacementTarget=button;button.ContextMenu.IsOpen=true;}
   e.Handled=true;
  }
  private void BackToProjects_Click(object s,RoutedEventArgs e){if(Busy||!SaveBeforeLeaving())return;LeaveProject();StatusText.Text="Select a project to continue.";UpdateUiState();}
  private void RenameProject_Click(object s,RoutedEventArgs e)
  {
   if(Busy||_project!=null)return;
   if(ProjectsList.SelectedItem is not ProjectEntry entry){MessageBox.Show("Select a project to rename.","Desensitizer");return;}
   if(!TryLockProjectForEdit(entry.FilePath,out var projectLock))return;
   using(projectLock)
   {
    var pass=PasswordDialog.Ask("Unlock to rename","Enter the passphrase for "+entry.Name+":");if(pass==null)return;
    try
    {
     var project=ProjectStore.Load(entry.FilePath,pass);
     var d=new TextPromptDialog("Rename project","Project name",project.Name){Owner=this};if(d.ShowDialog()!=true)return;
     if(project.LoadedFormat==ProjectFormat.Dsp1&&!ConfirmUpgrade())return;
     project.Name=d.Value;ProjectStore.Save(entry.FilePath,project);
     entry.LastOpened=DateTimeOffset.UtcNow;ProjectCatalog.HideName(entry);ProjectsList.Items.Refresh();SaveCatalog();
     StatusText.Text="Project renamed. The new name is stored inside the encrypted project.";
    }
    catch(Exception ex){QueueIssue(UiIssues.Failure(IssueKind.ProjectOpenFailed,ex));}
   }
  }
  private void DeleteProject_Click(object s,RoutedEventArgs e)
  {
   if(Busy||_project!=null)return;
   if(ProjectsList.SelectedItem is not ProjectEntry entry){MessageBox.Show("Select a project to delete.","Desensitizer");return;}
   if(!TryLockProjectForEdit(entry.FilePath,out var projectLock))return;
   using(projectLock)
   {
    if(MessageBox.Show("Delete ‘"+entry.Name+"’ and its encrypted mapping file? This cannot be undone.","Delete project",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
    try{UiProjectSession.DeleteFiles(entry.FilePath);_projects.Remove(entry);SaveCatalog();StatusText.Text="Project deleted.";UpdateUiState();}
    catch(Exception){QueueIssue(UiIssues.For(IssueKind.ProjectAccessFailed));}
   }
  }
  private void SaveCatalog()=>ProjectCatalog.Save(_projects);
  private void SaveCurrentProjectSilently(){SaveProjectNow(false);}
  private async void AddFiles_Click(object s, RoutedEventArgs e) { if(!Ready())return; var d=new OpenFileDialog { Filter=SupportedFormats.OpenFilter(), Multiselect=true }; if(d.ShowDialog()==true)await AddProjectFilesAsync(d.FileNames); }
  private async Task AddProjectFilesAsync(IEnumerable<string> paths)
  {
   if(_project==null||Busy)return;
   var added=new List<FileRecord>();var unsupported=0;var skipped=new List<string>();
   foreach(var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
   {
    if(!File.Exists(path)||!SupportedFormats.IsSupported(path)){unsupported++;skipped.Add(path);continue;}
    if(_project.Files.Any(file=>string.Equals(file.SourcePath,path,StringComparison.OrdinalIgnoreCase)))continue;
    var record=new FileRecord{SourcePath=path,Display=Path.GetFileName(path),AddedAt=DateTimeOffset.UtcNow,Status="Ready to scan"};
    _project.Files.Add(record);_files.Add(record);added.Add(record);
   }
   if(_files.Count>0&&FilesList.SelectedItem==null)FilesList.SelectedIndex=0;
   if(added.Count>0)SaveCurrentProjectSilently();
   StatusText.Text=added.Count>0?added.Count+" file"+(added.Count==1?"":"s")+" added. "+(_project.AutoScanOnAdd?"Scanning locally now.":"Scan a document to review it locally."):"No new supported files were added.";
   UpdateUiState();
   if(unsupported>0)QueueIssue(UiIssues.For(IssueKind.UnsupportedFile));
   if(_project.AutoScanOnAdd)foreach(var file in added){FilesList.SelectedItem=file;if(!await ScanFileAsync(file))break;}
  }
  private void ProjectFiles_DragOver(object sender,DragEventArgs e)
  {
   var files=e.Data.GetDataPresent(DataFormats.FileDrop)?e.Data.GetData(DataFormats.FileDrop) as string[]:null;
   var valid=_project!=null&&!Busy&&files?.Any(path=>File.Exists(path)&&SupportedFormats.IsSupported(path))==true;
   e.Effects=valid?DragDropEffects.Copy:DragDropEffects.None;FileDropOverlay.Visibility=valid?Visibility.Visible:Visibility.Collapsed;e.Handled=true;
  }
  private void ProjectFiles_DragLeave(object sender,DragEventArgs e){FileDropOverlay.Visibility=Visibility.Collapsed;}
  private async void ProjectFiles_Drop(object sender,DragEventArgs e)
  {
   FileDropOverlay.Visibility=Visibility.Collapsed;
   if(_project==null||Busy)return;
   if(e.Data.GetDataPresent(DataFormats.FileDrop)&&e.Data.GetData(DataFormats.FileDrop) is string[] files)await AddProjectFilesAsync(files);
   e.Handled=true;
  }
  private FileRecord? Selected => FilesList.SelectedItem as FileRecord;
  private void FilesList_SelectionChanged(object s, SelectionChangedEventArgs e) { RefreshMappings(); UpdateUiState(); }
  private void FileMenu_Click(object s,RoutedEventArgs e)
  {
   if(Busy||!(s is Button button)||!(button.DataContext is FileRecord file))return;
   FilesList.SelectedItem=file;
   if(button.ContextMenu!=null){button.ContextMenu.PlacementTarget=button;button.ContextMenu.IsOpen=true;}
   e.Handled=true;
  }
  private void RemoveFileMenu_Click(object s,RoutedEventArgs e)
  {
   if(!Ready()||!(s is MenuItem item))return;
   var menu=ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu;
   if(!(menu?.PlacementTarget is FrameworkElement target)||!(target.DataContext is FileRecord file))return;
   var consequence=_project!.Files.Count==1?"This is the last file, so all project mappings will also be cleared.":"Mappings found only in this file will also be removed.";
   if(MessageBox.Show("Remove ‘"+file.Display+"’ from this project?\n\n"+consequence+"\nThe original document will remain untouched on your computer.","Remove from project",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
   _project.RemoveFile(file);_files.Remove(file);SaveCurrentProjectSilently();
   StatusText.Text=_files.Count==0?"File removed and all mappings cleared. The original document was not changed.":"File and its exclusive mappings removed. The original document was not changed.";RefreshMappings();UpdateUiState();
  }
  private async void Scan_Click(object s, RoutedEventArgs e)
  {
   if(!Ready() || Selected==null)return;
   await ScanFileAsync(Selected);
  }
  private void RefreshMappings()
  {
   MappingsGrid.ItemsSource=null;_mappingView=null;
   if(_mappingFilter=="Review"&&_project?.Mappings.Any(m=>!m.Approved&&!m.Ignored)!=true)_mappingFilter="All";
   if(_project!=null)
   {
    var view=new ListCollectionView(_project.Mappings);
    view.Filter=item=>item is Mapping mapping&&MappingMatchesFilter(mapping);
    view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Mapping.Category)));
    view.SortDescriptions.Add(new SortDescription(nameof(Mapping.Category),ListSortDirection.Ascending));
    view.SortDescriptions.Add(new SortDescription(nameof(Mapping.Confidence),ListSortDirection.Ascending));
    _mappingView=view;MappingsGrid.ItemsSource=view;
   }
   UpdateUiState();ResetMappingHorizontalScroll();Dispatcher.BeginInvoke(()=>TextScaling.ApplyTree(MappingsGrid),DispatcherPriority.Loaded);
  }
  private void MergeLiveFindings(FileRecord file,IReadOnlyList<Candidate> candidates)
  {
   if(_project==null||candidates.Count==0)return;
   foreach(var candidate in candidates.Where(PresidioDetector.IsEligibleCandidate))_project.EnsureMapping(candidate,file.Id);
   // Rebuilding the table for every small batch made the window stutter, so it is rebuilt a couple of times a second.
   ScheduleMappingRefresh();
  }
  private void ResetAutomaticFindingsForRescan(FileRecord file)
  {
   ResetAutomaticFindings(file,true);
  }
  private void ResetAutomaticFindingsForDeepCheck(FileRecord file)
  {
   ResetAutomaticFindings(file,false);
  }
  private void ResetAutomaticFindings(FileRecord file,bool clearInitialScanHistory)
  {
   if(_project==null)return;
   // The file's mappings and decisions stay until the new results are complete (UI-3): a Stop or a crash keeps them.
   _project.BeginRescan(file.Id);
   if(clearInitialScanHistory)file.LastScanAt=null;
   file.LastDeepReviewAt=null;file.DeepReviewComplete=false;file.DeepReviewWarning="";
   _mappingFilter="All";
   RefreshMappings();
  }
  private bool MappingMatchesFilter(Mapping mapping)=>_mappingFilter=="All"||(_mappingFilter=="Replace"&&mapping.Approved)||(_mappingFilter=="Keep"&&mapping.Ignored)||(_mappingFilter=="Review"&&!mapping.Approved&&!mapping.Ignored)||(_mappingFilter=="Verify"&&mapping.NeedsVerification);
  private void MappingFilter_Click(object s,RoutedEventArgs e){if(!(s is Button button)||!(button.Tag is string filter))return;_mappingFilter=_mappingFilter==filter?"All":filter;MappingsGrid.SelectedItems.Clear();_mappingView?.Refresh();UpdateUiState();ResetMappingHorizontalScroll();}
  private void ResetMappingHorizontalScroll()
  {
   Dispatcher.BeginInvoke(new Action(()=>
   {
    MappingsGrid.UpdateLayout();
    FindMappingScrollViewer(MappingsGrid)?.ScrollToLeftEnd();
   }),System.Windows.Threading.DispatcherPriority.ContextIdle);
  }
  private static ScrollViewer? FindMappingScrollViewer(DependencyObject root)
  {
   for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
   {
    var child=VisualTreeHelper.GetChild(root,index);
    if(child is ScrollViewer viewer)return viewer;
    var nested=FindMappingScrollViewer(child);if(nested!=null)return nested;
   }
   return null;
  }
  private void MappingSelectionChanged(object s,SelectionChangedEventArgs e)=>UpdateUiState();
  private List<Mapping> BulkScope()
  {
   var selected=MappingsGrid.SelectedItems.Cast<object>().OfType<Mapping>().ToList();
   if(selected.Count>1)return selected;
   return _mappingView==null?new List<Mapping>():_mappingView.Cast<object>().OfType<Mapping>().ToList();
  }
  private void ApplyBulkDecision(string decision,bool remember=false)
  {
   var scope=BulkScope();if(scope.Count==0)return;
   foreach(var mapping in scope){if(decision!="Review")mapping.AcknowledgeContextReview();mapping.Decision=decision;}
   var text=scope.Count+" mapping"+(scope.Count==1?"":"s")+" set to "+decision+".";
   if(remember)text=UiKeepRemember.Describe(UiKeepRemember.Apply(scope,ReviewAllowList.Current),text);
   MappingsGrid.SelectedItems.Clear();_mappingView?.Refresh();RequestSave();UpdateUiState();ResetMappingHorizontalScroll();
   StatusText.Text=text;
  }
  private void BulkReplace_Click(object s,RoutedEventArgs e)=>ApplyBulkDecision("Replace");
  private void BulkReview_Click(object s,RoutedEventArgs e)=>ApplyBulkDecision("Review");
  private void BulkKeep_Click(object s,RoutedEventArgs e)=>ApplyBulkDecision("Keep as-is");
  private void BulkRemember_Click(object s,RoutedEventArgs e)=>ApplyBulkDecision("Keep as-is",true);
  private void AddMapping_Click(object s, RoutedEventArgs e) { if(!Ready()||Selected==null)return; var d=new MappingDialog { Owner=this }; if(d.ShowDialog()!=true)return; _project!.EnsureMapping(d.Value, d.Category, true,1,"Manual","Added by analyst",Selected.Id,true); RefreshMappings(); RequestSave(); StatusText.Text="Mapping added for this document and ready to reuse across the project."; }
  private void SelectAllMappings_Click(object s,RoutedEventArgs e)
  {
   if(!Ready()||_project!.Mappings.Count==0)return;
   foreach(var mapping in _project.Mappings.Where(m=>!m.Ignored)){mapping.AcknowledgeContextReview();mapping.Approved=true;}
   MappingsGrid.Items.Refresh();RequestSave();UpdateUiState();ResetMappingHorizontalScroll();
   StatusText.Text="All undecided mappings set to Replace. Existing Keep as-is decisions were preserved.";
  }
  private void ClearAllMappings_Click(object s,RoutedEventArgs e)
  {
   if(!Ready()||_project!.Mappings.Count==0)return;
   foreach(var mapping in _project.Mappings)mapping.Approved=false;
   MappingsGrid.Items.Refresh();RequestSave();UpdateUiState();ResetMappingHorizontalScroll();
   StatusText.Text="Replacement selections cleared. Existing Keep as-is decisions were preserved.";
  }
  private void BeginAnalysisProgress(string name,string detail,string workflow)
  {
   _analysisCancellation=new CancellationTokenSource();StopAnalysisButton.IsEnabled=true;StopAnalysisButton.Content="Stop";
   _analysisWorkflow=workflow;
   _analysisStages=workflow switch
   {
    "scan"=>_settings.AutomaticDeepReview?new[]{"Reading document","Local detector scan","Section cross-check","AI deep review","Review findings"}:new[]{"Reading document","Local detector scan","Review findings"},
    "deep"=>new[]{"Section cross-check","AI deep review","Review findings"},
    "export"=>_settings.AutomaticDeepReview?new[]{"Create protected copy","Final detector scan","Final AI safety review","Release copy"}:new[]{"Create protected copy","Final detector scan","Release copy"},
    _=>new[]{name}
   };
   _analysisStage=-1;
   _isBusy=true;UpdateUiState();
   StartProgressTracking();
   AnalysisOverlay.Visibility=Visibility.Visible;SetAnalysisStage(0,detail,false);StopAnalysisButton.Focus();
   ((Storyboard)Resources["LoadingStoryboard"]).Begin(this,true);
  }
  private bool IsStopTarget(object source)
  {
   var element=source as DependencyObject;
   while(element!=null)
   {
    if(ReferenceEquals(element,StopAnalysisButton))return true;
    element=element is System.Windows.Media.Visual||element is System.Windows.Media.Media3D.Visual3D
     ?VisualTreeHelper.GetParent(element)
     :element is FrameworkContentElement content?content.Parent:null;
   }
   return false;
  }
  private void Window_PreviewTouchDown(object sender,TouchEventArgs e){if(_isBusy&&!IsStopTarget(e.OriginalSource))e.Handled=true;}
  private void Window_PreviewStylusDown(object sender,StylusDownEventArgs e){if(_isBusy&&!IsStopTarget(e.OriginalSource))e.Handled=true;}
  private void Window_PreviewDragOver(object sender,DragEventArgs e){if(_isBusy){e.Effects=DragDropEffects.None;e.Handled=true;}}
  private void Window_PreviewDrop(object sender,DragEventArgs e){if(_isBusy)e.Handled=true;}
  private void Window_Closing(object? sender,CancelEventArgs e)
  {
   if(!Busy){if(!SaveBeforeLeaving())e.Cancel=true;return;}
   e.Cancel=true;
   MessageBox.Show(this,_isBusy?"Use Stop to end the current check before closing the app.":"A step is still running. Wait a moment, then close the app.","Analysis in progress",MessageBoxButton.OK,MessageBoxImage.Information);
  }
  private void ShowAnalysisProgress(AnalysisProgress progress)
  {
   if(!_isBusy||_analysisCancellation?.IsCancellationRequested==true)return;
   ReportTracker(progress);
   var stage=_analysisWorkflow switch
   {
    "scan"=>progress.Name.StartsWith("AI reviewer",StringComparison.OrdinalIgnoreCase)?3:progress.Name=="Deep check"?2:1,
    "deep"=>progress.Name.StartsWith("AI reviewer",StringComparison.OrdinalIgnoreCase)?1:0,
    "export"=>progress.Name.StartsWith("AI reviewer",StringComparison.OrdinalIgnoreCase)||progress.Name=="Final AI consensus review"?2:1,
    _=>0
   };
   SetAnalysisStage(Math.Min(stage,_analysisStages.Length-1),progress.Detail);
   var fraction=progress.Total>0?Math.Clamp((double)progress.Step/progress.Total,0,1):0;
   AnalysisProgressBar.Value=Math.Min(AnalysisProgressBar.Maximum,_analysisStage+fraction*0.9);
   UpdateProgressPercent();
   StatusText.Text=_analysisStages[_analysisStage]+": "+progress.Detail;
  }
  private void SetAnalysisStage(int index,string detail,bool animate=true)
  {
   if(_analysisStages.Length==0)return;
   index=Math.Clamp(index,0,_analysisStages.Length-1);
   var changed=index!=_analysisStage;
   _analysisStage=index;
   AnalysisStepBadge.Text="STEP "+(index+1)+" OF "+_analysisStages.Length;
   AnalysisStepName.Text=_analysisStages[index];AnalysisStepDetail.Text=detail;
   AnalysisPreviousCard.Visibility=index>0?Visibility.Visible:Visibility.Collapsed;
   if(index>0)AnalysisPreviousName.Text=_analysisStages[index-1];
   AnalysisNextCard.Visibility=index<_analysisStages.Length-1?Visibility.Visible:Visibility.Collapsed;
   if(index<_analysisStages.Length-1)AnalysisNextName.Text=_analysisStages[index+1];
   AnalysisProgressBar.Maximum=_analysisStages.Length;
   if(changed)AnalysisProgressBar.Value=index;
   UpdateProgressPercent();
   System.Windows.Automation.AutomationProperties.SetName(AnalysisCurrentCard,"Current step: "+_analysisStages[index]+". "+detail);
   if(changed&&animate&&SystemParameters.ClientAreaAnimation)
   {
    AnalysisCurrentCard.BeginAnimation(OpacityProperty,new DoubleAnimation(0.55,1,TimeSpan.FromMilliseconds(280)));
    if(AnalysisCurrentCard.RenderTransform is TranslateTransform move)move.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(18,0,TimeSpan.FromMilliseconds(280)));
    if(index>0)AnalysisPreviousCard.BeginAnimation(OpacityProperty,new DoubleAnimation(0.4,1,TimeSpan.FromMilliseconds(280)));
    if(index<_analysisStages.Length-1)AnalysisNextCard.BeginAnimation(OpacityProperty,new DoubleAnimation(0.4,1,TimeSpan.FromMilliseconds(280)));
   }
  }
  private void UpdateProgressPercent()
  {
   var percent=AnalysisProgressBar.Maximum>0?(int)Math.Round(100*AnalysisProgressBar.Value/AnalysisProgressBar.Maximum):0;
   AnalysisProgressPercent.Text=percent+"%";
   System.Windows.Automation.AutomationProperties.SetName(AnalysisProgressBar,"Workflow progress, approximately "+percent+" percent");
  }
  private void EndAnalysisProgress()
  {
   StopProgressTracking();((Storyboard)Resources["LoadingStoryboard"]).Stop(this);AnalysisOverlay.Visibility=Visibility.Collapsed;_isBusy=false;_analysisCancellation?.Dispose();_analysisCancellation=null;_analysisWorkflow="";_analysisStages=Array.Empty<string>();_analysisStage=-1;UpdateUiState();
  }
  private void StopAnalysis_Click(object s,RoutedEventArgs e)
  {
   if(_analysisCancellation==null||_analysisCancellation.IsCancellationRequested)return;
   if(MessageBox.Show("Stop this local analysis? The current check will end, and no new desensitized file will be exported. Findings from a completed earlier scan may remain in this project.","Stop analysis",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
   // The check may have ended while the box was open: read the field again instead of using the earlier value (UI-5).
   var cancellation=_analysisCancellation;
   if(cancellation==null||!_isBusy)return;
   StopAnalysisButton.IsEnabled=false;StopAnalysisButton.Content="Stopping…";AnalysisStepDetail.Text="Stopping the local check and cleaning up temporary work…";
   try{cancellation.Cancel();}catch(ObjectDisposedException){}
  }

  private static bool IsResidualLeak(Candidate candidate)
  {
   if(string.IsNullOrWhiteSpace(candidate.Value)||System.Text.RegularExpressions.Regex.IsMatch(candidate.Value,@"^\[?[A-Z_]+_\d{3,}\]?$"))return false;
   if(candidate.Source.StartsWith("AI",StringComparison.OrdinalIgnoreCase))return LocalAiReview.IsHighRiskResidualCandidate(candidate);
   var threshold=candidate.Source=="GLiNER"?0.58:candidate.Source=="Privacy Filter"&&candidate.Category=="DATE"?0.92:candidate.Source=="Privacy Filter"?0.55:0.65;
   return candidate.Confidence>=threshold;
  }
  private void ShowExportComplete(string filePath,string description)
  {
   var dialog=new ExportCompleteDialog(filePath,description){Owner=this};
   dialog.ShowDialog();
   try
   {
    if(dialog.Action==ExportOpenAction.OpenFile)Process.Start(new ProcessStartInfo(filePath){UseShellExecute=true});
    else if(dialog.Action==ExportOpenAction.OpenFolder)Process.Start(new ProcessStartInfo(Path.GetDirectoryName(filePath)!){UseShellExecute=true});
   }
   catch(Exception ex){MessageBox.Show(this,"The file was saved, but Windows could not open it. "+ex.Message,"Could not open file",MessageBoxButton.OK,MessageBoxImage.Warning);}
  }
  private string? ChooseOutput(string prefix,string sourcePath) { var extension=SupportedFormats.OutputExtension(sourcePath); var d=new SaveFileDialog { Filter=SupportedFormats.SaveFilter(extension), FileName=prefix+"-document"+extension}; return d.ShowDialog()==true?d.FileName:null; }
  private static bool SamePath(string first,string second)=>string.Equals(Path.GetFullPath(first),Path.GetFullPath(second),StringComparison.OrdinalIgnoreCase);
  private void MappingsGrid_CurrentCellChanged(object s, EventArgs e) { UpdateUiState(); RequestSave(); }
  private void DecisionMenu_Click(object s,RoutedEventArgs e)
  {
   if(!(s is Button button)||button.ContextMenu==null)return;
   button.ContextMenu.PlacementTarget=button;button.ContextMenu.IsOpen=true;e.Handled=true;
  }
  private void DecisionMenuItem_Click(object s,RoutedEventArgs e)
  {
   if(!(s is MenuItem item)||!(item.Tag is string decision)||!(item.Parent is ContextMenu menu)||!(menu.PlacementTarget is Button button)||!(button.DataContext is Mapping mapping)||_project==null)return;
   var remember=decision.EndsWith("|remember",StringComparison.Ordinal);
   if(remember)decision=decision.Substring(0,decision.IndexOf('|'));
   if(!remember&&mapping.Decision==decision&&!mapping.RequiresContextReview)return;
   if(decision!="Review")mapping.AcknowledgeContextReview();
   mapping.Decision=decision;
   var rememberText=remember?UiKeepRemember.Describe(UiKeepRemember.Apply(new[]{mapping},ReviewAllowList.Current),"Mapping kept as-is."):null;
   Dispatcher.BeginInvoke(new Action(()=>
   {
    if(_project==null)return;
    _mappingView?.Refresh();UpdateUiState();ResetMappingHorizontalScroll();RequestSave();
    StatusText.Text=rememberText??"Mapping decision saved. Replace creates a token; Keep as-is leaves the value unchanged.";
   }),System.Windows.Threading.DispatcherPriority.Background);
  }
  private void UpdateUiState()
  {
   if(!IsInitialized)return;
   var hasProject=_project!=null;var hasFile=Selected!=null;var hasScannedFile=HasScannedFile(Selected);var mappingCount=_project?.Mappings.Count??0;var approved=_project?.Mappings.Count(m=>m.Approved)??0;var exportable=_project==null?0:UiExportScope.ApprovedCount(_project.Mappings);var kept=_project?.Mappings.Count(m=>m.Ignored)??0;var review=mappingCount-approved-kept;
   if(review==0&&_mappingFilter=="Review"){_mappingFilter="All";_mappingView?.Refresh();}
   var verify=_project?.Mappings.Count(m=>m.NeedsVerification)??0;var visible=_mappingView?.Cast<object>().Count()??0;var selected=MappingsGrid.SelectedItems.Count;var bulkCount=selected>1?selected:visible;var bulkActive=mappingCount>0&&(_mappingFilter!="All"||selected>1);
   HomeView.Visibility=hasProject?Visibility.Collapsed:Visibility.Visible;ProjectBanner.Visibility=hasProject?Visibility.Visible:Visibility.Collapsed;ProjectBanner.IsEnabled=!Busy;WorkspaceView.Visibility=hasProject?Visibility.Visible:Visibility.Collapsed;BackButton.Visibility=hasProject?Visibility.Visible:Visibility.Collapsed;BackButton.IsEnabled=hasProject&&!Busy;SettingsButton.IsEnabled=!Busy;AboutButton.IsEnabled=!Busy;AutoDeepReviewHeader.Visibility=hasProject?Visibility.Visible:Visibility.Collapsed;AutoDeepReviewToggle.IsEnabled=hasProject&&!Busy;AddFilesButton.IsEnabled=hasProject&&!Busy;AddMappingButton.IsEnabled=hasFile&&!Busy;FilesList.IsEnabled=!Busy;MappingsGrid.IsEnabled=!Busy;ProjectsList.IsEnabled=!Busy;ScanButton.IsEnabled=hasFile&&!Busy;DeepCheckButton.Visibility=hasScannedFile?Visibility.Visible:Visibility.Collapsed;DeepCheckButton.IsEnabled=hasScannedFile&&!Busy;RehydrateButton.IsEnabled=hasProject&&!Busy&&approved>0;
   var exportHelp=!hasFile?"Select a document to export.":exportable==0?"Set at least one mapping to Replace. Keep as-is leaves sensitive values unchanged.":_settings.AutomaticDeepReview&&Selected!.DeepReviewComplete!=true?"Run the deep check before exporting this document.":"Ready to export. Every copy receives a final local safety scan."+(_settings.AutomaticDeepReview?"":" The final AI review is off, so only the local detectors check the copy. You will be asked before each export, or turn on Automatic deep check for the strongest check.");
   DesensitizeButton.IsEnabled=hasFile&&!Busy&&exportable>0&&(!_settings.AutomaticDeepReview||Selected!.DeepReviewComplete);
   ExportGuidanceButton.IsEnabled=!Busy;
   ExportHelpText.Text=exportHelp;DesensitizeButton.ToolTip=exportHelp;
   FileEmptyState.Visibility=_files.Count==0?Visibility.Visible:Visibility.Collapsed;MappingEmptyState.Visibility=mappingCount==0?Visibility.Visible:Visibility.Collapsed;MappingFilterEmptyState.Visibility=mappingCount>0&&visible==0?Visibility.Visible:Visibility.Collapsed;ProjectsEmptyState.Visibility=_projects.Count==0?Visibility.Visible:Visibility.Collapsed;BulkActionBar.Visibility=bulkActive?Visibility.Visible:Visibility.Collapsed;
   FileCountLabel.Text=_files.Count==1?"1 document":_files.Count+" documents";ReplaceFilterButton.Content="✓  "+approved+" Replace";KeepFilterButton.Content="Keep: "+kept;ReplaceFilterButton.IsEnabled=KeepFilterButton.IsEnabled=VerifyFilterButton.IsEnabled=!Busy;ReviewFilterButton.Content=(review>0?"⚠  ":"")+review+" Review";ReviewFilterButton.IsEnabled=review>0&&!Busy;VerifyFilterButton.Content="◒  "+verify+" Verify";BulkScopeText.Text=selected>1?selected+" selected rows":visible+" filtered row"+(visible==1?"":"s");BulkReplaceButton.IsEnabled=BulkReviewButton.IsEnabled=BulkKeepButton.IsEnabled=BulkRememberButton.IsEnabled=bulkCount>0&&!Busy;ReviewListsButton.IsEnabled=hasProject&&!Busy;
   StyleFilterChip(ReplaceFilterButton,_mappingFilter=="Replace",Color.FromRgb(234,242,255),Color.FromRgb(23,78,166));StyleFilterChip(KeepFilterButton,_mappingFilter=="Keep",Color.FromRgb(243,244,246),Color.FromRgb(75,85,99));StyleFilterChip(ReviewFilterButton,_mappingFilter=="Review",Color.FromRgb(255,247,232),Color.FromRgb(138,75,8));StyleFilterChip(VerifyFilterButton,_mappingFilter=="Verify",Color.FromRgb(255,247,232),Color.FromRgb(138,75,8));
   ProjectLabel.Text=hasProject?_project!.Name:"No project open";ProjectPathLabel.Text=!hasProject?"Create or open an encrypted project to start":"Encrypted local project";ProjectDot.Background=new SolidColorBrush(hasProject?Color.FromRgb(34,197,94):Color.FromRgb(148,163,184));
   UpdateNoticePanel();
  }
  private static bool HasScannedFile(FileRecord? file)=>file!=null&&(file.LastScanAt.HasValue||file.DeepReviewComplete||file.Status.StartsWith("Analyzed locally",StringComparison.Ordinal)||file.Status.StartsWith("Deep check",StringComparison.Ordinal)||file.Status.StartsWith("Export",StringComparison.Ordinal)||file.Status.StartsWith("Scan incomplete",StringComparison.Ordinal));
  private static void StyleFilterChip(Button button,bool selected,Color selectedBackground,Color selectedForeground){button.Background=new SolidColorBrush(selected?selectedBackground:Colors.White);button.Foreground=new SolidColorBrush(selected?selectedForeground:Color.FromRgb(83,97,118));button.BorderBrush=new SolidColorBrush(selected?selectedForeground:Color.FromRgb(205,213,225));button.BorderThickness=new Thickness(selected?2:1);}
 }
}

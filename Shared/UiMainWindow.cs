using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Desensitizer
{
 // The parts of the main window that are the same in both apps. The rest of the window is in each app's
 // MainWindow.xaml.cs, which also holds the few places where the apps differ (how their AI reviewers report
 // progress and how the final AI review is started). Decisions live in Shared/Ui*.cs classes with tests in
 // tests/UiLogic. Nothing in here may weaken the export checks: a check that did not finish blocks the export.
 public partial class MainWindow
 {
  // ---------------------------------------------------------------- state used only by this file
  private UiProgressTracker? _tracker;
  private readonly DispatcherTimer _progressTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
  private UiCheckpointRecorder? _recorder;
  private string? _recorderKey;
  private DateTime _lastCheckpointFlush=DateTime.UtcNow;
  private readonly Dictionary<string,string> _markupNotices=new Dictionary<string,string>();
  private readonly UiSaveScheduler _saveScheduler=new UiSaveScheduler(TimeSpan.FromSeconds(1.5),TimeSpan.FromSeconds(8));
  private readonly DispatcherTimer _saveTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};
  private readonly object _saveGate=new object();
  private long _saveSequence;
  private long _lastWrittenSequence;
  private bool _saveInFlight;
  private readonly UiSaveTracker _saveTracker=new UiSaveTracker();
  private readonly DispatcherTimer _mappingRefreshTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(600)};
  private bool _mappingRefreshPending;
  private bool _exportStarting;
  private bool _rehydrating;
  private UiProjectLock? _projectLock;
  // Any work that must not overlap with another: a check or export with its overlay, the start of an export, a rehydrate.
  private bool Busy=>_isBusy||_exportStarting||_rehydrating;
  // One estimator for the whole session: what earlier checks measured makes the next estimate better.
  private readonly UiScanEstimator _estimator=new UiScanEstimator();

  // Once at start: timers, leftover temporary copies of earlier exports (item G2) and the integrity notice (item R4).
  private void StartupHousekeeping()
  {
   _progressTimer.Tick+=(_,_)=>OnProgressTick();
   _saveTimer.Tick+=(_,_)=>OnSaveTimerTick();_saveTimer.Start();
   _mappingRefreshTimer.Tick+=(_,_)=>{if(_mappingRefreshPending){_mappingRefreshPending=false;RefreshMappings();}};_mappingRefreshTimer.Start();
   Action crashFlush=FlushForCrash;
   UiCrashFlush.Register(crashFlush);
   Closed+=(_,_)=>{_progressTimer.Stop();_saveTimer.Stop();_mappingRefreshTimer.Stop();UiCrashFlush.Unregister(crashFlush);_projectLock?.Dispose();_projectLock=null;};
   try{DocumentProcessor.SweepStaleTempFiles(Path.GetTempPath());}catch(Exception){}
   try
   {
    var warning=RuntimeIntegrity.Default.IntegrityWarning;
    if(warning!=null)FooterVersion.ToolTip=warning+" This is a notice only. Nothing is blocked.";
   }
   catch(Exception){}
  }

  // Called by the crash guard from another thread just before the app exits after a fatal error (UI-5). Best effort:
  // it writes the project only when it has unsaved changes and the format upgrade was accepted, waits at most a
  // few seconds for a write already running, and never throws.
  internal void FlushForCrash()
  {
   try
   {
    var project=_project;var path=_path;
    if(project==null||path==null||!_saveScheduler.IsDirty)return;
    if(_upgradeGate.Decide(project.LoadedFormat,false)!=UiSaveAction.Save)return;
    if(!Monitor.TryEnter(_saveGate,TimeSpan.FromSeconds(3)))return;
    try{var json=ProjectStore.Serialize(project);ProjectStore.SaveSerialized(path,project.Passphrase,json);}
    finally{Monitor.Exit(_saveGate);}
   }
   catch(Exception){}
  }

  // ---------------------------------------------------------------- saving (items P1, U4)
  // The one place a project is written on the user interface thread. userRequested: the user pressed Save,
  // renamed the project or changed the passphrase. An older project asks for the upgrade first (once), and
  // Cancel keeps it unsaved. Automatic saves go through RequestSave and SaveProjectAsync instead.
  private bool SaveProjectNow(bool userRequested)
  {
   if(_project==null||_path==null)return false;
   switch(_upgradeGate.Decide(_project.LoadedFormat,userRequested))
   {
    case UiSaveAction.SkipQuietly:if(_saveScheduler.IsDirty)_declinedChanges=true;return false;
    case UiSaveAction.AskUpgrade:
     if(!ConfirmUpgrade()){_upgradeGate.Decline();if(_saveScheduler.IsDirty)_declinedChanges=true;StatusText.Text=UiMessages.UpgradeDeclinedStatus;return false;}
     _upgradeGate.Approve();
     break;
   }
   try
   {
    var sequence=++_saveSequence;
    var path=_path;var passphrase=_project.Passphrase;
    var json=ProjectStore.Serialize(_project);
    lock(_saveGate){ProjectStore.SaveSerialized(path,passphrase,json);_lastWrittenSequence=Math.Max(_lastWrittenSequence,sequence);}
    _project.LoadedFormat=ProjectFormat.Dsp2;
    _saveScheduler.MarkSaved();
    _declinedChanges=false;
    return true;
   }
   catch(Exception ex){_saveScheduler.MarkDirty(DateTime.UtcNow);QueueIssue(UiIssues.Failure(IssueKind.SaveFailed,ex));return false;}
  }

  private bool TryLockProjectForEdit(string path,out UiProjectLock? projectLock)
  {
   projectLock=null;
   try{projectLock=UiProjectLock.TryAcquire(path,out _);}
   catch(IOException){QueueIssue(UiIssues.For(IssueKind.ProjectAccessFailed));return false;}
   catch(UnauthorizedAccessException){QueueIssue(UiIssues.For(IssueKind.ProjectAccessFailed));return false;}
   if(projectLock!=null)return true;
   QueueIssue(UiIssues.For(IssueKind.ProjectAlreadyOpen));return false;
  }

  private bool SaveBeforeLeaving()
  {
   if(!FinishInFlightSave())return false;
   if(_saveScheduler.IsDirty&&!SaveProjectNow(false)&&!_upgradeGate.Declined)return false;
   return ConfirmLeaveUnsaved();
  }

  // The same cleanup handles Back and failed setup. Do not release the lock until
  // writes have finished, except a failed open that never started a write.
  private void LeaveProject()
  {
   var closing=_activeEntry;
   _project=null;_path=null;_activeEntry=null;_upgradeGate.Reset();_declinedChanges=false;
   _saveScheduler.MarkSaved();saveIssueShown=false;
   UiProjectSession.Close(closing);
   _projectLock?.Dispose();_projectLock=null;
   ProjectsList.Items.Refresh();_files.Clear();_mappingView=null;_mappingFilter="Review";MappingsGrid.ItemsSource=null;
  }

  // The explicit OK before an older project is converted. Cancel is the default button.
  private bool ConfirmUpgrade()=>MessageBox.Show(this,UiMessages.UpgradeNotice,"Upgrade project format",MessageBoxButton.OKCancel,MessageBoxImage.Information,MessageBoxResult.Cancel)==MessageBoxResult.OK;

  // A change that does not need to reach the disk this second (a cell moved, a decision set). The project is
  // written shortly after the last change, and at the latest a few seconds after the first one.
  private void RequestSave(){if(_project!=null)_saveScheduler.MarkDirty(DateTime.UtcNow);}

  // True once changes were made that could not be saved because the user declined the format upgrade.
  private bool _declinedChanges;

  // Asked before leaving a project (or closing) whose changes could not be saved. Returns false to stay.
  private bool ConfirmLeaveUnsaved()
  {
   if(!_declinedChanges||!_upgradeGate.Declined)return true;
   return MessageBox.Show(this,"This project was opened without upgrading, so the changes made in this session were not saved. Leave without saving?","Changes not saved",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
  }

  // Waits for a background write that is still running (UI-4), so closing never ends the process mid-write.
  // A write that failed marks the project changed again, and the caller's flush then writes it on this thread.
  // Returns false when the write is still running after the wait: the caller stays open.
  private bool FinishInFlightSave()
  {
   if(!_saveTracker.IsPending)return true;
   var finished=_saveTracker.WaitForPending(TimeSpan.FromSeconds(20),out var failed);
   if(!finished){MessageBox.Show(this,"The project is still being saved. Try again in a moment.","Saving",MessageBoxButton.OK,MessageBoxImage.Information);return false;}
   if(failed)_saveScheduler.MarkDirty(DateTime.UtcNow);
   return true;
  }

  // Writes a pending change now (before a scan or export, when going back to the project list or closing).
  private void FlushPendingSave(){if(_saveScheduler.IsDirty)SaveProjectNow(false);}

  private async void OnSaveTimerTick()
  {
   if(_project==null||_path==null||_saveInFlight||!_saveScheduler.ShouldSave(DateTime.UtcNow))return;
   // An older project is never upgraded in the background: that needs the explicit OK of SaveProjectNow.
   if(_upgradeGate.Decide(_project.LoadedFormat,false)!=UiSaveAction.Save){_saveScheduler.MarkSaved();_declinedChanges=true;return;}
   _saveInFlight=true;
   try
   {
    var sequence=++_saveSequence;
    var path=_path;var passphrase=_project.Passphrase;
    var json=ProjectStore.Serialize(_project);
    _saveScheduler.MarkSaved();
    await _saveTracker.Track(Task.Run(()=>{lock(_saveGate){if(sequence<_lastWrittenSequence){ProjectContainer.Wipe("save-json",json);return;}ProjectStore.SaveSerialized(path,passphrase,json);_lastWrittenSequence=sequence;}}));
    if(_project!=null&&_path==path)_project.LoadedFormat=ProjectFormat.Dsp2;
    saveIssueShown=false;
   }
   catch(Exception ex){_saveScheduler.MarkDirty(DateTime.UtcNow);StatusText.Text="The project could not be saved yet.";if(!saveIssueShown){saveIssueShown=true;QueueIssue(UiIssues.Failure(IssueKind.SaveFailed,ex));}}
   finally{_saveInFlight=false;}
  }

  // ---------------------------------------------------------------- opening a project (items P1, P3)
  // Right after a project is unlocked: the one-time upgrade question, then the one-time offer to bring the
  // old global keep-as-is list into this project.
  private void AfterProjectOpened()
  {
   if(_project==null)return;
   _declinedChanges=false;
   SaveProjectNow(false);
   if(_upgradeGate.Declined)return;
   try
   {
    var legacyPath=LegacyKeepAsIsFile.DefaultPath(Path.GetFileName(ProjectCatalog.Root));
    var offered=UiProjectSession.LegacyLabelsToOffer(_project,legacyPath);
    if(offered.Count==0)return;
    var preview=string.Join(", ",offered.Take(8))+(offered.Count>8?", and "+(offered.Count-8)+" more":"");
    var yes=MessageBox.Show(this,UiMessages.LegacyImportPrompt(offered.Count)+"\n\n"+preview,"Import kept labels",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
    var imported=UiProjectSession.AnswerLegacyOffer(_project,offered,yes);
    SaveProjectNow(false);
    if(yes)StatusText.Text=UiMessages.LegacyImportResult(imported,offered.Count);
   }
   catch(Exception ex){StatusText.Text="The old keep-as-is list could not be read: "+ex.Message;}
  }

  private void ReviewLists_Click(object s,RoutedEventArgs e)
  {
   if(!Ready())return;
   var dialog=new ReviewListsDialog(_project!.ReviewPreferences){Owner=this};
   dialog.ShowDialog();
   if(dialog.Changed){SaveProjectNow(false);StatusText.Text="Review lists saved inside this project.";}
  }

  // Edits the neutral label shown in the project list while the project is locked.
  private void EditProjectLabel_Click(object s,RoutedEventArgs e)
  {
   if(!(ProjectsList.SelectedItem is ProjectEntry entry))return;
   var others=_projects.Where(p=>!ReferenceEquals(p,entry)).Select(p=>p.Label).ToList();
   var dialog=new TextPromptDialog("Neutral project label","Label shown in the project list",entry.Label,UiMessages.NeutralLabelWarning,text=>UiProjectLabel.Validate(text,others)){Owner=this};
   if(dialog.ShowDialog()!=true)return;
   entry.Label=dialog.Value;
   if(!entry.NameRevealed)entry.Name=entry.Label;
   ProjectsList.Items.Refresh();SaveCatalog();StatusText.Text="Project label saved.";
  }

  // ---------------------------------------------------------------- reading a document (items G1, U6)
  private sealed class ScanInput
  {
   public string Text="";
   // The text the detectors and AI reviewers see: the visible text plus a labelled section with the text Word keeps
   // in hidden channels (content control titles, document variables, field codes), so values found only there get a
   // mapping too. The export removes those channels either way.
   public string ScanText="";
   public ReviewMarkupSummary Markup=ReviewMarkupSummary.Empty;
   public int HiddenLines;
   public TableReviewContext? Tables;
   // What the protected copy will not contain, for formats that remove pictures, charts, links and similar (Excel, PowerPoint).
   public string Removals="";
   // SHA-256 and length of the file as it was read, when it did not change while it was being read (UI-8).
   public (string Hash,long Length)? Fingerprint;
  }

  // Runs off the interface thread. The package is opened once and shared by every read.
  private static ScanInput ReadScanInput(string path,CancellationToken token)
  {
   var input=new ScanInput();
   DocumentPackageContext? package=null;
   (string Hash,long Length)? before=null;
   try{before=UiFileFingerprint.Compute(path);}catch(Exception){}
   try
   {
    if(string.Equals(Path.GetExtension(path),".docx",StringComparison.OrdinalIgnoreCase))package=DocumentProcessor.OpenContext(path);
    try{input.Markup=DocumentProcessor.InspectReviewMarkup(path,package);}catch(Exception){}
    try{var removals=DocumentFormats.Find(path)?.DescribeRemovals(path,package)??Array.Empty<string>();input.Removals=removals.Count==0?"":"This file type is rebuilt for safety: "+string.Join("; ",removals)+".";}catch(Exception){}
    token.ThrowIfCancellationRequested();
    input.Text=DocumentProcessor.ReadText(path,package);
    IReadOnlyList<string> hidden=Array.Empty<string>();
    try{hidden=DocumentProcessor.ReadHiddenChannels(path,package);}catch(Exception){}
    input.HiddenLines=hidden.Count;
    input.ScanText=UiHiddenText.Append(input.Text,hidden);
    token.ThrowIfCancellationRequested();
    input.Tables=DocumentProcessor.ReadTableContext(path,input.Text,package)?.WithReviewText(input.ScanText);
    try{var after=UiFileFingerprint.Compute(path);if(before!=null&&after==before.Value)input.Fingerprint=before;}catch(Exception){}
   }
   finally{package?.Dispose();}
   return input;
  }

  private void ShowMarkupNotice(FileRecord file,ReviewMarkupSummary summary,string removals="")
  {
   var text=UiMessages.ReviewMarkupBanner(summary);
   if(removals.Length>0)text=text.Length==0?removals:text+" "+removals;
   if(text.Length==0)_markupNotices.Remove(file.Id);else _markupNotices[file.Id]=text;
   UpdateNoticePanel();
  }

  // ---------------------------------------------------------------- scanning (items U1, U2, U3, G1, U6)
  private async Task<bool> ScanFileAsync(FileRecord file)
  {
   if(Busy)return false;
   FlushPendingSave();
   var deepRequested=_settings.AutomaticDeepReview;
   ScanButton.IsEnabled=false; file.Status="Analyzing locally…";FilesList.Items.Refresh();StatusText.Text="Local privacy engines are reviewing this document. Nothing is uploaded.";
   IProgress<AnalysisProgress> progress=new Progress<AnalysisProgress>(ShowAnalysisProgress);
   IProgress<IReadOnlyList<Candidate>> liveFindings=new Progress<IReadOnlyList<Candidate>>(batch=>MergeLiveFindings(file,batch));
   BeginAnalysisProgress("Reading document","Extracting text from the selected file without changing it.","scan");
   var cancellationToken=_analysisCancellation!.Token;
   var scanCommitted=false;
   ExportReviewCache? deepCache=null;
   try
   {
    var input=await Task.Run(()=>ReadScanInput(file.SourcePath,cancellationToken));
    cancellationToken.ThrowIfCancellationRequested();
    ShowMarkupNotice(file,input.Markup,input.Removals);
    var text=input.ScanText;
    DocumentPrivacyPolicy.ClearSuppressed();
    ResetAutomaticFindingsForRescan(file);
    SetAnalysisStage(1,"Checking exact patterns and three independent local detectors.");
    var analysis=await Task.Run(()=>PresidioDetector.Find(text,p=>progress.Report(p),cancellationToken,batch=>liveFindings.Report(batch)));
    cancellationToken.ThrowIfCancellationRequested();
    SupportTelemetry.RecordEngines(analysis.Engines,new[]{"Presidio","GLiNER","Privacy Filter"});
    foreach(var candidate in analysis.Candidates)_project!.EnsureMapping(candidate,file.Id);
    RegisterFragmentFindings(file,text);
    file.LastScanAt=DateTimeOffset.UtcNow;
    file.LastDeepReviewAt=null;file.DeepReviewComplete=false;file.DeepReviewWarning="";
    // Only complete results replace the earlier findings of this file (UI-3). Mappings found again keep their decisions.
    if(analysis.Complete)_project!.CommitRescan(file.Id);else _project!.AbandonRescan();
    scanCommitted=true;
    UiFileFingerprint.Apply(file,input.Fingerprint);
    file.Status=analysis.Complete?"Local scan complete: review mappings":"Scan incomplete: check settings";
    FilesList.Items.Refresh();RefreshMappings();SaveCurrentProjectSilently();
    if(!deepRequested&&analysis.Complete)
    {
     EndAnalysisProgress();
     deepRequested=new DeepCheckChoiceDialog(UiScanEstimator.ForDisplay(PreviewEstimate(text))){Owner=this}.ShowDialog()==true;
     if(deepRequested){BeginAnalysisProgress("Section cross-check","Rechecking overlapping sections with local detectors before AI deep review.","deep");cancellationToken=_analysisCancellation!.Token;}
    }
    if(deepRequested)
    {
     var choice=AskResume(file,false);
     if(choice==ResumeChoice.Cancel)throw new OperationCanceledException();
     deepCache=BeginCheckpointRun(file,"deep",choice==ResumeChoice.Resume);
     ShowEstimate(BeginEstimate(text));
     var deep=await Task.Run(()=>DeepReview.Find(text,p=>progress.Report(new AnalysisProgress{Step=6+p.Step,Total=6+p.Total,Name=p.Name,Detail=p.Detail}),cancellationToken,batch=>liveFindings.Report(batch),input.Tables,deepCache));
     cancellationToken.ThrowIfCancellationRequested();
     foreach(var candidate in deep.Candidates)_project.EnsureMapping(candidate,file.Id);
     RegisterFragmentFindings(file,text);
     file.LastDeepReviewAt=DateTimeOffset.UtcNow;file.DeepReviewComplete=deep.Complete;file.DeepReviewWarning=deep.Warning;
     FinishDeepCheckpoint(deep);
    }
    _project!.RefreshOccurrenceEvidence(file.Id,text);
    file.Status=!analysis.Complete||(deepRequested&&!file.DeepReviewComplete)?"Scan incomplete: check settings":deepRequested?"Deep check complete: review mappings":"Analyzed locally: deep check optional";
    if(_isBusy){SetAnalysisStage(_analysisStages.Length-1,"Organizing findings for your decisions.");await Task.Delay(280,cancellationToken);}
    FilesList.Items.Refresh();_mappingFilter=_settings.OpenOnReview?"Review":"All";RefreshMappings();SaveCurrentProjectSilently();StatusText.Text="Scan complete with "+analysis.EngineLabel+(deepRequested?" and a second local context pass.":". Optional deep check skipped.")+(string.IsNullOrWhiteSpace(analysis.Warning)?"":" "+analysis.Warning)+(string.IsNullOrWhiteSpace(file.DeepReviewWarning)?"":" "+file.DeepReviewWarning)+UiMessages.SuppressedNotice(DocumentPrivacyPolicy.SuppressedOrganizations.Count);UpdateUiState();
    if(!analysis.Complete)QueueIssue(UiIssues.Failure(IssueKind.ScanIncomplete,warning:analysis.Warning),file);
    else if(deepRequested&&!file.DeepReviewComplete)QueueIssue(UiIssues.Failure(IssueKind.DeepIncomplete,warning:file.DeepReviewWarning),file);
    return analysis.Complete&&(!deepRequested||file.DeepReviewComplete);
   }
   catch(OperationCanceledException){EndCheckpointRun(false,null);file.Status=scanCommitted?"Deep check stopped: confirmed findings retained":"Scan stopped: confirmed findings retained";FilesList.Items.Refresh();RefreshMappings();SaveCurrentProjectSilently();StatusText.Text="Analysis stopped. Findings found so far were retained"+(HasCheckpoint(file)?" and the finished sections are saved, so the check can be resumed.":".");return false;}
   catch(Exception ex){EndCheckpointRun(false,null);file.Status="Scan failed";FilesList.Items.Refresh();SaveCurrentProjectSilently();QueueIssue(UiIssues.Failure(IssueKind.ScanIncomplete,ex),file);return false;}
   finally {_project?.AbandonRescan();deepCache?.Dispose();if(_isBusy)EndAnalysisProgress();ScanButton.IsEnabled=Selected!=null;UpdateUiState();}
  }

  private async void DeepCheck_Click(object s,RoutedEventArgs e)
  {
   if(!Ready()||!HasScannedFile(Selected)||Busy)return;
   await RunDeepCheckAsync(Selected!,false);
  }

  // Resume previous check, or Retry failed sections: the saved progress is used without asking again.
  private async void ResumeCheck_Click(object s,RoutedEventArgs e)
  {
   if(!Ready()||Selected==null||Busy)return;
   await RunDeepCheckAsync(Selected,true);
  }

  private async Task RunDeepCheckAsync(FileRecord file,bool automaticResume)
  {
   if(Busy)return;
   FlushPendingSave();
   IProgress<AnalysisProgress> progress=new Progress<AnalysisProgress>(ShowAnalysisProgress);IProgress<IReadOnlyList<Candidate>> liveFindings=new Progress<IReadOnlyList<Candidate>>(batch=>MergeLiveFindings(file,batch));
   var choice=AskResume(file,automaticResume);
   if(choice==ResumeChoice.Cancel)return;
   BeginAnalysisProgress("Section cross-check","Rechecking overlapping sections with local detectors before AI deep review.","deep");
   var cancellationToken=_analysisCancellation!.Token;
   ExportReviewCache? cache=null;
   try
   {
    var input=await Task.Run(()=>ReadScanInput(file.SourcePath,cancellationToken));
    cancellationToken.ThrowIfCancellationRequested();
    ShowMarkupNotice(file,input.Markup,input.Removals);
    // The document is not the one that was scanned: a deep check on it would attach findings to the wrong text (UI-8).
    if(UiFileFingerprint.Differs(file,input.Fingerprint))
    {
     file.DeepReviewComplete=false;file.Status="Changed since scan: scan again";FilesList.Items.Refresh();SaveCurrentProjectSilently();
     StatusText.Text="The document changed since it was scanned. Scan it again first.";
     QueueIssue(UiIssues.For(IssueKind.SourceChanged),file);
     return;
    }
    var text=input.ScanText;
    DocumentPrivacyPolicy.ClearSuppressed();
    ResetAutomaticFindingsForDeepCheck(file);
    cache=BeginCheckpointRun(file,"deep",choice==ResumeChoice.Resume);
    ShowEstimate(BeginEstimate(text));
    var result=await Task.Run(()=>DeepReview.Find(text,p=>progress.Report(p),cancellationToken,batch=>liveFindings.Report(batch),input.Tables,cache));
    cancellationToken.ThrowIfCancellationRequested();
    foreach(var candidate in result.Candidates)_project!.EnsureMapping(candidate,file.Id);
    RegisterFragmentFindings(file,text);
    if(result.Complete)_project!.CommitRescan(file.Id);else _project!.AbandonRescan();
    _project!.RefreshOccurrenceEvidence(file.Id,text);
    file.LastDeepReviewAt=DateTimeOffset.UtcNow;file.DeepReviewComplete=result.Complete;file.DeepReviewWarning=result.Warning;
    UiFileFingerprint.Apply(file,input.Fingerprint);
    FinishDeepCheckpoint(result);
    file.Status=result.Complete?"Deep check complete: review mappings":"Deep check incomplete: review warning";
    SetAnalysisStage(_analysisStages.Length-1,"Organizing findings for your decisions.");await Task.Delay(280,cancellationToken);
    FilesList.Items.Refresh();_mappingFilter=_settings.OpenOnReview?"Review":"All";RefreshMappings();SaveCurrentProjectSilently();
    StatusText.Text=(result.Complete?"Deep check finished: "+result.SectionsChecked+" local section"+(result.SectionsChecked==1?"":"s")+" checked.":"Deep check incomplete. "+result.Warning)+UiMessages.SuppressedNotice(DocumentPrivacyPolicy.SuppressedOrganizations.Count);
    if(!result.Complete)QueueIssue(UiIssues.Failure(IssueKind.DeepIncomplete,warning:result.Warning),file);
    else issueAttempts.Clear();
   }
   catch(OperationCanceledException){EndCheckpointRun(false,null);file.Status="Deep check stopped: confirmed findings retained";FilesList.Items.Refresh();RefreshMappings();SaveCurrentProjectSilently();StatusText.Text="Deep check stopped. Findings found so far were retained"+(HasCheckpoint(file)?" and the finished sections are saved, so the check can be resumed.":".");}
   catch(Exception ex){EndCheckpointRun(false,null);file.Status="Deep check failed";FilesList.Items.Refresh();SaveCurrentProjectSilently();QueueIssue(UiIssues.Failure(IssueKind.DeepIncomplete,ex),file);}
   finally{_project?.AbandonRescan();cache?.Dispose();EndAnalysisProgress();UpdateUiState();}
  }

  // ---------------------------------------------------------------- resumable checks (items U1, U3)
  private static string CheckpointKey(FileRecord file,string kind)=>file.Id+"#"+kind;

  private bool HasCheckpoint(FileRecord file)=>_project!=null&&_project.ReviewCheckpoints.TryGetValue(CheckpointKey(file,"deep"),out var checkpoint)&&UiResume.HasWork(checkpoint);

  // Asks what to do with the saved progress of an interrupted check. Nothing saved means start fresh without a question.
  private ResumeChoice AskResume(FileRecord file,bool automatic)
  {
   if(_project==null||!_project.ReviewCheckpoints.TryGetValue(CheckpointKey(file,"deep"),out var checkpoint)||!UiResume.HasWork(checkpoint))return ResumeChoice.StartOver;
   if(automatic)return ResumeChoice.Resume;
   var dialog=new ResumeChoiceDialog(checkpoint){Owner=this};
   dialog.ShowDialog();
   return dialog.Choice;
  }

  // A review cache whose finished sections are also written into the encrypted project as they complete.
  private ExportReviewCache BeginCheckpointRun(FileRecord file,string kind,bool resume)
  {
   var cache=new ExportReviewCache();
   var recorder=new UiCheckpointRecorder();
   var key=CheckpointKey(file,kind);
   if(_project!=null)
   {
    UiCheckpointPruner.Prune(_project.ReviewCheckpoints,_project.Files.Select(f=>f.Id),resume?key:null);
    if(resume&&_project.ReviewCheckpoints.TryGetValue(key,out var saved))recorder.Restore(saved,cache);
    else _project.ReviewCheckpoints.Remove(key);
   }
   recorder.Attach(cache);
   _recorder=recorder;_recorderKey=key;_lastCheckpointFlush=DateTime.UtcNow;
   return cache;
  }

  // Stores what the run finished (or removes the saved progress when everything was checked).
  private void EndCheckpointRun(bool complete,IReadOnlyList<SectionFailure>? failures)
  {
   var recorder=_recorder;var key=_recorderKey;_recorder=null;_recorderKey=null;
   if(recorder==null||key==null||_project==null)return;
   var failed=failures??Array.Empty<SectionFailure>();
   if(complete&&failed.Count==0)_project.ReviewCheckpoints.Remove(key);
   else if(recorder.Count>0||failed.Count>0)_project.ReviewCheckpoints[key]=recorder.Commit(failed);
   UiCheckpointPruner.Prune(_project.ReviewCheckpoints,_project.Files.Select(f=>f.Id));
   RequestSave();
  }

  private void FinishDeepCheckpoint(DeepReviewResult result)
  {
   var failures=UiResume.ParseFailures(result.Warning);
   SupportTelemetry.RecordSections(result.SectionsChecked,failures.Count);
   EndCheckpointRun(result.Complete,failures);
  }

  private void FlushCheckpointIfDue()
  {
   if(_recorder==null||_recorderKey==null||_project==null)return;
   if(!_recorder.HasUncommittedChanges||DateTime.UtcNow-_lastCheckpointFlush<TimeSpan.FromSeconds(15))return;
   _lastCheckpointFlush=DateTime.UtcNow;
   _project.ReviewCheckpoints[_recorderKey]=_recorder.Commit(null);
   UiCheckpointPruner.Prune(_project.ReviewCheckpoints,_project.Files.Select(f=>f.Id),_recorderKey);
   RequestSave();
  }

  // Under the file list: the tracked changes notice and the resume offer for the selected document.
  private void UpdateNoticePanel()
  {
   if(!IsInitialized)return;
   var file=Selected;
   var markup=file!=null&&_markupNotices.TryGetValue(file.Id,out var text)?text:"";
   ReviewCheckpoint? checkpoint=null;
   if(file!=null&&_project!=null)_project.ReviewCheckpoints.TryGetValue(CheckpointKey(file,"deep"),out checkpoint);
   var resumable=!Busy&&UiResume.HasWork(checkpoint);
   MarkupNoticeText.Text=markup;MarkupNoticeText.Visibility=markup.Length>0?Visibility.Visible:Visibility.Collapsed;
   if(resumable)
   {
    ResumeNoticeText.Text=(checkpoint!.Failed.Count>0?UiResume.FailedSummary(checkpoint.Failed)+" ":"")+UiResume.OfferDetail(checkpoint);
    ResumeCheckButton.Content=checkpoint.Failed.Count>0?"Retry failed sections":UiResume.OfferText(checkpoint);
   }
   ResumeNoticeText.Visibility=resumable?Visibility.Visible:Visibility.Collapsed;
   ResumeCheckButton.Visibility=resumable?Visibility.Visible:Visibility.Collapsed;
   ResumeCheckButton.IsEnabled=resumable&&file!=null&&HasScannedFile(file);
   NoticePanel.Visibility=markup.Length>0||resumable?Visibility.Visible:Visibility.Collapsed;
  }

  // ---------------------------------------------------------------- estimate of a long check (item P6)
  private int ReviewerCount=>Math.Max(1,ExpectedReviewers.Length);

  // The estimate for a document text, without starting to time anything (used in a question before the check starts).
  private string PreviewEstimate(string text)=>PerfEstimate.FromSections(UiScanEstimator.SectionSizes(text),ReviewerCount,_estimator.Tracker).Text;

  // Starts timing reviewer sections for this check and returns the estimate text.
  private string BeginEstimate(string text)=>_estimator.Begin(UiScanEstimator.SectionSizes(text),ReviewerCount);

  private void ShowEstimate(string text)
  {
   var shown=UiScanEstimator.ForDisplay(text);
   EstimateText.Text=shown;EstimateText.Visibility=shown.Length>0?Visibility.Visible:Visibility.Collapsed;
  }

  // ---------------------------------------------------------------- progress rows, time and estimate (item U2)
  private void StartProgressTracking()
  {
   _tracker=new UiProgressTracker(null,ExpectedReviewers);
   _tracker.Start();
   ReviewerRows.ItemsSource=_tracker.Rows;
   AnalysisTimeText.Text=_tracker.TimeText;ModelLoadText.Visibility=Visibility.Collapsed;
   _progressTimer.Start();
  }

  private void StopProgressTracking()
  {
   _progressTimer.Stop();
   if(_tracker!=null&&_analysisWorkflow.Length>0)SupportTelemetry.RecordPhase(_analysisWorkflow,_tracker.Elapsed.TotalSeconds);
   _estimator.End();EstimateText.Text="";EstimateText.Visibility=Visibility.Collapsed;
   _tracker?.Finish();
   ReviewerRows.ItemsSource=null;ReviewerRows.Visibility=Visibility.Collapsed;ModelLoadText.Visibility=Visibility.Collapsed;
   _tracker=null;
  }

  private void ReportTracker(AnalysisProgress progress)
  {
   if(_tracker==null)return;
   _tracker.Report(progress.Name,progress.Step,progress.Total,progress.Detail);
   var refreshed=_estimator.Report(progress.Name,progress.Detail);
   if(refreshed!=null)ShowEstimate(refreshed);
   if(UiProgressTracker.TryParseReviewer(progress.Name,progress.Detail,out _,out _,out var sectionCount)&&sectionCount>0)_recorder?.ReportProgress(0,Math.Max(1,ExpectedReviewers.Length)*sectionCount);
   ReviewerRows.Visibility=_tracker.Rows.Count>0?Visibility.Visible:Visibility.Collapsed;
  }

  private void OnProgressTick()
  {
   if(_tracker==null)return;
   AnalysisTimeText.Text=_tracker.TimeText;
   var notice=_tracker.ModelLoadNotice;
   ModelLoadText.Text=notice??"";ModelLoadText.Visibility=notice!=null?Visibility.Visible:Visibility.Collapsed;
   FlushCheckpointIfDue();
  }

  // ---------------------------------------------------------------- live findings (item U4)
  // Findings arrive in many small batches. Rebuilding the table for each one made the window stutter, so the
  // table is rebuilt at most about twice a second while a check runs.
  private void ScheduleMappingRefresh(){_mappingRefreshPending=true;}

  // ---------------------------------------------------------------- export (items G2, G3, G1, U3, U5, U4)
  private sealed class FinalSafety
  {
   public AnalysisResult Result=new AnalysisResult();
   public List<SectionFailure> Failed=new List<SectionFailure>();
  }

  private async void Desensitize_Click(object s, RoutedEventArgs e)
  {
   if(Busy)return;
   // Busy from the first moment (UI-7): the work before the progress overlay starts also awaits, and Back, Scan,
   // Rehydrate and project switching must stay disabled during it.
   _exportStarting=true;UpdateUiState();
   try{await DesensitizeAsync();}
   finally{_exportStarting=false;UpdateUiState();}
  }

  // Standalone shortened forms of approved names (fragment guard) become Review items. Decided ones are touched again so a rescan keeps them.
  private void RegisterFragmentFindings(FileRecord file,string text)
  {
   if(_project==null)return;
   foreach(var hit in FragmentGuard.Analyze(text,_project.Mappings.ToList(),file.Id))
   {
    if(!hit.Decided){_project.EnsureMapping(hit.Candidate,file.Id);continue;}
    if(FragmentGuard.IsFragment(hit.DecidedBy!)&&hit.DecidedBy!.NormalizedValue==Project.Normalize(hit.Value))_project.EnsureMapping(hit.Candidate,file.Id);
   }
  }

  private List<Candidate> ResidualLeaks(IEnumerable<Candidate> candidates,string sourceFileId)
  {
   // A Keep decision on a fragment of an approved name is scoped to its category (fragment guard), so it cannot hide a different finding.
   var kept=_project!.Mappings.Where(m=>m.Ignored&&!m.RequiresContextReview&&m.SourceFileIds.Contains(sourceFileId)).ToList();
   var allowed=new HashSet<string>(kept.Where(m=>!FragmentGuard.IsFragment(m)).Select(m=>m.NormalizedValue),StringComparer.Ordinal);
   var allowedFragments=new HashSet<string>(kept.Where(FragmentGuard.IsFragment).Select(m=>m.Category+"|"+m.NormalizedValue),StringComparer.Ordinal);
   return candidates.Where(IsResidualLeak).Where(candidate=>!allowed.Contains(Project.Normalize(candidate.Value))&&!allowedFragments.Contains(candidate.Category+"|"+Project.Normalize(candidate.Value))).GroupBy(candidate=>Project.Normalize(candidate.Value)).Select(group=>group.OrderByDescending(candidate=>candidate.RequiresContextReview).ThenByDescending(candidate=>candidate.Confidence).First()).ToList();
  }

  private async Task DesensitizeAsync()
  {
   if(!Ready()||Selected==null)return;
   var selectedFile=Selected!;
   if(_settings.AutomaticDeepReview&&!selectedFile.DeepReviewComplete){QueueIssue(UiIssues.For(IssueKind.DeepRequired),selectedFile);return;}
   DocumentProcessingOptions options;
   try{options=UiImageSettings.Build(_settings);}
   catch(ArgumentException){QueueIssue(UiIssues.For(IssueKind.InvalidPlaceholder),selectedFile);return;}
   FlushPendingSave();
   var sourcePath=selectedFile.SourcePath;
   var sourceFileId=selectedFile.Id;
   // The file about to be exported must be the one that was scanned (UI-8).
   (string Hash,long Length)? startPrint;
   var printState=await Task.Run(()=>{var state=UiFileFingerprint.Check(selectedFile,sourcePath,out var print);return (state,print);});
   startPrint=printState.print;
   if(_project==null||!_project.Files.Contains(selectedFile))return;
   if(printState.state==FingerprintState.Missing){QueueIssue(UiIssues.For(IssueKind.MissingFile),selectedFile);return;}
   if(printState.state==FingerprintState.Mismatch)
   {
    selectedFile.DeepReviewComplete=false;selectedFile.Status="Changed since scan: scan again";FilesList.Items.Refresh();SaveCurrentProjectSilently();UpdateUiState();
    StatusText.Text="Export stopped: the document changed since it was scanned. Nothing was exported.";
    QueueIssue(UiIssues.For(IssueKind.SourceChanged),selectedFile);
    return;
   }
   var unknownPrint=printState.state==FingerprintState.Unknown;
   // Older projects may have approved a full organization name before we
   // started detecting its adjacent acronym. Require an explicit decision
   // on that acronym before a new protected copy can be released.
   var project=_project!;
   var approvedNames=project.Mappings.Where(m=>m.Approved&&(m.Category=="ORGANIZATION"||m.Category=="VENDOR"||m.Category=="DEPARTMENT"))
    .Select(m=>new Candidate{Value=m.OriginalValue,Category=m.Category}).ToList();
   List<Candidate> relatedAcronyms;
   IReadOnlyList<int> estimateSizes=Array.Empty<int>();
   try{relatedAcronyms=await Task.Run(()=>{var sourceText=DocumentProcessor.ReadText(sourcePath);estimateSizes=UiScanEstimator.SectionSizes(sourceText);return FindingPostProcessor.Complete(sourceText,approvedNames)
    .Where(c=>c.Source=="Related acronym").ToList();});}
   catch(Exception ex){QueueIssue(UiIssues.Failure(IssueKind.MissingFile,ex),selectedFile);return;}
   if(_project==null||!ReferenceEquals(_project,project)||!_project.Files.Contains(selectedFile))return;
   var undecidedAcronyms=new List<Candidate>();
   foreach(var acronym in relatedAcronyms)
   {
    var existing=_project.Mappings.FirstOrDefault(m=>m.NormalizedValue==Project.Normalize(acronym.Value));
    if(existing==null){_project.EnsureMapping(acronym,sourceFileId);undecidedAcronyms.Add(acronym);}
    else if(!existing.Approved&&!existing.Ignored)undecidedAcronyms.Add(acronym);
   }
   if(undecidedAcronyms.Count>0)
   {
    _mappingFilter="Review";RefreshMappings();SaveCurrentProjectSilently();
    QueueIssue(UiIssues.For(IssueKind.ResidualFindings),selectedFile);
    return;
   }
   var chosenPath=ChooseOutput("Desensitized",sourcePath); if(chosenPath==null)return;
   // The copy always keeps the source file's type (UI-2): the transform and the final scan pick their reader from the extension.
   var exportPaths=UiOutputPath.Plan(chosenPath,sourcePath,Path.GetTempPath());
   var outPath=exportPaths.OutputPath;
   if(exportPaths.Corrected&&File.Exists(outPath)&&MessageBox.Show(this,"The file name was changed to "+exportPaths.ShownName+" so it keeps the type of the source document. That file already exists. Replace it?","Replace existing file",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
   if(SamePath(outPath,sourcePath)){MessageBox.Show("Choose a different file name. Desensitizer never overwrites your source document.","Choose another location");return;}
   // Item U8: with the final AI review off the user is asked once per export, and the safer answer is the default.
   var runFinalAi=_settings.AutomaticDeepReview;
   if(UiFinalReview.NeedsChoice(_settings.AutomaticDeepReview))
   {
    var estimateLine=PerfEstimate.FromSections(estimateSizes,ReviewerCount,_estimator.Tracker).Text;
    var finalChoice=new FinalReviewChoiceDialog(ReviewerCount,estimateLine,UiAdvisory.CountOpenMappings(_project.Mappings,sourceFileId)){Owner=this};
    finalChoice.ShowDialog();
    if(!UiFinalReview.MayExport(false,finalChoice.Choice)){StatusText.Text="Export cancelled. Nothing was exported.";return;}
    runFinalAi=UiFinalReview.AiReviewWillRun(false,finalChoice.Choice);
   }
   var tempPath=exportPaths.TempPath;
   DesensitizeButton.IsEnabled=false; ScanButton.IsEnabled=false; StatusText.Text="Creating a protected copy and checking it again for residual sensitive data…";
   IProgress<AnalysisProgress> progress=new Progress<AnalysisProgress>(ShowAnalysisProgress);
   BeginAnalysisProgress("Create protected copy","Applying approved replacement tokens in a temporary local file.","export");
   var cancellationToken=_analysisCancellation!.Token;
   string? completedPath=null;
   var allowUnapplied=false;
   var exportFailures=new List<SectionFailure>();
   var exportComplete=false;
   var exportNotice="";
   IReadOnlyList<Candidate> finalScanCandidates=Array.Empty<Candidate>();
   // Finished reviewer sections of this export are kept in the encrypted project, so a repeated export after a
   // failure only repeats the sections that did not finish. A saved section is only used for identical text.
   using var reviewCache=BeginCheckpointRun(selectedFile,"export",true);
   try
   {
    var sourceInput=await Task.Run(()=>ReadScanInput(sourcePath,cancellationToken));
    cancellationToken.ThrowIfCancellationRequested();
    ShowMarkupNotice(selectedFile,sourceInput.Markup,sourceInput.Removals);
    if(runFinalAi)ShowEstimate(BeginEstimate(sourceInput.ScanText));
    const int maximumAttempts=5;
    string? previousCopyHash=null;
    for(var attempt=1;attempt<=maximumAttempts;attempt++)
    {
     _project!.RefreshOccurrenceEvidence(sourceFileId,sourceInput.ScanText);
     _project.ConsolidatePersonAliases();
     // Every approved mapping of the project applies to every document (UI-1), whichever file it was found or added in.
     var approved=UiExportScope.Approved(_project.Mappings);
     FinalSafety final;
     try{final=await RunFinalSafetyAnalysisAsync(sourcePath,tempPath,approved,_project.Mappings.ToList(),sourceFileId,progress,cancellationToken,reviewCache,options,allowUnapplied,runFinalAi);}
     catch(InvalidDataException ex) when(!allowUnapplied&&UiMessages.TryParseUnapplied(ex,out _))
     {
      UiMessages.TryParseUnapplied(ex,out var unapplied);
      var choice=new UnappliedMappingsDialog(unapplied){Owner=this};
      choice.ShowDialog();
      if(!choice.ExportAnyway)
      {
       selectedFile.Status="Export stopped: fix the replacement list";FilesList.Items.Refresh();
       _mappingFilter="Replace";RefreshMappings();
       StatusText.Text="Export stopped. Nothing was exported. Check the approved replacements and remove or change the ones listed.";
       return;
      }
      allowUnapplied=true;attempt--;continue;
     }
     var analysis=final.Result;exportFailures=final.Failed;finalScanCandidates=analysis.Candidates.ToList();
     cancellationToken.ThrowIfCancellationRequested();
     if(!analysis.Complete)
     {
      // Findings from the sections that did finish are kept and shown in Review. The sections that did not finish are listed.
      var partial=ResidualLeaks(analysis.Candidates,sourceFileId).Where(c=>_project.Mappings.All(m=>m.NormalizedValue!=Project.Normalize(c.Value))).ToList();
      if(partial.Count>0){RegisterResidualFindings(partial,sourceFileId,false);_mappingFilter="Review";RefreshMappings();}
      SaveCurrentProjectSilently();
      selectedFile.Status="Export blocked: local safety check incomplete";FilesList.Items.Refresh();StatusText.Text="Export blocked: the final local safety check did not complete.";
      QueueIssue(UiIssues.Failure(IssueKind.ExportIncomplete,warning:analysis.Warning),selectedFile);
      return;
     }
     var leaks=ResidualLeaks(analysis.Candidates,sourceFileId);
     if(leaks.Count==0)break;
     var residualMappings=RegisterResidualFindings(leaks,sourceFileId,_settings.AutoReplaceDuringExport);
     _project.ConsolidatePersonAliases();
     SaveCurrentProjectSilently();RefreshMappings();
     var copyHash=await Task.Run(()=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DocumentProcessor.ReadText(tempPath)))));
     var noProgress=copyHash==previousCopyHash;
     previousCopyHash=copyHash;
     var contextualConflict=residualMappings.Any(m=>m.RequiresContextReview);
     if(_settings.AutoReplaceDuringExport&&attempt<maximumAttempts&&!noProgress&&!contextualConflict)
     {
      selectedFile.Status="Export retry "+attempt+": replacing "+leaks.Count+" new finding"+(leaks.Count==1?"":"s");FilesList.Items.Refresh();
      StatusText.Text="Automatic replacement added "+leaks.Count+" finding"+(leaks.Count==1?"":"s")+". Checking the protected copy again.";
      SetAnalysisStage(0,"Automatic replacement pass "+(attempt+1)+" of "+maximumAttempts+".",false);
      continue;
     }
     foreach(var mapping in residualMappings)mapping.Decision="Review";
     selectedFile.Status="Export blocked: review "+leaks.Count+" finding"+(leaks.Count==1?"":"s");
     FilesList.Items.Refresh();_mappingFilter="Review";RefreshMappings();SaveCurrentProjectSilently();
     var fragmentLeaks=leaks.Where(FragmentGuard.IsFragment).ToList();
     StatusText.Text=fragmentLeaks.Count>0?"Export blocked. "+fragmentLeaks.Count+" shortened name"+(fragmentLeaks.Count==1?"":"s")+" and "+(leaks.Count-fragmentLeaks.Count)+" other finding"+(leaks.Count-fragmentLeaks.Count==1?"":"s")+" are now visible in Review.":"Export blocked. The residual findings are now visible in Review.";
     QueueIssue(fragmentLeaks.Count>0?new UserIssue(IssueKind.ResidualFindings,"Shortened names need a decision",FragmentGuard.BlockMessage(fragmentLeaks)+(leaks.Count>fragmentLeaks.Count?" "+(leaks.Count-fragmentLeaks.Count)+" other new finding"+(leaks.Count-fragmentLeaks.Count==1?"":"s")+" also need"+(leaks.Count-fragmentLeaks.Count==1?"s":"")+" a decision.":""),"Inspect each finding in context and choose Replace or Keep as-is. Then export again.","Review findings",IssueAction.ReviewFindings):UiIssues.For(IssueKind.ResidualFindings),selectedFile);
     return;
    }
    cancellationToken.ThrowIfCancellationRequested();
    // The source must still be the file that was checked: a change during the export means the copy does not match it.
    var endPrint=await Task.Run(()=>{try{return (ValueTuple<string,long>?)UiFileFingerprint.Compute(sourcePath);}catch(Exception){return null;}});
    if(startPrint==null||endPrint==null||startPrint.Value!=endPrint.Value)throw new InvalidOperationException(UiFileFingerprint.MismatchMessage);
    SetAnalysisStage(_analysisStages.Length-1,"Saving the validated copy to your chosen location.");
    await Task.Run(()=>AtomicOutput.Copy(tempPath,outPath));
    await Task.Delay(280);
    selectedFile.Status="Exported: local checks passed"; FilesList.Items.Refresh(); StatusText.Text="Export completed after local checks: "+Path.GetFileName(outPath);
    completedPath=outPath;exportComplete=true;
    exportNotice=UiExportScope.ZeroAppliedWarning(UiExportScope.Approved(_project!.Mappings),sourceInput.ScanText);
    if(unknownPrint)exportNotice=(exportNotice.Length>0?exportNotice+" ":"")+UiFileFingerprint.UnknownNotice;
    if(exportPaths.Corrected)exportNotice=(exportNotice.Length>0?exportNotice+" ":"")+"The file name was changed to "+exportPaths.ShownName+" so it keeps the type of the source document.";
   }
   catch(OperationCanceledException){StatusText.Text="Export stopped. No new copy was released; project mappings are unchanged.";}
   catch(Exception ex){QueueIssue(UiIssues.Failure(IssueKind.ExportFailed,ex),selectedFile);}
   finally {try{if(File.Exists(tempPath))File.Delete(tempPath);}catch{} EndCheckpointRun(exportComplete,exportComplete?null:exportFailures);EndAnalysisProgress();UpdateUiState();}
   if(completedPath!=null)ShowExportComplete(completedPath,UiFinalReview.CompletionText(runFinalAi,UiAdvisory.Count(_project!.Mappings,sourceFileId,finalScanCandidates))+(exportNotice.Length>0?" "+exportNotice:""));
  }

  private Task<FinalSafety> RunFinalSafetyAnalysisAsync(string sourcePath,string tempPath,List<Mapping> approved,List<Mapping> projectMappings,string sourceFileId,IProgress<AnalysisProgress> progress,CancellationToken cancellationToken,ExportReviewCache reviewCache,DocumentProcessingOptions options,bool allowUnapplied,bool deepReview)
  {
   return Task.Run(()=>
   {
    cancellationToken.ThrowIfCancellationRequested();
    UiOutputPath.RequireSameFormat(sourcePath,tempPath);
    DocumentProcessor.Desensitize(sourcePath,tempPath,approved,options,null,allowUnapplied);
    cancellationToken.ThrowIfCancellationRequested();
    var exportedText=DocumentProcessor.ReadText(tempPath);
    progress.Report(new AnalysisProgress{Step=1,Total=6,Name="Final detector scan",Detail="Checking the protected copy with local detectors."});
    var detectorResult=PresidioDetector.Find(exportedText,p=>progress.Report(p),cancellationToken);
    var outcome=new FinalSafety{Result=detectorResult};
    // Selected identifying values are independent evidence, even if a model
    // misses them. Narrow stale automatic headings without changing old tokens.
    detectorResult.Candidates.AddRange(DocumentPrivacyPolicy.Apply(exportedText,approved.Where(m=>!m.IsManual).Select(m=>new Candidate{Value=m.OriginalValue,Category=m.Category,Confidence=1,Source="Selected value verification",Explanation="Identifying value remains from an already selected automatic finding."})));
    // Fragment guard: a lone first name, surname or company stem of an approved full name is not replaced and needs a decision.
    // The same text the scan used: visible text plus the hidden channels of the protected copy.
    IReadOnlyList<string> hiddenOut=Array.Empty<string>();
    try{hiddenOut=DocumentProcessor.ReadHiddenChannels(tempPath);}catch(Exception){}
    var fragments=FragmentGuard.Find(UiHiddenText.Append(exportedText,hiddenOut),projectMappings,sourceFileId);
    detectorResult.Candidates.AddRange(fragments);
    if(!detectorResult.Complete||!deepReview)return outcome;
    progress.Report(FinalAiProgress());
    try
    {
     // FindDetailed keeps the findings of every section that finished and names the sections that did not.
     var review=FinalAiReview(exportedText,p=>progress.Report(p),cancellationToken,reviewCache,detectorResult.Candidates,DocumentProcessor.ReadTableContext(tempPath,exportedText));
     detectorResult.Candidates.AddRange(review.Candidates);
     outcome.Failed.AddRange(review.FailedSections);
     if(!review.Complete){detectorResult.Complete=false;detectorResult.Warning=review.Warning;}
    }
    catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
    catch(Exception ex){detectorResult.Complete=false;detectorResult.Warning="Final local AI review could not finish: "+ex.Message;}
    // The post-processor keeps only spans it can find in the visible text, so the guard findings are put back unchanged.
    detectorResult.Candidates=FindingPostProcessor.Complete(exportedText,detectorResult.Candidates.Where(c=>!FragmentGuard.IsFragment(c))).Concat(fragments).ToList();
    return outcome;
   },cancellationToken);
  }

  private List<Mapping> RegisterResidualFindings(IEnumerable<Candidate> leaks,string sourceFileId,bool approve)
  {
   var mappings=new List<Mapping>();
   if(_project==null)return mappings;
   foreach(var leak in leaks)
   {
    var mapping=_project.EnsureMapping(leak,sourceFileId,approve);
    mapping.Decision=approve&&!mapping.RequiresContextReview?"Replace":"Review";
    mapping.Explanation="Found again during the final protected-copy safety check. "+leak.Explanation;
    mappings.Add(mapping);
   }
   return mappings;
  }

  // ---------------------------------------------------------------- rehydrate (item U10)
  private async void Rehydrate_Click(object s, RoutedEventArgs e)
  {
   if(!Ready()||Busy)return;
   var input=new OpenFileDialog { Filter=SupportedFormats.OpenFilter() }; if(input.ShowDialog()!=true)return;
   string? staged=null;
   _rehydrating=true;UpdateUiState();RehydrateButton.IsEnabled=false;
   try
   {
    var mappings=_project!.Mappings.Where(m=>m.Approved&&!m.Ignored).ToList();
    StatusText.Text="Checking returned tokens before restoring a copy.";
    var preflight=await Task.Run(()=>
    {
     var fingerprint=UiFileFingerprint.Compute(input.FileName);
     var preview=UiRehydratePreview.Analyze(DocumentProcessor.ReadText(input.FileName),mappings);
     if(fingerprint!=UiFileFingerprint.Compute(input.FileName))throw new InvalidOperationException("The returned file changed during preview. Preview it again before restoring.");
     return (Fingerprint:fingerprint,Preview:preview);
    });
    var review=new RehydratePreviewDialog(preflight.Preview){Owner=this};
    review.ShowDialog();if(!review.RestoreRequested)return;
    var outPath=ChooseOutput("Rehydrated",input.FileName); if(outPath==null)return;
    if(SamePath(outPath,input.FileName)){MessageBox.Show("Choose a different file name. Desensitizer never overwrites the returned document.","Choose another location");return;}
    staged=UiOutputPath.Plan(outPath,input.FileName,Path.GetTempPath()).TempPath;
    StatusText.Text="Restoring project tokens in a private copy.";
    var unknown=await Task.Run(()=>
    {
     if(preflight.Fingerprint!=UiFileFingerprint.Compute(input.FileName))throw new InvalidOperationException("The returned file changed after preview. Preview it again before restoring.");
     var count=DocumentProcessor.Rehydrate(input.FileName,staged,mappings);
     if(preflight.Fingerprint!=UiFileFingerprint.Compute(input.FileName))throw new InvalidOperationException("The returned file changed during restoration. No restored copy was released.");
     AtomicOutput.Copy(staged,outPath);return count;
    });
    StatusText.Text=UiMessages.RehydrateSummary(unknown,Path.GetFileName(outPath));
    ShowExportComplete(outPath,UiMessages.RehydrateDetail(unknown));
   }
   catch(Exception ex) { QueueIssue(UiIssues.Failure(IssueKind.RehydrateFailed,ex)); }
   finally{if(staged!=null){try{File.Delete(staged);}catch(IOException){}catch(UnauthorizedAccessException){}}_rehydrating=false;UpdateUiState();}
  }

  // ---------------------------------------------------------------- input while a check runs (item U7), shortcuts (item U9)
  private static bool IsScrollBarTarget(object source)
  {
   var element=source as DependencyObject;
   while(element!=null)
   {
    if(element is ScrollBar)return true;
    element=element is Visual||element is System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(element):element is FrameworkContentElement content?content.Parent:null;
   }
   return false;
  }

  // While a check runs only actions that start work or edit data are blocked. Scroll bars, the wheel,
  // Tab, the arrow keys, copying and the system menu keep working.
  private void Window_PreviewMouseDown(object sender,MouseButtonEventArgs e){if(_isBusy&&!IsStopTarget(e.OriginalSource)&&!IsScrollBarTarget(e.OriginalSource))e.Handled=true;}
  private void Window_PreviewMouseWheel(object sender,MouseWheelEventArgs e){}
  private void Window_PreviewKeyDown(object sender,KeyEventArgs e)
  {
   if(_isBusy)
   {
    var stopFocused=StopAnalysisButton.IsKeyboardFocusWithin;
    if(UiBusyInput.IsAllowed(e.Key==Key.System?e.SystemKey:e.Key,Keyboard.Modifiers,stopFocused)||e.Key==Key.System)return;
    e.Handled=true;StopAnalysisButton.Focus();
    return;
   }
   var key=e.Key==Key.System?e.SystemKey:e.Key;
   var typing=Keyboard.FocusedElement is TextBox||Keyboard.FocusedElement is PasswordBox||Keyboard.FocusedElement is ComboBox{IsEditable:true};
   var gridFocused=MappingsGrid.IsKeyboardFocusWithin;
   switch(UiShortcuts.Decide(key,Keyboard.Modifiers,Busy,_project!=null,gridFocused,typing))
   {
    case UiShortcut.OpenProject:e.Handled=true;if(_project==null)OpenProject_Click(this,new RoutedEventArgs());else AddFiles_Click(this,new RoutedEventArgs());break;
    case UiShortcut.NewProject:e.Handled=true;NewProject_Click(this,new RoutedEventArgs());break;
    case UiShortcut.ScanSelected:if(Selected!=null&&ScanButton.IsEnabled){e.Handled=true;Scan_Click(this,new RoutedEventArgs());}break;
    case UiShortcut.Replace:e.Handled=true;ApplySelectedDecision("Replace");break;
    case UiShortcut.Keep:e.Handled=true;ApplySelectedDecision("Keep as-is");break;
    case UiShortcut.Review:e.Handled=true;ApplySelectedDecision("Review");break;
   }
  }

  // R, K and V on the highlighted rows of the mapping table. Only the highlighted rows change, never the whole filter.
  private void ApplySelectedDecision(string decision)
  {
   var rows=MappingsGrid.SelectedItems.Cast<object>().OfType<Mapping>().ToList();
   if(rows.Count==0||_project==null)return;
   foreach(var mapping in rows){if(decision!="Review")mapping.AcknowledgeContextReview();mapping.Decision=decision;}
   _mappingView?.Refresh();RequestSave();UpdateUiState();
   StatusText.Text=rows.Count+" mapping"+(rows.Count==1?"":"s")+" set to "+decision+".";
  }
 }
}

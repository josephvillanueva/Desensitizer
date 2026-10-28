using System;
using System.IO;
using System.Text.Json;

namespace Desensitizer
{
 public sealed class AppSettings
 {
  public bool AutomaticDeepReview {get;set;}=true;
  public bool TutorialSeen {get;set;}
  public bool CompactTable {get;set;}
  public bool OpenOnReview {get;set;}=true;
  public double TextScalePercent {get;set;}=100;
  public bool AutoReplaceDuringExport {get;set;}
  // What replaces each picture or embedded object in an exported Word file. Empty removes them silently.
  public string ImagePlaceholder {get;set;}=DocumentProcessingOptions.DefaultImagePlaceholder;
  public bool ImagePlaceholderIncludeSize {get;set;}=true;
  // Off by default: picture descriptions can hold names that were never reviewed.
  public bool ImagePlaceholderIncludeAltText {get;set;}
  // Performance. 0 means automatic (decided from this computer's cores and RAM).
  public int LlamaThreads {get;set;}
  public int MaxResidentModels {get;set;}
  public int DetectorConcurrency {get;set;}
  public int WorkerIdleMinutes {get;set;}=5;
 }

 public static class AppSettingsStore
 {
  private static string? folderOverride;
  // Test seam: keep the settings file in another folder. Pass null to restore the default.
  public static void UseFolder(string? folder)=>folderOverride=folder;
  private static string SettingsPath=>Path.Combine(folderOverride??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Desensitizer"),"settings.json");
  public static AppSettings Load()
  {
   AppSettings loaded;
   try{loaded=File.Exists(SettingsPath)?JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))??new AppSettings():new AppSettings();}
   catch{loaded=new AppSettings();}
   PerfConfig.Apply(loaded.LlamaThreads,loaded.MaxResidentModels,loaded.DetectorConcurrency,loaded.WorkerIdleMinutes);
   return loaded;
  }
  public static void Save(AppSettings settings)
  {
   Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
   var temporary=SettingsPath+".tmp";
   File.WriteAllText(temporary,JsonSerializer.Serialize(settings,new JsonSerializerOptions{WriteIndented=true}));
   File.Move(temporary,SettingsPath,true);
   PerfConfig.Apply(settings.LlamaThreads,settings.MaxResidentModels,settings.DetectorConcurrency,settings.WorkerIdleMinutes);
  }
 }
}

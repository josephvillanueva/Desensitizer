using System;

namespace Desensitizer
{
 // The open project window registers one best-effort "write the project now" action here. The crash guard calls it
 // before the app exits after a fatal error, so the last edits are not lost. It must never throw.
 public static class UiCrashFlush
 {
  private static readonly object Gate=new object();
  private static Action? _flush;

  public static void Register(Action flush){lock(Gate)_flush=flush;}
  public static void Unregister(Action flush){lock(Gate){if(_flush==flush)_flush=null;}}

  public static void Run()
  {
   Action? flush;lock(Gate)flush=_flush;
   try{flush?.Invoke();}catch(Exception){}
  }
 }
}

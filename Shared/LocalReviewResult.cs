using System;
using System.Collections.Generic;
using System.Linq;

namespace Desensitizer
{
 public sealed class SectionFailure
 {
  public string Reviewer {get;set;}="";
  // One-based position in the document's review sections.
  public int SectionIndex {get;set;}
  public int SectionCount {get;set;}
  // Fixed diagnostic copy only, never dependency messages or document text.
  public string Reason {get;set;}="";
 }

 // Result of a local model review that does not abort on one bad section.
 // An incomplete result must never be treated as a passed safety check.
 public sealed class LocalReviewResult
 {
  public List<Candidate> Candidates {get;set;}=new List<Candidate>();
  public List<SectionFailure> FailedSections {get;set;}=new List<SectionFailure>();
  // Counts skipped section reviews across all reviewers (sections x reviewers).
  public int SectionsSkippedByTriage {get;set;}
  public bool Complete=>FailedSections.Count==0;
  public string Warning=>Complete?"":DescribeFailures(FailedSections);

  public static string DescribeFailures(IReadOnlyCollection<SectionFailure> failures)
  {
   if(failures.Count==0)return "";
   var groups=failures.GroupBy(failure=>failure.Reviewer).Select(group=>group.Key+" section"+(group.Count()==1?" ":"s ")+string.Join(", ",group.Select(failure=>failure.SectionIndex).Distinct().OrderBy(index=>index))+(group.First().SectionCount>0?" of "+group.First().SectionCount:""));
   var reason=failures.First().Reason;
   return "Local AI review is incomplete. These sections could not be checked: "+string.Join("; ",groups)+". The safety check is not passed and the file must not be released until the review is repeated"+(reason.Length>0?" (first reason: "+reason+")":"")+".";
  }

  public static string SafeReason(Exception error)
  {
   // A length cap cannot make an arbitrary dependency exception private.
   // Retain only exact application-owned messages needed by recovery guidance.
   // Never forward paths, response bodies, InnerException or Exception.Data.
   if(error is InvalidOperationException && error.Message is
    "The review section does not match its table context." or
    "Table review context does not match the review text." or
    "Table review context does not match the extracted document text.")
    return "The review section does not match its table context.";
   if(error is System.IO.InvalidDataException && error.Message=="The local AI response was cut off. The scan is incomplete.")
    return "The local AI response was cut off. The scan is incomplete.";
   return error switch
   {
    OperationCanceledException => "The local AI review was interrupted.",
    System.Text.RegularExpressions.RegexMatchTimeoutException => "A local text check exceeded its time limit.",
    TimeoutException => "The local AI reviewer did not respond within the time limit.",
    System.Net.Http.HttpRequestException => "The local AI reviewer could not be reached or returned an unsuccessful response.",
    System.Text.Json.JsonException or System.IO.InvalidDataException => "The local AI response was invalid or incomplete. The scan is incomplete.",
    System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException => "A required local file is unavailable.",
    UnauthorizedAccessException => "The application could not access a required local file.",
    System.IO.IOException => "A required local file could not be read or written.",
    _ => "The local AI review could not finish. Use privacy-safe support info if retrying does not resolve it."
   };
  }
 }

 // Thrown by the compatibility Find methods when any section failed. The partial
 // result remains available to callers that want to keep completed work.
 public sealed class IncompleteReviewException : Exception
 {
  public IncompleteReviewException(LocalReviewResult result):base(result.Warning){Result=result;}
  public LocalReviewResult Result {get;}
 }
}

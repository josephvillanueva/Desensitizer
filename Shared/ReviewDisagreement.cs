using System;
using System.Collections.Generic;
using System.Linq;

namespace Desensitizer
{
 // Agreement is evidence, not a calibrated probability. Preserve competing
 // interpretations for contextual review instead of silently choosing a winner.
 public static class ReviewDisagreement
 {
  public static List<Candidate> Mark(string document,IEnumerable<Candidate> findings)
  {
   var candidates=findings.ToList();
   foreach(var candidate in candidates)
   {
    if(candidate.RequiresContextReview)continue;
    if(!candidates.Any(other=>!ReferenceEquals(candidate,other)&&Conflicts(candidate,other)))continue;
    candidate.RequiresContextReview=true;
    candidate.Explanation="Review competing categories or partial-name interpretations in source context. "+candidate.Explanation;
   }
   return candidates;
  }

  private static bool Conflicts(Candidate left,Candidate right)
  {
   var a=Project.Normalize(left.Value);var b=Project.Normalize(right.Value);
   if(a.Length==0||b.Length==0)return false;
   var sameCategory=left.Category.Equals(right.Category,StringComparison.OrdinalIgnoreCase);
   if(a==b)return !sameCategory;
   if(!sameCategory)return false;
   if(!left.Category.Equals("PERSON",StringComparison.OrdinalIgnoreCase))return false;
   // A short name embedded in a longer name is not sufficient identity evidence.
   // Complete-name aliases are handled by the conservative shared resolver.
   if(PersonIdentity.AreAliases(left.Value,right.Value))return false;
   return ContainsWholeWords(a,b)||ContainsWholeWords(b,a);
  }

  private static bool ContainsWholeWords(string full,string part)
  {
   var start=full.IndexOf(part,StringComparison.Ordinal);
   while(start>=0)
   {
    var end=start+part.Length;
    if((start==0||!char.IsLetterOrDigit(full[start-1]))&&(end==full.Length||!char.IsLetterOrDigit(full[end])))return true;
    start=full.IndexOf(part,start+1,StringComparison.Ordinal);
   }
   return false;
  }
 }
}

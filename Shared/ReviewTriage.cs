using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer
{
 // Only empty, punctuation-only and fully protected sections can be skipped.
 // Case and number length do not establish anonymity: names can be lowercase,
 // and a short numeric table cell can be an employee or account identifier.
 public static class ReviewTriage
 {
  private static readonly Regex PlaceholderToken=new Regex(@"\[[A-Z_]+_\d{3,}\]",RegexOptions.CultureInvariant);

  public static bool NeedsModelReview(string section)
  {
   if(string.IsNullOrWhiteSpace(section))return false;
   // Export placeholders are already protected and are removed before review.
   var text=PlaceholderToken.Replace(section," ");
   return text.Any(char.IsLetterOrDigit)||text.Contains('@');
  }
 }
}

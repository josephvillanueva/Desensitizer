using System;
using System.Collections.Generic;

namespace Desensitizer
{
 public static class PublicTechnologyPolicy
 {
  private static readonly HashSet<string> Names=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
   "REST API","GraphQL","Apollo","Git","GitHub","React.js","Next.js","Node.js",
   "Tailwind CSS","SCSS","Framer Motion","Terraform","Angular.js","Express.js",
   "Fastify","Lit","Python","SQL","C#","Power BI","Microsoft Excel",
   "Microsoft Word","JavaScript","HTML","CSS","OCR","NEAR Protocol",
   "JPEG","JPG","PNG","GIF","BMP","TIFF","TIF","WEBP","SVG",
   "PDF","DOCX","XLSX","PPTX","CSV","XML","JSON"
  };
  public static bool IsPublicTechnology(string value)=>Names.Contains(value.Trim())||ReviewNoise.IsVersionedPublicTechnology(value);
 }
}

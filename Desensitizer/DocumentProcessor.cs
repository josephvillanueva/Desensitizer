using System;
using System.Linq;
using System.Xml.Linq;

namespace Desensitizer
{
 // The pipeline lives in Shared/DocumentProcessorCore.cs. This file holds the
 // two points where the apps differ.
 public static partial class DocumentProcessor
 {
  // Nothing to remove before sanitising in this app.
  private static void StripVolatileMetadata(XDocument doc){}
  // Attributes the export audit searches for mapped values.
  private static bool IsAuditedAttribute(string partName,XAttribute attribute)=>!IsLayoutAttribute(attribute);
  private static bool IsLayoutAttribute(XAttribute attribute)
  {
   XNamespace word="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
   return attribute.Name==word+"start"&&attribute.Parent?.Name==word+"pgNumType";
  }
 }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Desensitizer;

public static class LocalReviewJson
{
    // Optional local test harness hook. The UI does not enable source diagnostics.
    public static Action<string,string>? GroundingDiagnostic {get;set;}
    public static Action<string>? InvalidResponseDiagnostic {get;set;}
    public const string StrictPrompt="Review ONLY the text in the user message for identifying values. Do not extract anything from instructions, prior hints, examples, or outside this message. Copy literal substrings, with source punctuation and spelling. Return one JSON object with an entities array. Each entity has only a string value and a string category. Allowed categories: PERSON, ORGANIZATION, VENDOR, PROJECT, SYSTEM, PRODUCT, DEPARTMENT, ADDRESS, URL, EMAIL, PHONE, IDENTIFIER. Preserve ordinary role labels, generic business terms, requirements, public technologies, versions, outline numbers, durations, and quantities. A requirement can contain identifying values: extract only the name, company, email, phone or private identifier embedded within it, never the whole requirement. Never invent a value or include explanatory entries. If no identifying values are present, return {\"entities\":[]}.";

    private static readonly (string Field,string Category)[] NuFields=
    {
        ("Personal names","PERSON"),("Private organization names","ORGANIZATION"),
        ("Vendor names","VENDOR"),("Named business projects","PROJECT"),
        ("Named internal systems","SYSTEM"),("Named business products","PRODUCT"),
        ("Named organizational departments","DEPARTMENT"),("Identifying places or addresses","ADDRESS"),
        ("URLs","URL"),("Email addresses","EMAIL"),("Phone numbers","PHONE"),
        ("Private account, employee, transaction or reference identifiers","IDENTIFIER")
    };

    public static string NuExtractInput(string section,ISet<string> categories)
    {
        var schema=NuFields.Where(f=>categories.Contains(f.Category)).ToDictionary(f=>f.Field,f=>new[]{"verbatim-string"});
        return "# Template:\n"+JsonSerializer.Serialize(schema)+"\n# Context:\n"+section;
    }

    public static object NuExtractResponseSchema(ISet<string> categories)
    {
        var fields=NuFields.Where(f=>categories.Contains(f.Category)).ToArray();
        return new{type="object",required=fields.Select(f=>f.Field).ToArray(),additionalProperties=false,
            properties=fields.ToDictionary(f=>f.Field,f=>(object)new{type="array",items=new{type="string"}})};
    }

    public static List<(string Value,string Category)> ReadNuExtract(string output,ISet<string> categories,string sourceSection)
    {
        try
        {
            using var document=JsonDocument.Parse(output,new JsonDocumentOptions{AllowTrailingCommas=true});
            var root=document.RootElement;
            var fields=NuFields.Where(f=>categories.Contains(f.Category)).ToDictionary(f=>f.Field,f=>f.Category,StringComparer.Ordinal);
            if(root.ValueKind!=JsonValueKind.Object)throw Invalid();
            var properties=root.EnumerateObject().ToArray();
            if(properties.Length!=fields.Count||properties.Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=fields.Count)throw Invalid();
            var entities=new List<object>();
            foreach(var property in properties)
            {
                if(!fields.TryGetValue(property.Name,out var category)||property.Value.ValueKind!=JsonValueKind.Array)throw Invalid();
                foreach(var value in property.Value.EnumerateArray())
                {
                    if(value.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(value.GetString()))throw Invalid();
                    entities.Add(new{value=value.GetString(),category});
                }
            }
            return Read(JsonSerializer.Serialize(new{entities}),categories,sourceSection);
        }
        catch(JsonException){InvalidResponseDiagnostic?.Invoke(output);throw new InvalidDataException("The local AI response was invalid. The scan is incomplete.");}
        catch(InvalidDataException){InvalidResponseDiagnostic?.Invoke(output);throw;}
    }

    public static object ResponseSchema(ISet<string> categories)=>new
    {
        type="object",required=new[]{"entities"},additionalProperties=false,
        properties=new{entities=new{type="array",items=new
        {
            type="object",required=new[]{"value","category"},additionalProperties=false,
            properties=new{value=new{type="string"},category=new{type="string",@enum=categories.OrderBy(c=>c,StringComparer.Ordinal).ToArray()}}
        }}}
    };

    public static string GemmaInput(string instructions,string section)
    {
        static string Data(string text)=>Regex.Replace(text,@"<(?:bos|eos|start_of_turn|end_of_turn|start_of_image)>"," ",RegexOptions.IgnoreCase);
        return "<start_of_turn>user\n"+Data(instructions)+"\n\nDOCUMENT:\n"+Data(section)+"<end_of_turn>\n<start_of_turn>model\n";
    }

    public static string? RecoverKnownSourceAcronym(string section,string value,string category)
    {
        if(category is not ("ORGANIZATION" or "VENDOR" or "DEPARTMENT" or "SYSTEM" or "PROJECT" or "IDENTIFIER") &&
            !(category=="PERSON"&&Regex.IsMatch(value,@"\s+(?:Agent|Team)(?:\s+personnel)?$",RegexOptions.IgnoreCase)))return null;
        var head=Regex.Match(value,@"^[A-Z][A-Z0-9]{1,11}(?=\s|$)");
        if(!head.Success)return null;
        var known=DocumentPrivacyPolicy.Apply(section,Array.Empty<Candidate>())
            .Where(c=>c.Category=="ORGANIZATION"&&Regex.IsMatch(c.Value,@"^[A-Z][A-Z0-9]{1,11}$"))
            .Select(c=>c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recovered=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(Match match in Regex.Matches(section,Regex.Escape(value)))
        {
            var start=match.Index;
            while(start>0&&IsAcronymCharacter(section[start-1]))start--;
            var headEnd=match.Index+head.Length;
            while(headEnd<section.Length&&IsAcronymCharacter(section[headEnd]))headEnd++;
            var acronym=section.Substring(start,headEnd-start);
            if(!known.Contains(acronym)||acronym==head.Value)continue;
            var end=value.Length==head.Length?headEnd:match.Index+match.Length;
            var sourceValue=section.Substring(start,end-start);
            if(Regex.IsMatch(section,DocumentPrivacyPolicy.Pattern(sourceValue,true)))recovered.Add(sourceValue);
        }
        if(recovered.Count==0)
        {
            var tail=value.Substring(head.Length);
            foreach(var acronym in known.Where(k=>IsAdjacentSwap(head.Value,k)))
                foreach(Match match in Regex.Matches(section,DocumentPrivacyPolicy.Pattern(acronym+tail,true),RegexOptions.IgnoreCase))recovered.Add(match.Value);
        }
        return recovered.Count==1?recovered.Single():null;
    }

    private static bool IsAdjacentSwap(string a,string b)
    {
        if(a.Length!=b.Length)return false;
        for(var i=0;i<a.Length-1;i++)
            if(a[i]!=b[i] && a[i]==b[i+1] && a[i+1]==b[i] && a.Substring(0,i)==b.Substring(0,i) && a.Substring(i+2)==b.Substring(i+2))return true;
        return false;
    }

    private static bool IsAcronymCharacter(char c)=>c is >= 'A' and <= 'Z' or >= '0' and <= '9';

    public static InvalidDataException Ungrounded(string value,string section)
    {
        GroundingDiagnostic?.Invoke(value,section);
        return new InvalidDataException("The local AI reported a value absent from this section. The scan is incomplete.");
    }
    public static List<(string Value,string Category)> Read(string output,ISet<string> categories,string? sourceSection=null)
    {
        var text=output.Trim();
        var fence=Regex.Match(text,@"^\x60{3}(?:json)?\s*([\s\S]*?)\s*\x60{3}$",RegexOptions.IgnoreCase);
        if(fence.Success)text=fence.Groups[1].Value;
        try
        {
            using var document=JsonDocument.Parse(text,new JsonDocumentOptions{AllowTrailingCommas=true});
            var root=document.RootElement;
            JsonElement entities;
            if(root.ValueKind==JsonValueKind.Object)
            {
                if(root.EnumerateObject().Count()!=1)throw Invalid();
                if(!root.TryGetProperty("entities",out entities))throw Invalid();
            }
            else if(root.ValueKind==JsonValueKind.Array)entities=root;
            else throw Invalid();
            if(entities.ValueKind!=JsonValueKind.Array)throw Invalid();
            var result=new List<(string Value,string Category)>();
            foreach(var entity in entities.EnumerateArray())
            {
                if(entity.ValueKind!=JsonValueKind.Object ||
                    entity.EnumerateObject().Count()!=2 ||
                    entity.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=2 ||
                    !entity.TryGetProperty("value",out var value) || value.ValueKind!=JsonValueKind.String ||
                    !entity.TryGetProperty("category",out var category) || category.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw Invalid();
                var sourceValue=value.GetString()!.Trim();
                var sourceCategory=category.ValueKind==JsonValueKind.String?category.GetString()!.Trim().ToUpperInvariant():"";
                // A copied public field label is not an unclassified private value.
                // Require an actual source occurrence, never discard invented labels.
                if(sourceSection!=null && sourceValue.Length>0 && !categories.Contains(sourceCategory) &&
                    (DocumentPrivacyPolicy.IsGeneric(sourceValue,sourceCategory)||DocumentPrivacyPolicy.IsPublicDocumentControl(sourceSection,sourceValue)||DocumentPrivacyPolicy.IsPublicWorkflowLabel(sourceSection,sourceValue)) &&
                    Regex.IsMatch(sourceSection,DocumentPrivacyPolicy.Pattern(sourceValue,true),RegexOptions.IgnoreCase))continue;
                if(sourceValue.Length==0 || !categories.Contains(sourceCategory))
                {
                    throw Invalid();
                }
                result.Add((sourceValue,sourceCategory));
            }
            return result;
        }
        catch(JsonException){InvalidResponseDiagnostic?.Invoke(output);throw new InvalidDataException("The local AI response was invalid. The scan is incomplete.");}
        catch(InvalidDataException){InvalidResponseDiagnostic?.Invoke(output);throw;}
    }

    private static InvalidDataException Invalid()=>new("The local AI response had an invalid entity structure. A complete review is required.");
}

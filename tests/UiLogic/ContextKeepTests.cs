using System.Text;
using System.Text.Json;
using Desensitizer;

static class ContextKeepTests
{
    public static void Run(CheckFn check, string work)
    {
        const string label="Boarding Pass";
        const string phrase="ticket rules";
        var preferences=new ProjectReviewPreferences();
        check("context feedback: an explicit category and phrase can be saved", preferences.AddContextKeepAsIs(label,"PRODUCT",phrase));
        check("context feedback: blank phrase does not create a project-wide exemption", !preferences.AddContextKeepAsIs(label,"PRODUCT"," "));
        check("context feedback: phrase identical to label is not context", !preferences.AddContextKeepAsIs(label,"PRODUCT",label));
        check("context feedback: punctuation on the label is not extra context", !preferences.AddContextKeepAsIs(label,"PRODUCT",label+".")&&!preferences.AddContextKeepAsIs(label,"PRODUCT","("+label+")"));
        check("context feedback: unsupported identifying categories are refused", new[]{"PERSON","ORGANIZATION","VENDOR","EMAIL","PHONE","ADDRESS","IDENTIFIER"}.All(c=>!preferences.AddContextKeepAsIs(label,c,phrase)));
        check("context feedback: digit and token-shaped labels are refused", !preferences.AddContextKeepAsIs("Console 42","SYSTEM",phrase)&&!preferences.AddContextKeepAsIs("[PRODUCT_001]","PRODUCT",phrase));
        check("context feedback: control characters and overlong hints are refused", !preferences.AddContextKeepAsIs(label,"PRODUCT","ticket\nrules")&&!preferences.AddContextKeepAsIs(label,"PRODUCT",new string('x',201)));
        check("context feedback: same-sentence match is applied", ContextKeepPolicy.CanKeep(label,"PRODUCT","The ticket rules define Boarding Pass as an ordinary field.",preferences));
        check("context feedback: matching is case-insensitive and whitespace-tolerant", ContextKeepPolicy.CanKeep("boarding pass","product","TICKET   RULES define BOARDING  PASS.",preferences));
        check("context feedback: another category is never affected", !ContextKeepPolicy.CanKeep(label,"PERSON","The ticket rules define Boarding Pass.",preferences)&&!ContextKeepPolicy.CanKeep(label,"SYSTEM","The ticket rules define Boarding Pass.",preferences));
        check("context feedback: absent or unknown context retains the suggestion", !ContextKeepPolicy.CanKeep(label,"PRODUCT","Boarding Pass is the named private service.",preferences)&&!ContextKeepPolicy.CanKeep(label,"PRODUCT","",preferences));
        check("context feedback: another sentence cannot lend its phrase", !ContextKeepPolicy.CanKeep(label,"PRODUCT","The ticket rules are public. Boarding Pass is the private service.",preferences));
        check("context feedback: another line cannot lend its phrase", !ContextKeepPolicy.CanKeep(label,"PRODUCT","The ticket rules are public.\nBoarding Pass is the private service.",preferences));
        check("context feedback: another clause cannot lend its phrase", !ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules apply; Boarding Pass is private.",preferences));
        check("context feedback: all section occurrences need their own context", !ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules define Boarding Pass. Boarding Pass is a private integration.",preferences));
        check("context feedback: every separately contextualized occurrence may match", ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules define Boarding Pass. The ticket rules describe Boarding Pass.",preferences));
        check("context feedback: two mentions in one sentence remain ambiguous", !ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules describe Boarding Pass while the private Boarding Pass is different.",preferences));
        check("context feedback: phrase and label require word boundaries", !ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket ruleset defines Boarding Pass.",preferences)&&!ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules define Boarding Passage.",preferences));
        check("context feedback: unknown preference version cannot apply", !ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules define Boarding Pass.",new ProjectReviewPreferences{Version=999,KeepAsIs=preferences.KeepAsIsSnapshot().ToList()}));

        var model=Project.New("Fictional contextual feedback","Violet-Harbor-Lantern-42");
        model.ReviewPreferences=preferences;
        var path=Path.Combine(work,"context-feedback.dsproj");
        ProjectStore.Save(path,model);
        check("context feedback: label and hint never appear in encrypted bytes", !Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(label)&&!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(phrase));
        var reopened=ProjectStore.Load(path,model.Passphrase);
        check("context feedback: category and context survive encrypted save/load", ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules define Boarding Pass.",reopened.ReviewPreferences));
        check("context feedback: removal reverses the preference", reopened.ReviewPreferences.RemoveKeepAsIs(label,phrase)&&!ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules define Boarding Pass.",reopened.ReviewPreferences));
        check("context feedback: another project is unaffected", !ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules define Boarding Pass.",new ProjectReviewPreferences()));

        try
        {
            ProjectScope.Bind(model);
            check("context feedback: production default uses the currently bound project", ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules define Boarding Pass."));
            var mapping=new Mapping{OriginalValue=label,Category="PRODUCT",Approved=true};
            var before=JsonSerializer.Serialize(mapping);
            ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules define Boarding Pass.");
            check("context feedback: applying feedback does not change direct mapping decisions", before==JsonSerializer.Serialize(mapping)&&mapping.Decision=="Replace");
            ProjectScope.Bind(new Project{ReviewPreferences=new ProjectReviewPreferences{Version=999,KeepAsIs=preferences.KeepAsIsSnapshot().ToList()}});
            check("context feedback: unsupported bound-project version cannot apply", !ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules define Boarding Pass."));
        }
        finally{ProjectScope.Unbind();}
        check("context feedback: closing the project removes its active feedback", !ContextKeepPolicy.CanKeep(label,"PRODUCT","ticket rules define Boarding Pass."));
    }
}

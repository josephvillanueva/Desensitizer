using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Desensitizer;

internal static class Program
{
    private const string Secret="FICTIONAL_PRIVATE_SENTINEL";
    private static int passed,failed;
    private static void Check(string name,bool result)
    {
        Console.WriteLine((result?"PASS ":"FAIL ")+name);
        if(result)passed++;else failed++;
    }
    private static int Main()
    {
        var errors=new Exception[]
        {
            new Exception(Secret),new InvalidOperationException(Secret),new InvalidDataException(Secret),
            new JsonException(Secret),new HttpRequestException(Secret),new IOException(Secret),
            new FileNotFoundException(Secret,Secret+".gguf"),new DirectoryNotFoundException(Secret),
            new UnauthorizedAccessException(Secret),new TimeoutException(Secret),
            new OperationCanceledException(Secret),new RegexMatchTimeoutException(Secret,Secret,TimeSpan.FromSeconds(1))
        };
        foreach(var error in errors)
        {
            error.Data["source"]=Secret;
            var reason=LocalReviewResult.SafeReason(error);
            Check("reason hides confidential payload: "+error.GetType().Name,!reason.Contains(Secret)&&reason.Length>0&&reason.Length<=220);
            var outcome=new LocalReviewResult();
            outcome.FailedSections.Add(new SectionFailure{Reviewer="Qwen",SectionIndex=2,SectionCount=3,Reason=reason});
            Check("incomplete review remains blocked and warning private: "+error.GetType().Name,
                !outcome.Complete&&!outcome.Warning.Contains(Secret)&&outcome.Warning.Contains("must not be released")&&outcome.Warning.Contains("Qwen section 2 of 3"));
        }
        foreach(var message in new[]{"The review section does not match its table context.","Table review context does not match the review text.","Table review context does not match the extracted document text."})
            Check("known table error retains recovery classification",LocalReviewResult.SafeReason(new InvalidOperationException(message))=="The review section does not match its table context.");
        Check("table message plus private text is not forwarded",!LocalReviewResult.SafeReason(new InvalidOperationException("The review section does not match its table context. "+Secret)).Contains("table context"));
        Check("truncation remains actionable",LocalReviewResult.SafeReason(new InvalidDataException("The local AI response was cut off. The scan is incomplete.")).Contains("cut off"));
        LocalReviewJson.InvalidResponseDiagnostic=null;LocalReviewJson.GroundingDiagnostic=null;
#if AI_APP
        foreach(var reviewer in new[]{"Qwen","NuExtract","Gemma"})TestTruncatedResponse(reviewer);
#else
        TestTruncatedResponse("Qwen");
#endif
        EnvelopeReliabilityTests.Run(Check);
        Console.WriteLine($"Checks: {passed} passed, {failed} failed. Fake HTTP responses only, no live models.");
        return failed==0?0:1;
    }
    private static void TestTruncatedResponse(string reviewer)
    {
        var envelope=reviewer=="Gemma"
            ?JsonSerializer.Serialize(new{content=Secret,truncated=true})
            :JsonSerializer.Serialize(new{choices=new[]{new{finish_reason="length",message=new{content=Secret}}}});
        using var client=new HttpClient(new FakeResponse(envelope)){BaseAddress=new Uri("http://127.0.0.1:1")};
        var method=typeof(LocalAiReview).GetMethod("ReviewSectionOnce",BindingFlags.NonPublic|BindingFlags.Static)!;
        object?[] args;
#if AI_APP
        var definitionType=typeof(LocalAiReview).GetNestedType("ReviewerDefinition",BindingFlags.NonPublic)!;
        var definition=Activator.CreateInstance(definitionType,true)!;
        definitionType.GetProperty("Name")!.SetValue(definition,reviewer);
        args=new object?[]{client,definition,Secret,CancellationToken.None,"Review identifying data."};
#else
        args=new object?[]{client,Secret,CancellationToken.None,"Review identifying data."};
#endif
        Exception? failure=null;
        try{method.Invoke(null,args);}catch(TargetInvocationException caught){failure=caught.InnerException;}
        Check(reviewer+" truncation fails closed",failure is InvalidDataException);
        Check(reviewer+" exception retains no source or output",failure!=null&&!failure.ToString().Contains(Secret)&&failure.Data.Count==0&&failure.InnerException==null);
        Check(reviewer+" safe warning retains no source",failure!=null&&!LocalReviewResult.SafeReason(failure).Contains(Secret));
    }
    private sealed class FakeResponse(string body):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            =>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)});
    }
}

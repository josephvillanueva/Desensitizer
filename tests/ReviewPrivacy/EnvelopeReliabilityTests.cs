using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Desensitizer;

internal static class EnvelopeReliabilityTests
{
    private const string Secret = "FICTIONAL_ENVELOPE_PRIVATE_SENTINEL";
    private const string Section = "Elena Vale approves refunds within 30 days.";
    private static readonly string[] NativeFields =
    {
        "Personal names", "Private organization names", "Vendor names", "Named business projects",
        "Named internal systems", "Named business products", "Named organizational departments",
        "Identifying places or addresses", "URLs", "Email addresses", "Phone numbers",
        "Private account, employee, transaction or reference identifiers"
    };

    public static void Run(Action<string, bool> check)
    {
        ParserShapes(check);
#if AI_APP
        foreach (var reviewer in new[] { "Qwen", "NuExtract", "Gemma" }) RetryResponses(check, reviewer);
#else
        RetryResponses(check, "Qwen");
#endif
        ExactCache(check);
    }

    private static void ParserShapes(Action<string, bool> check)
    {
        var chatInvalid = new[]
        {
            Secret + "{", "null", "[]", "{}", "{\"choices\":null}", "{\"choices\":[]}",
            "{\"choices\":[{},{}]}", "{\"choices\":[null]}", "{\"choices\":[{\"message\":{}}]}",
            "{\"choices\":[{\"message\":{\"content\":null}}]}",
            "{\"choices\":[{\"message\":{\"content\":1}}]}",
            "{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":null}]}",
            "{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"tool_calls\"}]}",
            "{\"choices\":[],\"choices\":[{\"message\":{\"content\":\"{}\"}}]}",
            "{\"choices\":[{\"message\":{\"content\":\"{}\",\"content\":\"{}\"}}]}"
        };
        for (var i = 0; i < chatInvalid.Length; i++)
        {
            var failure = Catch(() => LocalReviewEnvelope.ReadChat(chatInvalid[i]));
            check("chat envelope rejects malformed shape privately " + i, PrivateFailure(failure));
        }
        var nativeInvalid = new[]
        {
            Secret + "{", "null", "[]", "{}", "{\"content\":null}", "{\"content\":42}",
            "{\"content\":\"{}\",\"truncated\":\"false\"}",
            "{\"content\":\"{}\",\"stop_type\":false}",
            "{\"content\":\"{}\",\"content\":\"{}\"}"
        };
        for (var i = 0; i < nativeInvalid.Length; i++)
        {
            var failure = Catch(() => LocalReviewEnvelope.ReadCompletion(nativeInvalid[i]));
            check("native envelope rejects malformed shape privately " + i, PrivateFailure(failure));
        }
        const string content = "{\"entities\":[]}";
        check("chat envelope preserves entity JSON verbatim", LocalReviewEnvelope.ReadChat(Chat(content)) == content);
        check("chat envelope permits ordinary transport metadata", LocalReviewEnvelope.ReadChat(JsonSerializer.Serialize(new { id = "fake", choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } }, usage = new { total_tokens = 5 } })) == content);
        check("native envelope preserves entity JSON verbatim", LocalReviewEnvelope.ReadCompletion(JsonSerializer.Serialize(new { content, stop_type = "eos", truncated = false })) == content);
        check("chat truncation remains actionable and private", Catch(() => LocalReviewEnvelope.ReadChat(Chat(Secret, "length"))) is InvalidDataException c && PrivateFailure(c) && c.Message.Contains("cut off"));
        check("native truncation remains actionable and private", Catch(() => LocalReviewEnvelope.ReadCompletion(JsonSerializer.Serialize(new { content = Secret, truncated = true }))) is InvalidDataException n && PrivateFailure(n) && n.Message.Contains("cut off"));
    }

    private static void RetryResponses(Action<string, bool> check, string reviewer)
    {
        using (var handler = new SequenceHandler(i => i == 0 ? Secret + "{" : Valid(reviewer)))
        using (var client = Client(handler))
        {
            List<Candidate>? result = null;
            var failure = Catch(() => result = Review(client, reviewer));
            check(reviewer + " malformed envelope retries once and keeps exact findings", failure == null && handler.Calls == 2 && result?.SingleOrDefault()?.Value == "Elena Vale");
            check(reviewer + " successful retry preserves model temperature and token limit", handler.Requests.All(UnchangedSettings));
            if (reviewer == "NuExtract")
                check("NuExtract retry repeats the unchanged native request", handler.Requests.Count == 2 && handler.Requests[0] == handler.Requests[1] && handler.Requests.All(NativeRequest));
        }
        using (var handler = new SequenceHandler(_ => Secret + "{"))
        using (var client = Client(handler))
        {
            var failure = Catch(() => Review(client, reviewer));
            check(reviewer + " repeated malformed envelopes fail after two short-section attempts", PrivateFailure(failure) && handler.Calls == 2);
            var outcome = new LocalReviewResult();
            if (failure != null) outcome.FailedSections.Add(new SectionFailure { Reviewer = reviewer, SectionIndex = 1, SectionCount = 1, Reason = LocalReviewResult.SafeReason(failure) });
            check(reviewer + " repeated invalid response remains incomplete and release-blocking", !outcome.Complete && outcome.Warning.Contains("must not be released") && !outcome.Warning.Contains(Secret));
        }
        using (var handler = new SequenceHandler(i => i == 0 ? Truncated(reviewer) : Valid(reviewer)))
        using (var client = Client(handler))
        {
            List<Candidate>? result = null;
            var failure = Catch(() => result = Review(client, reviewer));
            check(reviewer + " truncated envelope retries without accepting partial content", failure == null && handler.Calls == 2 && result?.SingleOrDefault()?.Value == "Elena Vale");
        }
        using (var handler = new SequenceHandler(_ => Valid(reviewer, "Invented Person")))
        using (var client = Client(handler))
        {
            var failure = Catch(() => Review(client, reviewer));
            check(reviewer + " absent source name is never silently dropped into a passed review", PrivateFailure(failure) && handler.Calls == 2);
        }
        using (var handler = new SequenceHandler(_ => Valid(reviewer), HttpStatusCode.ServiceUnavailable))
        using (var client = Client(handler))
        {
            var failure = Catch(() => Review(client, reviewer));
            check(reviewer + " transport failures are not converted to empty findings", failure is HttpRequestException && handler.Calls == 1);
        }
        using (var handler = new SequenceHandler(_ => Valid(reviewer)))
        using (var client = Client(handler))
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var failure = Catch(() => Review(client, reviewer, cancelled.Token));
            check(reviewer + " cancellation is not retried or swallowed", failure is OperationCanceledException && handler.Calls == 0);
        }
    }

    private static void ExactCache(Action<string, bool> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ReviewEnvelopeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var runtime = Path.Combine(directory, "fake-runtime.exe");
        var model = Path.Combine(directory, "fake-model.gguf");
        File.WriteAllText(runtime, "not executed");
        File.WriteAllText(model, "not loaded");
        var project = Project.New("Fictional cache policy", "not saved");
        ProjectScope.Bind(project);
        try
        {
            using var cache = new ExportReviewCache();
            var calls = 0;
            List<Candidate> Found() { calls++; return new() { new() { Value = "Elena Vale", Category = "PERSON" } }; }
            string Config(string context = "context-A") => ExportReviewCache.Configuration(runtime, model, "reviewer-Qwen|" + context);
            var initial = Config();
            cache.Review(initial, Section, Found, default);
            cache.Review(initial, Section, Found, default);
            check("exact cache reuses only a successful identical section and configuration", calls == 1 && cache.Hits == 1);
            cache.Review(Config("different-table-context"), Section, Found, default);
            check("exact cache changes table/prompt context identity", calls == 2);
            cache.Review(initial, Section + " Approval is required.", Found, default);
            check("exact cache changes source section identity", calls == 3);
            project.ReviewPreferences.AddGenericTerm("Fictional Portal", new[] { "SYSTEM" });
            var changedPolicy = Config();
            cache.Review(changedPolicy, Section, Found, default);
            check("exact cache changes project generic policy identity", changedPolicy != initial && calls == 4);
            File.WriteAllText(model, "different model file stamp");
            var changedModel = Config();
            cache.Review(changedModel, Section, Found, default);
            check("exact cache changes model stamp identity", changedModel != changedPolicy && calls == 5);
            check("exact cache structurally separates context and section boundaries", ExportReviewCache.KeyFor("A\u0001B", "C") != ExportReviewCache.KeyFor("A", "B\u0001C"));

            using var failedCache = new ExportReviewCache();
            var attempts = 0;
            var failure = Catch(() => failedCache.Review("failure", Section, () => { attempts++; throw new InvalidDataException("fixed failure"); }, default));
            check("exact cache never stores an exception as a successful empty result", failure is InvalidDataException && failedCache.Count == 0);
            failedCache.Review("failure", Section, () => { attempts++; return Found(); }, default);
            check("exact cache reruns an unsuccessful identical request", attempts == 2 && failedCache.Hits == 0 && failedCache.Count == 1);
            var ungrounded = Catch(() => failedCache.Review("ungrounded", Section, () => new() { new() { Value = "Invented Person", Category = "PERSON" } }, default));
            check("exact cache refuses ungrounded findings", ungrounded is InvalidDataException && failedCache.Count == 1);
            using var cancelled = new CancellationTokenSource();
            var cancellation = Catch(() => failedCache.Review("cancelled", Section, () => { cancelled.Cancel(); return Found(); }, cancelled.Token));
            check("exact cache does not store a cancelled review", cancellation is OperationCanceledException && failedCache.Count == 1);
            var resource = new DisposalProbe();
            var owned = new ExportReviewCache();
            owned.GetOrAttach("fake-host-resource", () => resource);
            owned.Dispose();
            check("owned exact cache disposes attached host resources", resource.Disposed);
            check("disposed exact cache cannot retain or reuse source findings", Catch(() => owned.Review("x", Section, Found, default)) is ObjectDisposedException);
        }
        finally
        {
            ProjectScope.Unbind();
            Directory.Delete(directory, true);
        }
    }

    private static string Valid(string reviewer, string name = "Elena Vale")
    {
        string content;
        if (reviewer == "NuExtract")
        {
            var fields = NativeFields.ToDictionary(field => field, _ => Array.Empty<string>());
            fields["Personal names"] = new[] { name };
            content = JsonSerializer.Serialize(fields);
        }
        else content = JsonSerializer.Serialize(new { entities = new[] { new { value = name, category = "PERSON" } } });
        return reviewer == "Gemma" ? JsonSerializer.Serialize(new { content, stop_type = "eos", truncated = false }) : Chat(content);
    }

    private static string Truncated(string reviewer) => reviewer == "Gemma"
        ? JsonSerializer.Serialize(new { content = Secret, truncated = true }) : Chat(Secret, "length");
    private static string Chat(string content, string reason = "stop")
        => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = reason } } });
    private static HttpClient Client(SequenceHandler handler) => new(handler) { BaseAddress = new Uri("http://127.0.0.1:1") };

    private static List<Candidate> Review(HttpClient client, string reviewer, CancellationToken token = default)
    {
        var method = typeof(LocalAiReview).GetMethod("ReviewSection", BindingFlags.NonPublic | BindingFlags.Static)!;
#if AI_APP
        var definitionType = typeof(LocalAiReview).GetNestedType("ReviewerDefinition", BindingFlags.NonPublic)!;
        var definition = Activator.CreateInstance(definitionType, true)!;
        definitionType.GetProperty("Name")!.SetValue(definition, reviewer);
        definitionType.GetProperty("Prompt")!.SetValue(definition, "Review exact identifying spans.");
        definitionType.GetProperty("Source")!.SetValue(definition, "AI " + reviewer);
        object?[] args = { client, definition, Section, token, "Business context.", 0 };
#else
        object?[] args = { client, Section, token, "Review exact identifying spans.", 0 };
#endif
        try { return (List<Candidate>)method.Invoke(null, args)!; }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static bool PrivateFailure(Exception? failure) => failure is InvalidDataException && failure.InnerException == null
        && failure.Data.Count == 0 && !failure.ToString().Contains(Secret);
    private static bool UnchangedSettings(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return root.GetProperty("temperature").GetDouble() == 0
            && (root.TryGetProperty("max_tokens", out var limit) ? limit.GetInt32() : root.GetProperty("n_predict").GetInt32()) == 1600;
    }
    private static bool NativeRequest(string body)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");
        return messages.GetArrayLength() == 1 && messages[0].GetProperty("role").GetString() == "user"
            && messages[0].GetProperty("content").GetString()!.StartsWith("# Template:");
    }
    private sealed class DisposalProbe : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }
    private sealed class SequenceHandler(Func<int, string> response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(await request.Content!.ReadAsStringAsync(token));
            var body = response(Calls++);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}

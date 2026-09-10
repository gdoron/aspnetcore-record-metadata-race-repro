using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AspNetCoreRecordMetadataRaceRepro;

public readonly record struct ReproResult(int ColdStartsWithFailures, int PermanentlyBroken, int Transient);

/// <summary>
/// Each iteration is one simulated pod cold start: a brand new <see cref="WebApplication"/> (and so a
/// brand new, empty model-metadata cache) hit by N concurrent first requests released at the same
/// instant, exactly as a freshly scheduled replica is when it enters a load balancer's rotation.
/// </summary>
public static class ColdStartHarness {
    private const int PostBurstProbes = 20;

    public static ReproResult Run(int coldStarts, int threads, bool applyWorkaround) {
        int coldStartsWithFailures = 0, permanentlyBroken = 0, transient = 0;

        for (var attempt = 0; attempt < coldStarts; attempt++) {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddControllers();
            builder.Services.AddSingleton<IModelMetadataProvider, RecordingModelMetadataProvider>();
            using var app = builder.Build();
            var validator = app.Services.GetRequiredService<IObjectModelValidator>();
            var metadataProvider =
                (RecordingModelMetadataProvider)app.Services.GetRequiredService<IModelMetadataProvider>();

            if (applyWorkaround) MetadataWarmUp.Warm(metadataProvider, typeof(ProcessConversationRequest));

            var burstFailures = RunConcurrentFirstRequests(validator, app.Services, threads);
            if (burstFailures == 0) continue;

            coldStartsWithFailures++;

            // Let the burst fully drain, then probe serially. If the cached mapping was latched
            // incomplete, these fail too - and will keep failing for the life of the process.
            Thread.Sleep(50);
            var postBurstFailures = 0;
            for (var probe = 0; probe < PostBurstProbes; probe++)
                if (!TryValidate(validator, app.Services, 9000 + probe))
                    postBurstFailures++;

            if (postBurstFailures == PostBurstProbes) {
                permanentlyBroken++;
                Console.WriteLine($"cold start {attempt}: PERMANENTLY BROKEN - "
                                  + $"{burstFailures}/{threads} concurrent, {postBurstFailures}/{PostBurstProbes} serial afterwards");
                MetadataInspector.Dump(metadataProvider, typeof(ProcessConversationRequest));
            }
            else {
                transient++;
                Console.WriteLine($"cold start {attempt}: transient - "
                                  + $"{burstFailures}/{threads} concurrent failed, recovered afterwards");
            }
        }

        return new ReproResult(coldStartsWithFailures, permanentlyBroken, transient);
    }

    private static int RunConcurrentFirstRequests(IObjectModelValidator validator, IServiceProvider services,
        int threads) {
        using var startingGate = new ManualResetEventSlim(initialState: false);
        var failures = 0;
        var requests = new Task[threads];

        for (var index = 0; index < threads; index++) {
            var seed = index;
            requests[index] = Task.Run(() => {
                startingGate.Wait();
                if (!TryValidate(validator, services, seed)) Interlocked.Increment(ref failures);
            });
        }

        startingGate.Set();
        Task.WaitAll(requests);
        return failures;
    }

    private static bool TryValidate(IObjectModelValidator validator, IServiceProvider services, int seed) {
        var actionContext = new ActionContext(
            new DefaultHttpContext { RequestServices = services },
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());

        try {
            validator.Validate(actionContext, validationState: null, prefix: "", model: BuildRequest(seed));
            return true;
        }
        catch (InvalidOperationException) {
            // "No property found that maps to constructor parameter '{x}' for type '{y}'."
            return false;
        }
    }

    private static ProcessConversationRequest BuildRequest(int seed) =>
        new(
            new Conversation(
                Messages: [new Message("user", $"message-{seed}")],
                System: ["system"],
                Tools: new ToolSet([new ToolDefinition($"tool-{seed}", "description")])),
            new RequestMetadata($"request-{seed}", DateTimeOffset.UtcNow));
}

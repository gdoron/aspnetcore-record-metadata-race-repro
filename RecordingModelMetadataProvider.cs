using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;

namespace AspNetCoreRecordMetadataRaceRepro;

/// <summary>
/// Records every <see cref="ModelMetadata"/> the framework creates, including the ones discarded by
/// the provider's deliberate "safe race-condition" when two threads build the same property set
/// concurrently. Without this, a poisoned instance that is no longer reachable from the provider's
/// current object graph is invisible to inspection.
/// </summary>
/// <remarks>
/// <c>CreateModelMetadata</c> is a documented extension point and this override adds no
/// synchronization, so it does not perturb the race being measured.
/// </remarks>
public sealed class RecordingModelMetadataProvider(
    ICompositeMetadataDetailsProvider detailsProvider,
    IOptions<MvcOptions> optionsAccessor)
    : DefaultModelMetadataProvider(detailsProvider, optionsAccessor) {
    private readonly ConcurrentBag<ModelMetadata> _created = [];

    public IReadOnlyCollection<ModelMetadata> Created => _created;

    protected override ModelMetadata CreateModelMetadata(DefaultMetadataDetails entry) {
        var metadata = base.CreateModelMetadata(entry);
        _created.Add(metadata);
        return metadata;
    }
}

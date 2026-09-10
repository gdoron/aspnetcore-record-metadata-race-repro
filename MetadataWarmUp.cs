using System.Reflection;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AspNetCoreRecordMetadataRaceRepro;

/// <summary>
/// The workaround an application can apply today: force every record-type constructor-to-property
/// mapping to be computed on one thread at startup, before the server accepts traffic, so no two
/// requests can ever race to build it.
/// </summary>
public static class MetadataWarmUp {
    private static readonly PropertyInfo? BoundConstructorParameterMapping = typeof(ModelMetadata)
        .GetProperty("BoundConstructorParameterMapping", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Warm(IModelMetadataProvider provider, params Type[] boundRequestTypes) {
        // Reading this internal property is the only way to force the mapping without also
        // constructing an instance of every bound request type. That it takes reflection at all is
        // the reason this belongs in the framework rather than in every application.
        if (BoundConstructorParameterMapping == null) return;

        foreach (var requestType in boundRequestTypes)
        foreach (var metadata in MetadataWalker.Walk(provider.GetMetadataForType(requestType)))
            if (metadata.BoundConstructor != null)
                BoundConstructorParameterMapping.GetValue(metadata);
    }
}

using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AspNetCoreRecordMetadataRaceRepro;

public static class MetadataWalker {
    /// <summary>
    /// Every <see cref="ModelMetadata"/> node reachable from <paramref name="root"/> by the same
    /// route the validator takes: properties, collection elements, and constructor parameters.
    /// </summary>
    public static IEnumerable<ModelMetadata> Walk(ModelMetadata root) {
        // ModelMetadata equality is by identity, so this both de-duplicates and terminates on cycles.
        var seen = new HashSet<ModelMetadata>();
        var pending = new Stack<ModelMetadata>([root]);

        while (pending.Count > 0) {
            var metadata = pending.Pop();
            if (!seen.Add(metadata)) continue;
            yield return metadata;

            foreach (var property in metadata.Properties) pending.Push(property);
            if (metadata.ElementMetadata is { } element) pending.Push(element);
            foreach (var parameter in metadata.BoundConstructor?.BoundConstructorParameters ?? [])
                pending.Push(parameter);
        }
    }
}

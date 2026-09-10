using System.Reflection;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AspNetCoreRecordMetadataRaceRepro;

/// <summary>
/// Reads the private state behind the cached record-type mapping so a broken process can be
/// described precisely rather than just observed to throw.
/// </summary>
public static class MetadataInspector {
    private static readonly FieldInfo? DetailsCalculated = Field("_recordTypeConstructorDetailsCalculated");
    private static readonly FieldInfo? ParameterMapping = Field("_parameterMapping");
    private static readonly FieldInfo? CachedHashCode = Field("_hashCode");

    private static readonly PropertyInfo? Identity = typeof(ModelMetadata)
        .GetProperty("Identity", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Dump(RecordingModelMetadataProvider provider, Type rootType) {
        if (DetailsCalculated == null || ParameterMapping == null) {
            Console.WriteLine("    (private ModelMetadata fields not found on this runtime)");
            return;
        }

        var reachable = MetadataWalker.Walk(provider.GetMetadataForType(rootType))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var reported = false;

        foreach (var metadata in provider.Created) {
            if (metadata.BoundConstructor?.BoundConstructorParameters is not { } parameters) continue;
            if (!(bool)DetailsCalculated.GetValue(metadata)!) continue;
            if (ParameterMapping.GetValue(metadata) is not IReadOnlyDictionary<ModelMetadata, ModelMetadata> mapping)
                continue;

            var unresolvable = parameters.Where(parameter => !mapping.ContainsKey(parameter)).ToList();
            if (mapping.Count == parameters.Count && unresolvable.Count == 0) continue;

            reported = true;
            Console.WriteLine($"    BROKEN {metadata.ModelType.Name} ({metadata.MetadataKind}), "
                              + $"reachableFromProvider={reachable.Contains(metadata)}: "
                              + $"{mapping.Count} entries for {parameters.Count} constructor parameters, "
                              + $"{unresolvable.Count} of them not findable in their own mapping");

            foreach (var parameter in unresolvable)
                Console.WriteLine($"        parameter '{parameter.ParameterName}': "
                                  + $"cachedHash={Describe(CachedHashCode?.GetValue(parameter))}, "
                                  + $"identityHash={IdentityHash(parameter)}, "
                                  + $"GetHashCode()={parameter.GetHashCode()}, "
                                  + $"presentByEquality={mapping.Keys.Any(key => key.Equals(parameter))}");

            DumpDictionaryEntries(mapping);
        }

        if (!reported) Console.WriteLine("    (no broken mapping found among recorded metadata)");
    }

    /// <summary>
    /// Prints the hash each key was actually filed under against the hash it reports now. A
    /// mismatch means the key was inserted while <see cref="ModelMetadata.GetHashCode"/> returned a
    /// different value than it does today, which is what makes the entry unreachable by lookup.
    /// </summary>
    private static void DumpDictionaryEntries(IReadOnlyDictionary<ModelMetadata, ModelMetadata> mapping) {
        var entries = mapping.GetType().GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(mapping) as Array;
        if (entries == null) {
            Console.WriteLine("        (dictionary internals unavailable)");
            return;
        }

        const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var entryType = entries.GetType().GetElementType()!;
        var hashCodeField = entryType.GetField("hashCode", AnyInstance);
        var keyField = entryType.GetField("key", AnyInstance);
        if (hashCodeField == null || keyField == null) {
            Console.WriteLine("        (dictionary entry layout unavailable)");
            return;
        }

        for (var index = 0; index < mapping.Count; index++) {
            var entry = entries.GetValue(index);
            if (entry == null || keyField.GetValue(entry) is not ModelMetadata key) continue;

            var storedHash = unchecked((int)(uint)hashCodeField.GetValue(entry)!);
            var currentHash = key.GetHashCode();
            var verdict = storedHash == currentHash ? "ok" : "MISMATCH - unreachable by lookup";
            Console.WriteLine($"        stored key '{key.ParameterName ?? key.Name}': "
                              + $"filedUnderHash={storedHash}, reportsHashNow={currentHash} -> {verdict}");
        }
    }

    private static string IdentityHash(ModelMetadata metadata) =>
        Identity?.GetValue(metadata)?.GetHashCode().ToString() ?? "?";

    private static string Describe(object? cachedHash) => cachedHash?.ToString() ?? "null";

    private static FieldInfo? Field(string name) =>
        typeof(ModelMetadata).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
}

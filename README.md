# ASP.NET Core: `ModelMetadata.GetHashCode()` can return 0 under a race, permanently corrupting the cached record-type mapping

Standalone reproduction and root-cause analysis for
**[dotnet/aspnetcore#59384](https://github.com/dotnet/aspnetcore/issues/59384)**
(open since Dec 2024, Backlog, no repro attached upstream).

No dependencies beyond `Microsoft.AspNetCore.App`. Copy the folder anywhere and run it.

```bash
dotnet run -c Release                        # reproduce
dotnet run -c Release -- --warm-up           # same run with the application-side workaround applied
dotnet run -c Release -- --cold-starts 5000 --threads 96
```

Exit code is `1` if any cold start was left permanently broken, `0` otherwise.

## Symptom

A process serving `[ApiController]` endpoints whose request contracts are positional records starts
failing model validation with:

```
System.InvalidOperationException: No property found that maps to constructor parameter 'Metadata'
for type 'ProcessConversationRequest'. Validation requires that each bound parameter of a record
type's primary constructor must have a property to read the value.
```

The record is well-formed, the property exists, and the same binary serves the same request shape
correctly on every other instance. Restarting the affected instance clears it; nothing else does.

We hit this in production: one pod out of a fleet threw 1352 of these in six minutes, across nine
tenants and every request that touched the type, from twenty seconds after the pod came up until it
left rotation. No other pod on the same build was ever affected. That matches the report in #59384 —
one instance of a load-balanced pair, restart resolves it.

## Root cause

`ModelMetadata.GetHashCode()` caches its hash in an `int?`:

```csharp
private int? _hashCode;

public override int GetHashCode()
{
    // Normally caching the hashcode would be dangerous, but Identity is deeply immutable so this is safe.
    if (_hashCode == null)
    {
        _hashCode = Identity.GetHashCode();
    }

    return _hashCode.Value;
}
```

The comment is correct that `Identity` is immutable, but that is not the hazard. `int?` is
`Nullable<int>`, a **two-field struct** (`bool hasValue`, `int value`), and assigning it is **not an
atomic write**. A second thread can observe `hasValue == true` while `value` is still `0`, and
`GetHashCode()` then returns **0** instead of the real hash. It is a transient wrong answer — the
field settles to the correct value — but the damage is not transient, because that wrong answer gets
baked into a dictionary that is cached for the lifetime of the process.

`ModelMetadata.CalculateRecordTypeConstructorDetails()` builds exactly such a dictionary, lazily,
with no synchronization, on first use — i.e. on the first requests a freshly started process
receives, which is precisely when several threads are racing to compute these hashes for the first
time:

```csharp
var parameterMapping = new Dictionary<ModelMetadata, ModelMetadata>();
// ...
parameterMapping[parameter] = property;   // filed under GetHashCode(), which may be 0
// ...
_recordTypeConstructorDetailsCalculated = true;
_parameterMapping = parameterMapping;     // cached for the lifetime of the process
```

If a key is inserted while its `GetHashCode()` returns 0, the entry is filed in the bucket for hash
0. Once `_hashCode` settles, the key reports its real hash and the entry becomes **unreachable by
lookup** — present in the dictionary, findable by a linear scan, invisible to `TryGetValue`. Every
subsequent request then reaches
`DefaultComplexObjectValidationStrategy.Enumerator.MoveNext()`, misses, and throws:

```csharp
if (!_modelMetadata.BoundConstructorParameterMapping.TryGetValue(parameter, out var property))
{
    throw new InvalidOperationException(
        Resources.FormatValidationStrategy_MappedPropertyNotFound(parameterName, _modelMetadata.ModelType));
}
```

Nothing ever rebuilds the mapping, so the process is broken until it is replaced. This is why the
error is 0% or 100% per instance and never anything in between.

### Evidence from the repro

The harness reflects into the cached dictionary and prints the hash each key was actually filed
under against the hash it reports now:

```
cold start 3816: PERMANENTLY BROKEN - 64/64 concurrent, 20/20 serial afterwards
    BROKEN ProcessConversationRequest (Type), reachableFromProvider=True: 2 entries for 2 constructor parameters, 1 of them not findable in their own mapping
        parameter 'Metadata': cachedHash=2051929668, identityHash=2051929668, GetHashCode()=2051929668, presentByEquality=True
        stored key 'Conversation': filedUnderHash=-1352266258, reportsHashNow=-1352266258 -> ok
        stored key 'Metadata':     filedUnderHash=0,           reportsHashNow=2051929668 -> MISMATCH - unreachable by lookup
```

Note what this rules out. The mapping is **not** incomplete — it has an entry per constructor
parameter. The key **is** present and compares equal (`presentByEquality=True`). Every hash agrees
*now*. The only anomaly is the hash the entry was filed under: `0`.

## Reproducing

Each iteration is one simulated cold start: a fresh `WebApplication` (so a fresh, empty
model-metadata cache) hit by N concurrent first requests released from a starting gate at the same
instant — what a newly scheduled replica sees when it enters a load balancer's rotation. After the
burst drains, the harness probes the same process serially to separate two outcomes:

| Outcome | What it looks like |
| --- | --- |
| **Transient** | A few concurrent requests fail while the mapping is being built; the process then works correctly. |
| **Permanently broken** | *Every* request in the burst fails, and every serial request afterwards fails too, for the remaining life of the process. |

Measured on .NET 10.0.8 / arm64, 2000–4000 cold starts x 64 threads: roughly **0.05–0.35% of cold
starts end up permanently broken**, plus a similar rate of transient ones. Rare per cold start,
certain per request once it happens.

Expect this to be far easier to hit on arm64 (Apple Silicon, AWS Graviton, Ampere) than on x64: the
weaker memory model makes the torn read of `Nullable<int>` observable much more often.

## Suggested fix

**1. Make the cached hash a plain `int` (the actual bug).** A naturally aligned 32-bit write is
atomic, so no reader can ever observe a half-written value. Read it into a local exactly once so the
returned value cannot differ from the one that was tested:

```csharp
private int _hashCode;   // 0 = not yet computed

public override int GetHashCode()
{
    var hashCode = _hashCode;
    if (hashCode == 0)
    {
        hashCode = Identity.GetHashCode();
        _hashCode = hashCode;
    }

    return hashCode;
}
```

An identity whose hash is genuinely 0 is simply recomputed on each call — correct, just not cached.
Computing the hash eagerly in the constructor would work too, since `Identity` is immutable.

This same defect makes *any* `Dictionary` or `HashSet` keyed by `ModelMetadata` and populated
concurrently unreliable; the record-type mapping is merely the one that is then cached forever.

**2. Publish the cached mapping after the data it guards, not before.** Independently of the hash
bug, `CalculateRecordTypeConstructorDetails` sets its completion flag *before* assigning the fields
that flag protects, and none of them are `volatile`:

```csharp
_recordTypeConstructorDetailsCalculated = true;
_parameterMapping = parameterMapping;
_boundConstructorPropertyMapping = propertyMapping;
```

A second thread can observe the flag as `true`, return early from the guard at the top of the
method, and read a `_parameterMapping` that is still `null`. Using `_parameterMapping != null` as
the completion signal and publishing it last removes both the ordering hazard and the redundant
flag:

```csharp
private void CalculateRecordTypeConstructorDetails()
{
    if (Volatile.Read(ref _parameterMapping) is not null)
    {
        return;
    }

    // ... build parameterMapping / propertyMapping as today ...

    _boundConstructorPropertyMapping = propertyMapping;
    Volatile.Write(ref _parameterMapping, parameterMapping);
}
```

Fix 1 is the one that stops the reported failure. Fix 2 closes a latent `NullReferenceException` on
the same code path.

## Workaround available to applications today

Force the mapping for every bound request type on a single thread at startup, before the server
accepts traffic, so no two threads ever race to compute these hashes — see `MetadataWarmUp.cs`. With
it applied, the same harness reports **0** failures of either kind over 2000+ cold starts.

It needs reflection, because `BoundConstructorParameterMapping` is `internal` and the only public
way to force it is to run a full validation pass over a constructed instance of every request type.
That it cannot be done with public API is part of why we are reporting this rather than just
shipping the workaround.

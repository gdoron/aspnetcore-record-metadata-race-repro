using AspNetCoreRecordMetadataRaceRepro;

var applyWorkaround = args.Contains("--warm-up");
var coldStarts = ArgValue("--cold-starts") ?? 2000;
var threads = ArgValue("--threads") ?? 64;

Console.WriteLine(
    $"runtime {Environment.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
Console.WriteLine($"{coldStarts} cold starts x {threads} concurrent first requests, warm-up={applyWorkaround}\n");

var result = ColdStartHarness.Run(coldStarts, threads, applyWorkaround);

Console.WriteLine($"\ncold starts with any failure : {result.ColdStartsWithFailures}");
Console.WriteLine($"  of which PERMANENTLY broken: {result.PermanentlyBroken}");
Console.WriteLine($"  of which transient         : {result.Transient}");

// A permanently broken cold start is the interesting one: it is indistinguishable from a healthy
// process until the first request arrives, and it never recovers.
return result.PermanentlyBroken > 0 ? 1 : 0;

int? ArgValue(string name) {
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1]) : null;
}

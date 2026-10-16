using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MikuN2N.Models;
using MikuN2N.Services;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++; Console.WriteLine("PASS: " + message);
}
Check(new AppSettings().AutoConfigureNetwork, "new settings retain existing enabled behavior");
Check(JsonSerializer.Deserialize<AppSettings>("{}")!.AutoConfigureNetwork, "old settings with no new key keep enabled behavior");
var settings = new AppSettings { AutoConfigureNetwork = false };
Check(!JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.AutoConfigureNetwork, "disabled setting survives serialization");
Check(!settings.ExperimentalDiagnosticUpload, "normal diagnostic upload stays off by default");

var type = typeof(EdgeController);
var builder = type.GetMethod("BuildN3nConfiguration", BindingFlags.Static | BindingFlags.NonPublic)!;
string Configuration(object enabled) => (string)builder.Invoke(null,
    ["node.invalid:3076", "fixture", "fixture", null, null, 50001, 31337, "fixture-secret", false, enabled])!;
var enabledConfig = Configuration(true);
var disabledConfig = Configuration(false);
Check(enabledConfig == Configuration(Type.Missing), "omitted network option preserves enabled n3n configuration");
Check(enabledConfig.Contains("metric=1\n"), "enabled mode preserves native metric 1");
Check(disabledConfig.Contains("metric=0\n") && !disabledConfig.Contains("metric=1\n"), "disabled mode tells native edge to leave metric alone");
Check(disabledConfig.Replace("metric=0\n", "metric=1\n") == enabledConfig, "turning adaptation off preserves all other connection configuration");
Check(disabledConfig.Contains("address_mode=auto") && disabledConfig.Contains("mikun2n_punch=true"), "virtual address assignment and connectivity remain configured");

// Only invoke the proven disabled path. No EdgeController constructor, processes,
// discovery, adapters or enabled setup path are run by this fixture.
var runType = type.GetNestedType("EdgeRun", BindingFlags.NonPublic)!;
var run = RuntimeHelpers.GetUninitializedObject(runType);
runType.GetProperty("AutoConfigureNetwork")!.SetValue(run, false);
var controller = RuntimeHelpers.GetUninitializedObject(type);
var mapping = type.GetMethod("ConfigurePortMappingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
await (Task)mapping.Invoke(controller, [run, CancellationToken.None])!;
Check(runType.GetProperty("PortMapping")!.GetValue(run) is null, "disabled UPnP path completes without creating a mapping service");

var source = File.ReadAllText(Path.Combine(args[0], "Services", "EdgeController.cs"));
Check(source.Contains("if (run.AutoConfigureNetwork)\n                {\n                    run.TunedAdapterId"), "managed firewall/profile/metric scheduling is inside the session option gate");
var native = File.ReadAllText(Path.Combine(args[0], "native", "n3n-3.4.4", "src", "win32", "wintap.c"));
Check(native.Contains("if(metric) { /* try to change only if a value has been given"), "existing shipped native source confirms metric 0 skips modification");
Directory.CreateDirectory(args[1]);
File.WriteAllText(Path.Combine(args[1], "network-option-validation.json"), JsonSerializer.Serialize(new {
    passed = true, assertions = checks, productionAssembly = typeof(EdgeController).Assembly.Location,
    realNetworkOrSettingsOrAppStartup = false, enabledSystemSetupExecuted = false,
    coverage = "production model/JSON migration, config builder, disabled UPnP path; managed scheduling source gate and native metric contract"
}, new JsonSerializerOptions { WriteIndented = true }));

using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using MikuN2N;
using MikuN2N.Models;

internal static class TestState
{
    internal static readonly List<string> Trace = [];
    internal static int Assertions;
    internal static Exception? Failure;
    internal static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Assertions++; Console.WriteLine("PASS: " + description);
    }
}

internal static class TrayExitEntry
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args[0] == "--policy-ui") return SettingsPolicyTests.Run(args[1], args[2]);
        var scenario = args[0];
        var root = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app, root);
        app.ThemeManager.Apply(ThemePreference.Light);
        app.DispatcherUnhandledException += (_, e) =>
        {
            TestState.Failure = e.Exception; e.Handled = true; app.Shutdown(1);
        };
        var owner = new MainWindow();
        app.Exit += (_, _) => TestState.Trace.Add("app.exit");
        app.Dispatcher.BeginInvoke(async () =>
        {
            try { await Run(owner, scenario, app); }
            catch (Exception exception) { TestState.Failure = exception; app.Shutdown(1); }
        }, DispatcherPriority.Background);
        app.Run(owner);
        try
        {
            if (TestState.Failure is not null) throw TestState.Failure;
            TestState.Check(owner.Edge.Disposals == 1, "edge cleanup runs exactly once");
            TestState.Check(TestState.Trace.Count(item => item == "tray.disposed") == 1, "tray is disposed exactly once");
            TestState.Check(owner.CloseAllowed, "shutdown permits owner closing");
            TestState.Check(TestState.Trace.Contains("owner.closed") && TestState.Trace.Contains("app.exit"), "WPF window closes and Application exits");
            TestState.Check(TestState.Trace.IndexOf("settings.applied") < TestState.Trace.IndexOf("edge.dispose"), "modal frame unwinds and settings finish before teardown");
            if (scenario == "cleanup-failure")
                TestState.Check(TestState.Trace.Contains("edge.fallback.fake"), "cleanup failure uses only fake fallback and still disposes tray");
            else TestState.Check(TestState.Trace.IndexOf("edge.dispose") < TestState.Trace.IndexOf("diagnostics.finished") &&
                TestState.Trace.IndexOf("diagnostics.finished") < TestState.Trace.IndexOf("tray.disposed"), "edge, diagnostics, tray cleanup order is preserved");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, scenario + ".json"), JsonSerializer.Serialize(new {
                passed = true, scenario, assertions = TestState.Assertions, trace = TestState.Trace,
                actualSettingsXamlAndClosing = true, actualMainExitCode = true,
                settingsPersistence = "simulated completion; no SettingsStore", prompts = "controlled choices",
                actualAppStartup = false, realSettings = false, realEdge = false, realTray = false,
                network = false, offscreen = true, showActivated = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { app.ThemeManager.Dispose(); }
    }

    private static async Task Run(MainWindow owner, string scenario, App app)
    {
        owner.Edge.IsRunning = scenario.StartsWith("connected");
        owner.Edge.FailDispose = scenario == "cleanup-failure";
        var settings = new SettingsWindow(new AppSettings { Theme = ThemePreference.Light },
            initialTabIndex: scenario == "about" ? 2 : 1, connected: owner.Edge.IsRunning) { Owner = owner };
        var dirty = scenario.StartsWith("dirty") || scenario == "invalid-save";
        if (dirty)
        {
            Set(settings, "_nodesDirty", true);
            if (scenario == "invalid-save")
            {
                var nodes = (List<SupernodeNode>)Field(settings, "_nodes")!;
                nodes.Add(new SupernodeNode { Name = "Fixture", Server = "invalid::::", Community = "fixture" });
                Set(settings, "_activeNodeId", nodes[0].Id);
            }
        }
        ThemedMessageDialog.Choice = scenario switch {
            "dirty-save" or "invalid-save" => DialogChoice.Primary,
            "dirty-cancel" => DialogChoice.Cancel,
            _ => DialogChoice.Secondary
        };
        ThemedMessageDialog.Confirmation = scenario != "connected-cancel";
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.SettingsCompletion(completion.Task);
        settings.Loaded += (_, _) => settings.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                TestState.Check(settings.Left < -10000 && !settings.ShowActivated, "fixture settings stay offscreen without activation");
                if (scenario == "nested-dialog")
                {
                    var child = new Window { Owner = settings, ShowActivated = false, ShowInTaskbar = false,
                        Left = -20000, Top = -20000, Width = 100, Height = 100 };
                    child.Show();
                    await owner.RequestExit();
                    TestState.Check(settings.IsVisible && child.IsVisible && owner.Edge.Disposals == 0 && owner.FocusRequests == 1,
                        "an existing child dialog is preserved before exit is retried");
                    child.Close();
                }
                var exit = owner.RequestExit();
                if (scenario == "repeat-exit")
                {
                    await owner.RequestExit();
                    TestState.Check(owner.Edge.Disposals == 0, "repeat exit does not overtake pending settings completion");
                }
                if (scenario is "dirty-cancel" or "invalid-save" or "connected-cancel")
                {
                    await exit;
                    TestState.Check(owner.Edge.Disposals == 0 && !owner.ExitPending, "cancel or validation failure aborts exit and releases the guard");
                    if (scenario != "connected-cancel")
                        TestState.Check(settings.IsVisible, "unsaved settings remain open after cancelled exit");
                    if (scenario == "invalid-save")
                        TestState.Check(TestState.Trace.Contains("prompt.validation"), "failed Save validation does not discard edits");
                    ThemedMessageDialog.Choice = DialogChoice.Secondary;
                    ThemedMessageDialog.Confirmation = true;
                    await owner.RequestExit();
                }
                else await exit;
            }
            catch (Exception exception) { TestState.Failure = exception; app.Shutdown(1); }
        }, DispatcherPriority.Background);
        var result = settings.ShowDialog();
        if (scenario == "dirty-save") TestState.Check(result == true, "dirty Save follows the real Settings Save handler");
        // A delayed completion catches teardown running while the owner is applying saved settings.
        await Task.Delay(30);
        TestState.Trace.Add("settings.applied");
        completion.TrySetResult();
    }

    private static object? Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    internal static void LoadResources(App app, string source)
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary = XDocument.Load(Path.Combine(source, "App.xaml")).Descendants(p + "ResourceDictionary").First();
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        dictionary.SetAttributeValue(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=mscorlib");
        foreach (var item in dictionary.Descendants(p + "ResourceDictionary"))
            if (item.Attribute("Source") is { } uri) uri.Value = "pack://application:,,,/" + uri.Value;
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DocsViewer.Core;
using DocsViewer.Core.Parsing;
using DocsViewer.Infrastructure;
using DocsViewer.ViewModels;

namespace DocsViewer;

public partial class App : Application
{
    private TextWriterTraceListener? _bindingListener;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ErrorLog.Write(args.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, args) => { ErrorLog.Write(args.Exception, "Unobserved task"); args.SetObserved(); };

        var args = e.Args ?? Array.Empty<string>();

        // --dump <file.md> [out.txt]: render the graph as text and exit.
        var dumpIndex = Array.FindIndex(args, a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase));
        if (dumpIndex >= 0)
        {
            var code = RunDump(args.Skip(dumpIndex + 1).ToArray());
            Shutdown(code);
            return;
        }

        var sample = args.Any(a => a.Equals("--sample", StringComparison.OrdinalIgnoreCase));
        if (sample) EnableBindingTrace();

        var settings = AppSettings.Load();
        var library = new DocLibrary();
        _viewModel = new MainViewModel(library, settings, Dispatcher);
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();

        try
        {
            if (sample)
            {
                _viewModel.LoadSample(DesignTimeData.CreateLoginGraph(), DesignTimeData.DeepNodeId);
                // Optional "--mode Components|Sections|Flows" to exercise the other root views.
                var modeIndex = Array.FindIndex(args, a => a.Equals("--mode", StringComparison.OrdinalIgnoreCase));
                if (modeIndex >= 0 && modeIndex + 1 < args.Length && Enum.TryParse<ViewMode>(args[modeIndex + 1], true, out var mode))
                    _viewModel.Mode = mode;
            }
            else
            {
                var explicitPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
                _viewModel.Startup(explicitPath);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Startup");
            MessageBox.Show(window, $"DocsViewer could not load its documents:\n\n{ex.Message}\n\nDetails were written to {ErrorLog.LogPath}",
                "DocsViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        try
        {
            _bindingListener?.Flush();
            _bindingListener?.Dispose();
        }
        catch
        {
            // ignore
        }
        base.OnExit(e);
    }

    private static int RunDump(string[] rest)
    {
        string? output = null;
        try
        {
            if (rest.Length == 0)
            {
                output = "usage: DocsViewer --dump <file.md> [out.txt]";
                WriteDumpOutput(output, null);
                return 2;
            }
            var file = Path.GetFullPath(rest[0]);
            var outFile = rest.Length > 1 ? Path.GetFullPath(rest[1]) : null;
            var markdown = File.ReadAllText(file);
            var graph = new MarkdownDocParser().Parse(markdown, file);
            output = GraphDump.Render(graph);
            WriteDumpOutput(output, outFile);
            return 0;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "--dump");
            WriteDumpOutput((output ?? string.Empty) + Environment.NewLine + "error: " + ex.Message, null);
            return 1;
        }
    }

    private static void WriteDumpOutput(string text, string? outFile)
    {
        if (outFile != null)
        {
            var dir = Path.GetDirectoryName(outFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outFile, text);
            return;
        }

        NativeMethods.AttachConsole(NativeMethods.AttachParentProcess);
        try
        {
            using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            stdout.WriteLine();
            stdout.Write(text);
            stdout.WriteLine();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "--dump console");
        }
    }

    /// <summary>Writes WPF data-binding warnings/errors to %LocalAppData%\DocsViewer\bindings.log (enabled by --sample).</summary>
    private void EnableBindingTrace()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            var path = Path.Combine(AppSettings.DataDirectory, "bindings.log");
            File.WriteAllText(path, $"# binding trace {DateTime.Now:O}{Environment.NewLine}");
            PresentationTraceSources.Refresh();
            _bindingListener = new TextWriterTraceListener(path);
            PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingListener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
            Trace.AutoFlush = true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Binding trace");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception, "DispatcherUnhandledException");
        e.Handled = true;
        try
        {
            MessageBox.Show(MainWindow!,
                $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to {ErrorLog.LogPath}",
                "DocsViewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Never let the handler itself throw.
        }
    }
}

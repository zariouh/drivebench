using System.Threading;
using System.Windows;
namespace DriveBench.App;
public partial class App : Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    { 
        _mutex = new Mutex(true, @"Local\DriveBench.Phase1", out bool created);
        if (!created) { MessageBox.Show("DriveBench is already running."); Shutdown(); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}
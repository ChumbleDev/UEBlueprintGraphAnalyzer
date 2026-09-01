using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace UEBlueprintGraphViewer
{
    public partial class App : Application
    {
        public static MainWindow MainWindow = null!;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    _ = DialogWindow.Show(ErrorLog.DialogText(e.Exception, "Dispatcher.UIThread"), "Unhandled error");
                }
                catch (Exception showEx)
                {
                    ErrorLog.Record(showEx, "Failed to show unhandled error dialog");
                }
            };

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                MainWindow = new MainWindow();
                desktop.MainWindow = MainWindow;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}

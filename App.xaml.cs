using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FinishProjectOrganizer;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static DateTime _lastErrorBox = DateTime.MinValue;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Só uma janela por vez: duas instâncias sobrescreveriam o config.json uma da outra.
        try
        {
            _mutex = new Mutex(true, @"Local\FinishProjectOrganizer.SingleInstance", out bool first);
            if (!first)
            {
                var h = FindWindow(null, "Finish — Project Organizer");
                if (h != IntPtr.Zero) { ShowWindow(h, 9 /* restaurar */); SetForegroundWindow(h); }
                Shutdown();
                return;
            }
        }
        catch (Exception ex) { Log.Write("Não foi possível criar o controle de instância única", ex); }

        // Erros inesperados: registra no log e avisa, em vez de fechar o programa em silêncio.
        DispatcherUnhandledException += OnUiError;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write("Erro fatal não tratado", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        { Log.Write("Erro em tarefa em segundo plano", args.Exception); args.SetObserved(); };

        // Sons: passar o mouse sobre qualquer botão (toque bem suave).
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseEnterEvent,
            new MouseEventHandler((s, _) =>
            {
                // os botões da barra de título (largura 46) ficam em silêncio
                if (s is ButtonBase b && b.IsEnabled && b.Width != 46) Sfx.Play(Sound.Hover);
            }));

        base.OnStartup(e);

        // a janela é criada aqui (e não por StartupUri) para que a 2ª instância nunca chegue a abrir uma
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Erro na interface", e.Exception);
        e.Handled = true;
        // no máximo um aviso a cada 10 s, para um erro repetido não encher a tela de janelas
        if ((DateTime.Now - _lastErrorBox).TotalSeconds < 10) return;
        _lastErrorBox = DateTime.Now;
        MessageBox.Show("Ocorreu um erro inesperado, mas o programa continua aberto.\n\n" +
                        e.Exception.Message + "\n\nDetalhes foram salvos em:\n" + Log.FileLocation,
                        "Finish", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

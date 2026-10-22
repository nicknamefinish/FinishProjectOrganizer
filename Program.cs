using Velopack;

namespace FinishProjectOrganizer;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Precisa ser a primeira coisa: durante a instalação/atualização o Velopack roda o programa
        // com argumentos especiais, trata o assunto e encerra aqui mesmo (a janela nem chega a abrir).
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

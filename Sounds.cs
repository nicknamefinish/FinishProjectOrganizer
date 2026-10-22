using System.IO;
using System.Media;
using System.Text;

namespace FinishProjectOrganizer;

public enum Sound
{
    Hover, Click, Select, Back, Open, Start, Success, Error, ToggleOn, ToggleOff, Theme,
}

/// <summary>
/// Sons da interface. Assim como os fundos, tudo é sintetizado por código (senoides com envelope):
/// não existe nenhum arquivo de áudio de terceiros dentro do programa.
/// Os sons são gerados em segundo plano na abertura e guardados na memória.
/// </summary>
public static class Sfx
{
    private const int Rate = 44100;
    private static volatile Dictionary<Sound, SoundPlayer>? _players;
    private static DateTime _lastHover = DateTime.MinValue;
    private static DateTime _lastSpecific = DateTime.MinValue;
    private static DateTime _lastAny = DateTime.MinValue;

    public static bool Enabled { get; set; } = true;

    /// <summary>Volume geral (0.05 a 1). Os sons já nascem baixos: o padrão é 0.35.</summary>
    public static double Volume { get; set; } = 0.35;

    /// <summary>Muda o volume: refaz os sons em segundo plano e toca um exemplo.</summary>
    public static void SetVolume(double v, bool preview)
    {
        Volume = Math.Clamp(v, 0.05, 1.0);
        Task.Run(() => { Build(); if (preview) Play(Sound.Select); });
    }

    /// <summary>Prepara os sons sem travar a abertura da janela.</summary>
    public static void Init() => Task.Run(Build);

    /// <summary>Toca um som. "generic" = som automático de qualquer botão; ele cede a um som específico
    /// que acabou de tocar (ex.: sucesso, erro), para um não atropelar o outro.</summary>
    public static void Play(Sound s, bool generic = false)
    {
        if (!Enabled) return;
        var players = _players;
        if (players == null || !players.TryGetValue(s, out var p)) return;

        var now = DateTime.UtcNow;
        if (s == Sound.Hover)
        {
            if ((now - _lastHover).TotalMilliseconds < 70) return;
            if ((now - _lastAny).TotalMilliseconds < 220) return;   // não corta um clique que acabou de tocar
            _lastHover = now;
        }
        else
        {
            if (generic && (now - _lastSpecific).TotalMilliseconds < 60) return;
            if (!generic) _lastSpecific = now;
            _lastAny = now;
        }

        try { p.Play(); }
        catch (Exception ex) { Log.Write("Falha ao tocar som " + s, ex); }
    }

    // ---------------------------------------------------------------------
    //  Síntese
    // ---------------------------------------------------------------------
    private static void Build()
    {
        try
        {
            var raw = new Dictionary<Sound, byte[]>
            {
                // toque bem suave ao passar o mouse
                [Sound.Hover] = Make(0.05, t => Chirp(t, 2400, 2000, 80, 90), 0.09),
                // "pop" curto de clique
                [Sound.Click] = Make(0.11, t => Chirp(t, 1500, 620, 60, 34) + 0.35 * Chirp(t, 3000, 1240, 60, 50), 0.28),
                // seleção de opção / chip
                [Sound.Select] = Make(0.10, t => Chirp(t, 900, 1500, 40, 36), 0.24),
                // voltar / cancelar (desce)
                [Sound.Back] = Make(0.13, t => Chirp(t, 900, 480, 45, 32), 0.24),
                // botão principal (duas notas subindo)
                [Sound.Open] = Make(0.24, t => 0.8 * Bell(t, 659.25, 22) + Note(t, 0.06, 987.77, 20), 0.24),
                // "Começar": acorde aberto
                [Sound.Start] = Make(0.80, t =>
                    Note(t, 0.00, 392.00, 5) + Note(t, 0.05, 493.88, 5) + Note(t, 0.10, 587.33, 5) + Note(t, 0.15, 783.99, 5), 0.18),
                // projeto criado: arpejo brilhante
                [Sound.Success] = Make(1.00, t =>
                    Note(t, 0.00, 523.25, 6) + Note(t, 0.085, 659.25, 6) + Note(t, 0.17, 783.99, 6) + Note(t, 0.255, 1046.50, 5), 0.20),
                // erro: dois tons graves descendo
                [Sound.Error] = Make(0.34, t => Buzz(t, 0.00, 196.0) + Buzz(t, 0.12, 147.0), 0.24),
                [Sound.ToggleOn] = Make(0.14, t => Chirp(t, 880, 1320, 38, 30) + 0.4 * Chirp(t, 1760, 2640, 38, 36), 0.22),
                [Sound.ToggleOff] = Make(0.14, t => Chirp(t, 1300, 700, 38, 30), 0.22),
                // troca de tema: brilho subindo
                [Sound.Theme] = Make(0.60, t =>
                    Note(t, 0.00, 784, 8) + Note(t, 0.06, 988, 8) + Note(t, 0.12, 1175, 8) + Note(t, 0.18, 1568, 8), 0.16),
            };

            var players = new Dictionary<Sound, SoundPlayer>();
            foreach (var kv in raw)
            {
                var sp = new SoundPlayer(new MemoryStream(kv.Value));
                sp.Load();
                players[kv.Key] = sp;
            }
            _players = players;
        }
        catch (Exception ex) { Log.Write("Falha ao preparar os sons", ex); }
    }

    /// <summary>Senoide que desliza de f0 até f1 (glissando exponencial) com decaimento.</summary>
    private static double Chirp(double t, double f0, double f1, double k, double decay)
    {
        double phase = 2 * Math.PI * (f1 * t + (f0 - f1) * (1 - Math.Exp(-k * t)) / k);
        return Math.Sin(phase) * Math.Exp(-decay * t) * Math.Min(1.0, t / 0.003);
    }

    /// <summary>Timbre de sino: fundamental + dois harmônicos que somem mais rápido.</summary>
    private static double Bell(double t, double f, double decay)
    {
        if (t < 0) return 0;
        double w = 2 * Math.PI * f * t;
        double s = Math.Sin(w)
                 + 0.45 * Math.Sin(2.0 * w) * Math.Exp(-decay * 0.6 * t)
                 + 0.18 * Math.Sin(3.01 * w) * Math.Exp(-decay * 1.3 * t);
        return s * Math.Exp(-decay * t) * Math.Min(1.0, t / 0.003) * 0.7;
    }

    private static double Note(double t, double start, double f, double decay) => Bell(t - start, f, decay);

    private static double Buzz(double t, double start, double f)
    {
        double x = t - start;
        if (x < 0) return 0;
        double w = 2 * Math.PI * f * x;
        return (Math.Sin(w) + 0.35 * Math.Sin(3 * w)) * Math.Exp(-14 * x) * Math.Min(1.0, x / 0.004) * 0.8;
    }

    /// <summary>Gera um WAV mono de 16 bits na memória.</summary>
    private static byte[] Make(double seconds, Func<double, double> gen, double gain)
    {
        int n = (int)(Rate * seconds);
        int fade = (int)(Rate * 0.012);
        var pcm = new short[n];
        for (int i = 0; i < n; i++)
        {
            double v = gen(i / (double)Rate) * gain * Volume;
            if (i > n - fade) v *= (n - i) / (double)fade;   // sem "estalo" no fim
            pcm[i] = (short)Math.Clamp(v * 32767.0, -32767, 32767);
        }

        int dataLen = n * 2;
        using var ms = new MemoryStream(44 + dataLen);
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataLen);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);          // PCM
            bw.Write((short)1);          // mono
            bw.Write(Rate);
            bw.Write(Rate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataLen);
            foreach (var s in pcm) bw.Write(s);
        }
        return ms.ToArray();
    }
}

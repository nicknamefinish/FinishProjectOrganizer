using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Text.RegularExpressions;


namespace FinishProjectOrganizer;

/// <summary>Configuração salva em %LOCALAPPDATA%\FinishProjectOrganizer\config.json
/// (mesmo arquivo e formato do organizador .bat, então suas configurações antigas continuam valendo).</summary>
public class AppConfig
{
    public string Base { get; set; } = "";
    public List<string> CustomApps { get; set; } = new();
    public Dictionary<string, string> AppFolders { get; set; } = new();
    /// <summary>Sons da interface ligados/desligados.</summary>
    public bool SoundOn { get; set; } = true;
    /// <summary>Volume dos sons (0.05 a 1). Começa baixo: 0.35.</summary>
    public double SoundVolume { get; set; } = 0.35;
    /// <summary>Cor de destaque da interface, como #RRGGBB.</summary>
    public string AccentColor { get; set; } = "#2FD67B";
    /// <summary>Como a pessoa quer ser chamada. Vazio = usar o nome da conta do Windows.</summary>
    public string UserName { get; set; } = "";
    /// <summary>true = usa a foto escolhida pela pessoa (avatar.png); false = foto da conta do Windows.</summary>
    public bool CustomAvatar { get; set; }
}

public static class ConfigService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FinishProjectOrganizer");
    private static readonly string FilePath = Path.Combine(Dir, "config.json");

    private static readonly string BakPath = FilePath + ".bak";

    public static bool Exists => File.Exists(FilePath);

    /// <summary>Pasta de dados do programa (config, log e foto escolhida).</summary>
    public static string DirPath => Dir;

    /// <summary>Lê a configuração; se o arquivo principal estiver corrompido, tenta o backup.</summary>
    public static AppConfig Load()
    {
        if (TryRead(FilePath, out var cfg)) return cfg;
        if (TryRead(BakPath, out cfg)) return cfg;
        return new AppConfig();
    }

    private static bool TryRead(string path, out AppConfig cfg)
    {
        cfg = new AppConfig();
        try
        {
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("Base", out var b) && b.ValueKind == JsonValueKind.String)
                cfg.Base = b.GetString() ?? "";

            if (root.TryGetProperty("CustomApps", out var ca))
            {
                if (ca.ValueKind == JsonValueKind.Array)
                    foreach (var e in ca.EnumerateArray())
                    { if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) cfg.CustomApps.Add(s); }
                else if (ca.ValueKind == JsonValueKind.String && ca.GetString() is { Length: > 0 } one)
                    cfg.CustomApps.Add(one);
            }

            if (root.TryGetProperty("AppFolders", out var af) && af.ValueKind == JsonValueKind.Object)
                foreach (var p in af.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) cfg.AppFolders[p.Name] = p.Value.GetString() ?? "";

            if (root.TryGetProperty("SoundOn", out var so) && (so.ValueKind == JsonValueKind.True || so.ValueKind == JsonValueKind.False))
                cfg.SoundOn = so.GetBoolean();

            if (root.TryGetProperty("SoundVolume", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.TryGetDouble(out var vol))
                cfg.SoundVolume = Math.Clamp(vol, 0.05, 1.0);

            if (root.TryGetProperty("AccentColor", out var ac) && ac.ValueKind == JsonValueKind.String && ac.GetString() is { Length: > 0 } accent)
                cfg.AccentColor = accent;

            if (root.TryGetProperty("UserName", out var un) && un.ValueKind == JsonValueKind.String)
                cfg.UserName = (un.GetString() ?? "").Trim();

            if (root.TryGetProperty("CustomAvatar", out var ca2) && (ca2.ValueKind == JsonValueKind.True || ca2.ValueKind == JsonValueKind.False))
                cfg.CustomAvatar = ca2.GetBoolean();

            return true;
        }
        catch (Exception ex) { Log.Write("Configuração ilegível: " + path, ex); return false; }   // arquivo corrompido ou ilegível
    }

    /// <summary>Grava em arquivo temporário e só então troca o arquivo real,
    /// guardando uma cópia (.bak) da versão anterior que estava íntegra.</summary>
    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(FilePath))
        {
            if (TryRead(FilePath, out _))
            {
                try { File.Copy(FilePath, BakPath, true); } catch (Exception ex) { Log.Write("Backup da configuração falhou", ex); }
            }
            File.Move(tmp, FilePath, true);
        }
        else File.Move(tmp, FilePath);
    }
}

/// <summary>Registro simples de erros em %LOCALAPPDATA%\FinishProjectOrganizer\log.txt (ajuda no suporte).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FinishProjectOrganizer");
    private static readonly string FilePath = Path.Combine(Dir, "log.txt");

    public static string FileLocation => FilePath;

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) File.Move(FilePath, FilePath + ".old", true);   // não cresce sem parar
                var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}" + (ex != null ? Environment.NewLine + ex : "") + Environment.NewLine;
                File.AppendAllText(FilePath, text);
            }
        }
        catch { /* registrar o erro nunca pode causar outro erro */ }
    }
}

/// <summary>Cria a estrutura de pastas; se algo falhar no meio, desfaz o que foi criado
/// (assim o usuário pode tentar de novo sem cair em "este projeto já existe").</summary>
public static class ProjectCreator
{
    /// <summary>Limite de caminho com folga: Explorer e vários programas de criação não passam de 260 caracteres.</summary>
    public const int MaxPathLength = 240;

    /// <summary>Mensagem de erro se o caminho (ou alguma subpasta) ficar longo demais; senão, null.</summary>
    public static string? CheckLength(string target, IEnumerable<string> subfolders)
    {
        if (target.Length > MaxPathLength)
            return $"O caminho do projeto ficou longo demais ({target.Length} caracteres; o limite é {MaxPathLength}). Use nomes mais curtos.";
        foreach (var f in subfolders)
            if (System.IO.Path.Combine(target, f).Length > MaxPathLength)
                return $"A subpasta \"{f}\" deixaria o caminho longo demais (limite de {MaxPathLength} caracteres). Use nomes mais curtos.";
        return null;
    }

    /// <summary>Devolve null se deu certo, ou a mensagem do erro (já com tudo desfeito).</summary>
    public static string? Create(string target, IEnumerable<string> subfolders)
    {
        // a pasta mais alta que ainda não existe é a que será desfeita em caso de falha
        string? firstNew = null;
        try
        {
            for (var d = target; !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = System.IO.Path.GetDirectoryName(d))
                firstNew = d;
        }
        catch { firstNew = target; }

        try
        {
            Directory.CreateDirectory(target);
            foreach (var f in subfolders)
                Directory.CreateDirectory(System.IO.Path.Combine(target, f));
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("Falha ao criar " + target, ex);
            try
            {
                if (firstNew != null && System.IO.Path.GetPathRoot(firstNew) != firstNew && Directory.Exists(firstNew))
                    Directory.Delete(firstNew, true);
            }
            catch (Exception rb) { Log.Write("Não foi possível desfazer " + firstNew, rb); }
            return ex.Message;
        }
    }
}

public static class Validation
{
    private static readonly Regex Invalid = new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);
    private static readonly Regex Reserved = new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim()) return false;
        if (Invalid.IsMatch(name) || name.EndsWith('.')) return false;
        if (Reserved.IsMatch(name)) return false;
        return true;
    }

    public static bool IsValidRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (var part in path.Split('\\', '/'))
            if (!IsValidName(part) || part == "." || part == "..") return false;
        return true;
    }
}

public static class Presets
{
    public static readonly string[] DefaultApps =
    {
        // Adobe
        "Adobe Photoshop", "Adobe Illustrator", "Adobe After Effects", "Adobe Premiere", "Adobe Substance 3D Painter",
        // Outros
        "DaVinci Resolve", "Blender", "Autodesk Maya", "ZBrush",
    };

    public static string[] For(string app) => app switch
    {
        "Blender" => new[] { "Arquivo Blender", "Modelos", "Texturas", "Materiais", "HDRI", "Renders", "Animações", "Referências", "Exportações", "Entregas" },
        "Autodesk Maya" => new[] { "Cenas Maya", "Modelos", "Texturas", "Rigs", "Animações", "Renders", "Referências", "Exportações", "Entregas" },
        "ZBrush" => new[] { "Arquivos ZBrush", "Subtools", "Alphas", "Texturas", "Referências", "Exportações", "Entregas" },
        "Adobe Substance 3D Painter" => new[] { "Arquivos Painter", "Modelos", "Texturas Exportadas", "Smart Materials", "HDRI", "Referências", "Exportações", "Entregas" },
        "Adobe Photoshop" => new[] { "Arquivos PSD", "Imagens", "Texturas", "Fontes", "Mockups", "Referências", "Exportações", "Entregas" },
        "Adobe Illustrator" => new[] { "Arquivos AI", "Vetores", "Fontes", "Mockups", "Referências", "Exportações", "Entregas" },
        "Adobe After Effects" => new[] { "Arquivos AEP", "Assets", "Imagens", "Áudio", "Vídeos", "Pré-composições", "Referências", "Prévias", "Exportações", "Entregas" },
        "Adobe Premiere" => new[] { "Arquivos Premiere", "Footage", "Áudio", "Trilha Sonora", "SFX", "Referências", "Prévias", "Exportações", "Entregas" },
        "DaVinci Resolve" => new[] { "Projetos Resolve", "Footage", "Áudio", "LUTs", "Referências", "Prévias", "Exportações", "Entregas" },
        _ => new[] { "Projeto", "Assets", "Referências", "Exportações", "Entregas", "Imagens", "Áudio", "Vídeos", "Fontes", "Prévias", "Final" },
    };

    /// <summary>Sigla de duas letras exibida no cartão do programa.</summary>
    public static string Badge(string app) => app switch
    {
        "Adobe Photoshop" => "Ps",
        "Adobe After Effects" => "Ae",
        "Adobe Premiere" => "Pr",
        "DaVinci Resolve" => "Dv",
        "Adobe Illustrator" => "Ai",
        "Blender" => "Bl",
        "Autodesk Maya" => "Ma",
        "ZBrush" => "Zb",
        "Adobe Substance 3D Painter" => "Sp",
        _ => app.Length >= 2 ? char.ToUpper(app[0]) + app[1].ToString().ToLower() : app.ToUpper(),
    };

    /// <summary>Nome do arquivo de ícone em Icons\ (ex.: "Adobe Photoshop" -> adobe-photoshop.png).</summary>
    public static string Slug(string app) =>
        System.Text.RegularExpressions.Regex.Replace(app.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}

/// <summary>Nome e foto da conta do Windows que está usando o programa.</summary>
public static class UserInfo
{
    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetUserNameEx(int nameFormat, StringBuilder name, ref uint size);
    private const int NameDisplay = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_10
    {
        public string usri10_name;
        public string usri10_comment;
        public string usri10_usr_comment;
        public string usri10_full_name;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);
    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    // Função do Windows que devolve o caminho da imagem da conta (usada pelo próprio sistema).
    [DllImport("shell32.dll", EntryPoint = "#261", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void GetUserTilePath(string? username, uint options, StringBuilder path, int maxLength);

    /// <summary>Nome de exibição (ex.: "João Silva"); se não houver, usa o nome de usuário do Windows.</summary>
    public static string GetDisplayName()
    {
        try
        {
            var sb = new StringBuilder(256);
            uint size = 256;
            if (GetUserNameEx(NameDisplay, sb, ref size) && sb.ToString().Trim().Length > 0)
                return sb.ToString().Trim();
        }
        catch { }

        try
        {
            if (NetUserGetInfo(null, Environment.UserName, 10, out var ptr) == 0 && ptr != IntPtr.Zero)
            {
                try
                {
                    var info = Marshal.PtrToStructure<USER_INFO_10>(ptr);
                    if (!string.IsNullOrWhiteSpace(info.usri10_full_name)) return info.usri10_full_name.Trim();
                }
                finally { NetApiBufferFree(ptr); }
            }
        }
        catch { }

        var u = Environment.UserName;
        return u.Length > 0 ? char.ToUpper(u[0]) + u[1..] : "Usuário";
    }

    /// <summary>Iniciais (até 2 letras) para quando não houver foto.</summary>
    public static string Initials(string name)
    {
        var parts = name.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..1].ToUpper();
        return (parts[0][..1] + parts[^1][..1]).ToUpper();
    }

    /// <summary>Foto da conta do Windows, ou null se não for possível obter.</summary>
    public static ImageSource? GetAvatar()
    {
        // 1) caminho informado pelo próprio Windows
        try
        {
            var sb = new StringBuilder(1024);
            GetUserTilePath(null, 0x80000000, sb, sb.Capacity);
            var img = LoadFile(sb.ToString());
            if (img != null) return img;
        }
        catch { }

        // 2) arquivo da foto de conta salvo no perfil (formato .accountpicture-ms)
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "Microsoft", "Windows", "AccountPictures");
            if (Directory.Exists(dir))
            {
                var file = new DirectoryInfo(dir).GetFiles("*.accountpicture-ms")
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (file != null)
                {
                    var img = FromAccountPicture(File.ReadAllBytes(file.FullName));
                    if (img != null) return img;
                }
            }
        }
        catch { }

        // 3) imagens padrão do Windows
        try
        {
            var common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                      "Microsoft", "User Account Pictures");
            foreach (var n in new[] { Environment.UserName + ".png", Environment.UserDomainName + "+" + Environment.UserName + ".png",
                                      "user-192.png", "user.png" })
            {
                var img = LoadFile(Path.Combine(common, n));
                if (img != null) return img;
            }
        }
        catch { }

        return null;
    }

    private static ImageSource? LoadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return Decode(File.ReadAllBytes(path));
    }

    internal static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>O .accountpicture-ms guarda as fotos (PNG/JPEG) dentro de um contêiner; procuramos as imagens e usamos a maior.</summary>
    private static ImageSource? FromAccountPicture(byte[] data)
    {
        var starts = new List<int>();
        for (int i = 0; i + 8 < data.Length; i++)
        {
            bool png = data[i] == 0x89 && data[i + 1] == 0x50 && data[i + 2] == 0x4E && data[i + 3] == 0x47;
            bool jpg = data[i] == 0xFF && data[i + 1] == 0xD8 && data[i + 2] == 0xFF;
            if (png || jpg) starts.Add(i);
        }

        BitmapSource? best = null;
        for (int n = 0; n < starts.Count; n++)
        {
            int from = starts[n];
            int to = n + 1 < starts.Count ? starts[n + 1] : data.Length;
            var img = Decode(data[from..to]);
            if (img != null && (best == null || img.PixelWidth > best.PixelWidth)) best = img;
        }
        return best;
    }
}


/// <summary>Foto escolhida pela pessoa (guardada em %LOCALAPPDATA%\FinishProjectOrganizer\avatar.png).</summary>
public static class ProfileService
{
    private static string AvatarPath => Path.Combine(ConfigService.DirPath, "avatar.png");

    public static ImageSource? LoadCustomAvatar()
    {
        try { return File.Exists(AvatarPath) ? UserInfo.Decode(File.ReadAllBytes(AvatarPath)) : null; }
        catch (Exception ex) { Log.Write("Não foi possível ler a foto escolhida", ex); return null; }
    }

    /// <summary>Recorta a imagem em quadrado (centro), reduz para no máximo 320 px e salva como PNG.
    /// Devolve null se deu certo, ou a mensagem de erro.</summary>
    public static string? SaveCustomAvatar(string sourceFile)
    {
        try
        {
            var src = UserInfo.Decode(File.ReadAllBytes(sourceFile));
            if (src == null) return "Não foi possível ler essa imagem.";

            int w = src.PixelWidth, h = src.PixelHeight, side = Math.Min(w, h);
            if (side < 1) return "Imagem inválida.";
            BitmapSource img = new CroppedBitmap(src, new System.Windows.Int32Rect((w - side) / 2, (h - side) / 2, side, side));
            if (side > 320) img = new TransformedBitmap(img, new ScaleTransform(320.0 / side, 320.0 / side));

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            Directory.CreateDirectory(ConfigService.DirPath);
            var tmp = AvatarPath + ".tmp";
            using (var fs = File.Create(tmp)) enc.Save(fs);
            File.Move(tmp, AvatarPath, true);
            return null;
        }
        catch (Exception ex) { Log.Write("Falha ao salvar a foto escolhida", ex); return ex.Message; }
    }

    public static void DeleteCustomAvatar()
    {
        try { if (File.Exists(AvatarPath)) File.Delete(AvatarPath); }
        catch (Exception ex) { Log.Write("Não foi possível apagar a foto escolhida", ex); }
    }
}


/// <summary>Informações do programa e de onde vêm as atualizações.</summary>
public static class AppInfo
{
    /// <summary>TROQUE pelo endereço do seu repositório no GitHub (veja ONLINE.txt).
    /// Enquanto estiver com "SEU-USUARIO", a atualização automática fica desligada.</summary>
    public const string RepoUrl = "https://github.com/nicknamefinish/FinishProjectOrganizer";

    public static bool UpdatesConfigured => !RepoUrl.Contains("SEU-USUARIO", StringComparison.OrdinalIgnoreCase);

    public static string Version
    {
        get
        {
            var v = typeof(AppInfo).Assembly.GetName().Version;
            return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}

/// <summary>Atualização automática (Velopack + GitHub Releases). Só funciona no programa instalado pelo Setup;
/// rodando direto do Visual Studio / pasta dist, ela simplesmente fica indisponível.</summary>
public static class UpdateService
{
    private static Velopack.UpdateManager? _mgr;
    private static Velopack.UpdateInfo? _info;

    private static Velopack.UpdateManager? Manager
    {
        get
        {
            try { return _mgr ??= new Velopack.UpdateManager(new Velopack.Sources.GithubSource(AppInfo.RepoUrl, null, false)); }
            catch (Exception ex) { Log.Write("Atualizador indisponível", ex); return null; }
        }
    }

    public static bool CanUpdate
    {
        get { try { return AppInfo.UpdatesConfigured && Manager?.IsInstalled == true; } catch { return false; } }
    }

    /// <summary>Procura uma versão nova. Devolve o número dela, ou null se está tudo em dia (ou sem internet).</summary>
    public static async Task<string?> CheckAsync()
    {
        try
        {
            var mgr = Manager;
            if (mgr == null || !CanUpdate) return null;
            _info = await mgr.CheckForUpdatesAsync();
            return _info?.TargetFullRelease.Version.ToString();
        }
        catch (Exception ex) { Log.Write("Falha ao procurar atualização", ex); _info = null; return null; }
    }

    /// <summary>Baixa a versão encontrada e reinicia o programa já atualizado.</summary>
    public static async Task DownloadAndRestartAsync(Action<int>? progress)
    {
        var mgr = Manager;
        if (mgr == null || _info == null) throw new InvalidOperationException("Nenhuma atualização encontrada.");
        await mgr.DownloadUpdatesAsync(_info, progress);
        mgr.ApplyUpdatesAndRestart(_info);
    }
}

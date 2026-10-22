using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FinishProjectOrganizer;

public class AppItem
{
    public string Name { get; set; } = "";
    public string Badge { get; set; } = "";
    public string Status { get; set; } = "";
    public ImageSource? Icon { get; set; }
    public bool IsCustom { get; set; }
    public Visibility RemoveVisibility => IsCustom ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => Icon != null ? Visibility.Collapsed : Visibility.Visible;
}

public partial class MainWindow : Window
{
    private AppConfig _cfg = new();
    private readonly bool _ready;
    private Grid? _current;

    // Estado do assistente
    private string _app = "";
    private string _appDir = "";
    private bool _firstSetup;
    private string? _customParent;
    private int _step;
    private bool _clientOnly;   // novo cliente + projeto em branco = cria só a pasta do cliente
    private string _parentDir = ""; // pasta onde o projeto será criado (programa ou cliente)
    private string _projectName = "";
    private string _target = "";
    private readonly List<string> _selected = new();

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
        _ready = true;

        ProjectNameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Next_Click(this, new RoutedEventArgs()); };
        ProjectNameBox.TextChanged += (_, _) => { if (_ready) UpdateNextLabel(); };
        ClientNameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Next_Click(this, new RoutedEventArgs()); };
        WelcomePathBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) WelcomeStart_Click(this, new RoutedEventArgs()); };
        FolderNameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) NameFolderCreate_Click(this, new RoutedEventArgs()); };
        PreviewKeyDown += OnEscape;

        // Configuração lida já aqui, para que o volume (baixo por padrão) valha desde o primeiro som.
        _cfg = ConfigService.Load();
        Sfx.Volume = _cfg.SoundVolume;
        Sfx.Enabled = _cfg.SoundOn;

        // sons: clique em qualquer botão (os específicos, como sucesso/erro, têm prioridade)
        Sfx.Init();
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnAnyClick));

        // qualquer mensagem de erro que aparece na tela também ganha o som de erro
        var textDesc = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        foreach (var tb in new[] { WizardError, WelcomeError, NameFolderError, AddAppError })
            textDesc.AddValueChanged(tb, (s, _) => { if (s is TextBlock { Text: { Length: > 0 } }) Sfx.Play(Sound.Error); });

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && IntroView.Visibility == Visibility.Visible) { IntroStart_Click(this, new RoutedEventArgs()); e.Handled = true; } };
        Loaded += (_, _) =>
        {
            _syncSound = true; SoundToggle.IsChecked = _cfg.SoundOn; SoundToggle2.IsChecked = _cfg.SoundOn; _syncSound = false;
            BuildSwatches();
            ApplyAccent(Readable(ParseColor(_cfg.AccentColor) ?? DefaultAccent));
            LoadProfile();
            ShowView(IntroView);
        };
    }

    // =====================================================================
    //  Sons e atalhos
    // =====================================================================
    private bool _syncSound;

    private void OnAnyClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not ButtonBase b) return;
        if (b == SoundToggle || b == SoundToggle2) return;   // têm som próprio
        if (b is RadioButton { GroupName: "vol" }) return;   // o volume toca o exemplo já com o novo nível
        if (b is RadioButton or ToggleButton) { Sfx.Play(Sound.Select, generic: true); return; }

        var label = (b.Content as string)?.Trim();
        if (label is "Voltar" or "Cancelar" or "←") Sfx.Play(Sound.Back, generic: true);
        else if (b.Style == (Style)FindResource("PrimaryButton")) Sfx.Play(Sound.Open, generic: true);
        else Sfx.Play(Sound.Click, generic: true);
    }

    private void SoundToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncSound || !_ready || sender is not ToggleButton t) return;
        bool on = t.IsChecked == true;

        _syncSound = true;   // mantém os dois botões (menu e Personalizar) iguais
        SoundToggle.IsChecked = on; SoundToggle2.IsChecked = on;
        _syncSound = false;

        if (!on) Sfx.Play(Sound.ToggleOff);   // o "desligar" ainda é ouvido
        Sfx.Enabled = on;
        _cfg.SoundOn = on;
        SaveCfg();
        if (on) Sfx.Play(Sound.ToggleOn);
    }

    private bool _syncVol;

    private void Volume_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncVol || !_ready || sender is not RadioButton { Tag: string tag }) return;
        if (!double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return;
        _cfg.SoundVolume = v;
        SaveCfg();
        Sfx.SetVolume(v, preview: Sfx.Enabled);
    }

    /// <summary>Esc: fecha a janelinha aberta ou volta uma tela.</summary>
    private void OnEscape(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (AddAppOverlay.Visibility == Visibility.Visible) { CloseAddApp(); e.Handled = true; return; }
        if (_current == WizardView) { Back_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (_current == HomeView || _current == SettingsView || _current == SuccessView) { GoHome_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (_current == NameFolderView) { NameFolderBack_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }

    // =====================================================================
    //  Ajuste à tela: em telas pequenas (ou com escala de 125%+), a janela
    //  diminui e o conteúdo escala junto (Viewbox no XAML), sem cortar nada.
    // =====================================================================
    private const double DesignW = 1040, DesignH = 700;

    private void FitToScreen()
    {
        var wa = SystemParameters.WorkArea;
        double k = Math.Min(1.0, Math.Min((wa.Width - 24) / DesignW, (wa.Height - 24) / DesignH));
        if (k >= 1.0) return;
        MinWidth = 0; MinHeight = 0;
        Width = DesignW * k; Height = DesignH * k;
        try { var chrome = WindowChrome.GetWindowChrome(this); if (chrome != null) chrome.CaptionHeight = 40 * k; }
        catch { /* mantém a altura padrão da barra */ }
    }

    // =====================================================================
    //  Cor da interface (destaque). Os pincéis ficam em App.xaml como DynamicResource;
    //  aqui eles são trocados em tempo real, com uma transição suave.
    // =====================================================================
    private static readonly Color DefaultAccent = Color.FromRgb(0x2F, 0xD6, 0x7B);
    private Color _accent = DefaultAccent;
    private DispatcherTimer? _tween;
    private bool _syncAccent, _syncHex;

    private static readonly (string Name, string Hex)[] Palette =
    {
        ("Verde", "#2FD67B"), ("Lima", "#A3E635"), ("Amarelo", "#FFD23F"), ("Laranja", "#FF8A3D"), ("Vermelho", "#FF5A5F"),
        ("Rosa", "#FF5CA8"), ("Roxo", "#9B6BFF"), ("Azul", "#4D8DFF"), ("Ciano", "#22D3EE"), ("Prata", "#E6E6EC"),
    };

    private static Color? ParseColor(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 6) t = "#" + t;
        if (t.Length != 7 || t[0] != '#') return null;
        for (int i = 1; i < 7; i++) if (!Uri.IsHexDigit(t[i])) return null;
        return Color.FromRgb(Convert.ToByte(t.Substring(1, 2), 16), Convert.ToByte(t.Substring(3, 2), 16), Convert.ToByte(t.Substring(5, 2), 16));
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Cores muito escuras sumiriam no fundo escuro; clareamos até ficarem legíveis.</summary>
    private static Color Readable(Color c)
    {
        for (int i = 0; i < 12 && Luma(c) < 0.30; i++) c = Mix(c, Colors.White, 0.15);
        return c;
    }

    private void ApplyAccent(Color c)
    {
        _accent = c;
        var res = Application.Current.Resources;
        void Put(string key, Color col) { var b = new SolidColorBrush(col); b.Freeze(); res[key] = b; }

        res["AccentColor"] = c;
        Put("AccentBrush", c);
        Put("AccentHoverBrush", Mix(c, Colors.White, 0.28));
        Put("AccentDimBrush", Color.FromArgb(0x33, c.R, c.G, c.B));
        Put("AccentBorderBrush", Color.FromArgb(0x80, c.R, c.G, c.B));
        Put("AccentTintBrush", Mix(Color.FromRgb(0x0D, 0x0D, 0x0F), c, 0.14));
        Put("OnAccentBrush", Luma(c) > 0.42 ? Color.FromRgb(0x0A, 0x0A, 0x0C) : Colors.White);   // texto legível sobre a cor

        // o gradiente escuro do fundo ganha um tom bem leve da cor escolhida
        if (BgGradient.Background is LinearGradientBrush { IsFrozen: false } g && g.GradientStops.Count >= 3)
            g.GradientStops[0].Color = Mix(Color.FromRgb(0x0D, 0x0D, 0x0F), c, 0.12);
    }

    /// <summary>Troca a cor com transição suave e salva a escolha.</summary>
    private void ChangeAccent(Color target)
    {
        target = Readable(target);
        _cfg.AccentColor = Hex(target);
        SaveCfg();

        var start = _accent;
        var t0 = DateTime.UtcNow;
        _tween?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double k = Math.Min(1.0, (DateTime.UtcNow - t0).TotalMilliseconds / 320.0);
            double ease = 1 - Math.Pow(1 - k, 3);
            ApplyAccent(k >= 1.0 ? target : Mix(start, target, ease));
            if (k >= 1.0) timer.Stop();
        };
        _tween = timer;
        timer.Start();
    }

    private void SaveCfg()
    {
        try { ConfigService.Save(_cfg); }
        catch (Exception ex) { Log.Write("Não foi possível salvar as configurações", ex); }
    }

    private void BuildSwatches()
    {
        foreach (var (name, hex) in Palette)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "accent",
                Tag = hex,
                ToolTip = name,
                Background = new SolidColorBrush(ParseColor(hex)!.Value),
            };
            rb.Checked += Swatch_Checked;
            SwatchPanel.Children.Add(rb);
        }
    }

    private void SyncSwatches(string hex)
    {
        _syncAccent = true;
        foreach (var rb in SwatchPanel.Children.OfType<RadioButton>())
            rb.IsChecked = string.Equals((string)rb.Tag, hex, StringComparison.OrdinalIgnoreCase);
        _syncAccent = false;
    }

    private void SyncHex(string hex)
    {
        _syncHex = true;
        HexBox.Text = hex;
        _syncHex = false;
        HexPreview.Fill = new SolidColorBrush(ParseColor(hex) ?? _accent);
    }

    private void Swatch_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncAccent || !_ready || sender is not RadioButton { Tag: string hex }) return;
        var c = ParseColor(hex)!.Value;
        ChangeAccent(c);
        SyncHex(hex);
        Sfx.Play(Sound.Theme);
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _syncHex) return;
        if (ParseColor(HexBox.Text) is Color c) HexPreview.Fill = new SolidColorBrush(c);   // prévia enquanto digita
    }

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyHex_Click(sender, e); e.Handled = true; }
    }

    private void ApplyHex_Click(object sender, RoutedEventArgs e)
    {
        if (ParseColor(HexBox.Text) is not Color c)
        {
            ShowToast("Cor inválida. Use o formato #RRGGBB.");
            Sfx.Play(Sound.Error);
            return;
        }
        c = Readable(c);
        ChangeAccent(c);
        SyncHex(Hex(c));
        SyncSwatches(Hex(c));
        Sfx.Play(Sound.Theme);
        ShowToast("Cor aplicada");
    }

    // =====================================================================
    //  Aviso rápido (toast)
    // =====================================================================
    private DispatcherTimer? _toastTimer;
    private int _toastToken;

    private void ShowToast(string message)
    {
        int token = ++_toastToken;
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        ((TranslateTransform)Toast.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = EaseOut });

        _toastTimer?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (token != _toastToken) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => { if (token == _toastToken) Toast.Visibility = Visibility.Collapsed; };
            Toast.BeginAnimation(OpacityProperty, fade);
        };
        _toastTimer = timer;
        timer.Start();
    }

    // =====================================================================
    //  Perfil: nome (o que a pessoa quiser) e foto (do Windows ou escolhida por ela)
    // =====================================================================
    private string _winName = "";
    private ImageSource? _winAvatar;
    private ImageSource? _avatar;
    private bool _syncName;

    private void LoadProfile()
    {
        _winName = UserInfo.GetDisplayName();
        _winAvatar = UserInfo.GetAvatar();
        ReloadAvatar();
        ApplyProfile();
    }

    private string CurrentName() => string.IsNullOrWhiteSpace(_cfg.UserName) ? _winName : _cfg.UserName.Trim();

    private void ReloadAvatar() =>
        _avatar = (_cfg.CustomAvatar ? ProfileService.LoadCustomAvatar() : null) ?? _winAvatar;

    private static string Greeting()
    {
        int h = DateTime.Now.Hour;
        return h is >= 5 and < 12 ? "Bom dia" : h is >= 12 and < 18 ? "Boa tarde" : "Boa noite";
    }

    private void PaintAvatar(System.Windows.Shapes.Ellipse circle, TextBlock initials)
    {
        if (_avatar != null)
        {
            circle.Fill = new ImageBrush(_avatar) { Stretch = Stretch.UniformToFill };
            initials.Visibility = Visibility.Collapsed;
        }
        else
        {
            circle.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentDimBrush");
            initials.Text = UserInfo.Initials(CurrentName());
            initials.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Atualiza nome e foto em todas as telas (boas-vindas, menu, Personalizar).</summary>
    private void ApplyProfile()
    {
        var name = CurrentName();
        IntroName.Text = name;
        PillName.Text = name;
        MenuGreeting.Text = $"{Greeting()}, {name}";
        PaintAvatar(AvatarCircle, AvatarInitials);
        PaintAvatar(PillAvatar, PillInitials);
        PaintAvatar(SetAvatar, SetInitials);
    }

    // =====================================================================
    //  Tela Personalizar
    // =====================================================================
    private void Personalize_Click(object sender, RoutedEventArgs e)
    {
        _syncName = true; NameBox.Text = _cfg.UserName; _syncName = false;
        UpdateNameHint();
        WinPhotoButton.IsEnabled = _cfg.CustomAvatar;
        ApplyProfile();

        var hex = Hex(_accent);
        SyncSwatches(hex);
        SyncHex(hex);

        _syncSound = true; SoundToggle2.IsChecked = _cfg.SoundOn; _syncSound = false;
        _syncVol = true;
        VolLow.IsChecked = _cfg.SoundVolume < 0.5;
        VolMid.IsChecked = _cfg.SoundVolume >= 0.5 && _cfg.SoundVolume < 0.85;
        VolHigh.IsChecked = _cfg.SoundVolume >= 0.85;
        _syncVol = false;

        RefreshUpdateCard();
        ShowView(SettingsView);
    }

    private void UpdateNameHint() =>
        NameHint.Text = string.IsNullOrWhiteSpace(_cfg.UserName)
            ? $"Em branco, usamos o nome do Windows: {_winName}"
            : "É assim que você aparece no menu e na tela de boas-vindas.";

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _syncName) return;
        _cfg.UserName = (NameBox.Text ?? "").Trim();
        SaveCfg();
        ApplyProfile();
        UpdateNameHint();
    }

    private void UseWindowsName_Click(object sender, RoutedEventArgs e)
    {
        NameBox.Text = "";   // dispara NameBox_TextChanged, que salva e atualiza tudo
        ShowToast("Usando o nome do Windows");
    }

    private void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Escolha sua foto",
            Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Todos os arquivos|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        var error = ProfileService.SaveCustomAvatar(dlg.FileName);
        if (error != null)
        {
            ShowToast("Não foi possível usar essa imagem.");
            Sfx.Play(Sound.Error);
            return;
        }
        _cfg.CustomAvatar = true;
        SaveCfg();
        ReloadAvatar();
        ApplyProfile();
        WinPhotoButton.IsEnabled = true;
        PopAvatar();
        Sfx.Play(Sound.Theme);
        ShowToast("Foto atualizada");
    }

    private void UseWindowsPhoto_Click(object sender, RoutedEventArgs e)
    {
        _cfg.CustomAvatar = false;
        SaveCfg();
        ProfileService.DeleteCustomAvatar();
        ReloadAvatar();
        ApplyProfile();
        WinPhotoButton.IsEnabled = false;
        PopAvatar();
        ShowToast(_winAvatar != null ? "Voltamos para a foto do Windows" : "Sem foto: usando suas iniciais");
    }

    // =====================================================================
    //  Atualizações
    // =====================================================================
    private bool _updating;

    private void RefreshUpdateCard()
    {
        VersionText.Text = "Versão " + AppInfo.Version;
        if (_updating) return;
        if (!AppInfo.UpdatesConfigured) { UpdateStatus.Text = "A atualização automática ainda não foi configurada."; UpdateCheckButton.IsEnabled = false; }
        else if (!UpdateService.CanUpdate) { UpdateStatus.Text = "Disponível no programa instalado pelo Setup."; UpdateCheckButton.IsEnabled = false; }
        else
        {
            UpdateCheckButton.IsEnabled = true;
            if (UpdateApplyButton.Visibility != Visibility.Visible) UpdateStatus.Text = "Você pode procurar versões novas a qualquer momento.";
        }
    }

    /// <summary>Procura versão nova. "silent" = no início do programa: só avisa se achar algo.</summary>
    private async Task CheckUpdateAsync(bool silent)
    {
        if (_updating || !UpdateService.CanUpdate) { if (!silent) RefreshUpdateCard(); return; }
        if (!silent) { UpdateStatus.Text = "Procurando…"; UpdateCheckButton.IsEnabled = false; }

        var version = await UpdateService.CheckAsync();

        if (version != null)
        {
            UpdateStatus.Text = $"A versão {version} está disponível.";
            UpdateApplyButton.Visibility = Visibility.Visible;
            ShowToast($"Nova versão {version} disponível — veja em Personalizar");
        }
        else if (!silent)
        {
            UpdateStatus.Text = "Você está com a versão mais recente.";
            ShowToast("Tudo em dia");
        }
        if (!silent) UpdateCheckButton.IsEnabled = true;
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e) => await CheckUpdateAsync(silent: false);

    private async void UpdateApply_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _updating = true;
        UpdateApplyButton.IsEnabled = false;
        UpdateCheckButton.IsEnabled = false;
        UpdateStatus.Text = "Baixando… 0%";
        try
        {
            await UpdateService.DownloadAndRestartAsync(p => Dispatcher.BeginInvoke(new Action(() => UpdateStatus.Text = $"Baixando… {p}%")));
        }
        catch (Exception ex)
        {
            Log.Write("Falha ao atualizar", ex);
            _updating = false;
            UpdateApplyButton.IsEnabled = true;
            UpdateCheckButton.IsEnabled = true;
            UpdateStatus.Text = "Não foi possível atualizar agora. Tente de novo em instantes.";
            Sfx.Play(Sound.Error);
        }
    }

    private void PopAvatar()
    {
        var pop = new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(420))
        { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
        SetAvatarScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        SetAvatarScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    // =====================================================================
    //  Tela inicial (boas-vindas com foto e nome do usuário do Windows)
    // =====================================================================
    private bool _introDone;

    private void AnimateIntro()
    {
        // a foto "salta" para dentro e os textos entram em sequência
        AvatarScale.ScaleX = 0.6; AvatarScale.ScaleY = 0.6;
        var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(600))
        { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }, BeginTime = TimeSpan.FromMilliseconds(120) };
        AvatarScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        AvatarScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        int i = 0;
        foreach (var el in new FrameworkElement[] { IntroHello, IntroName, IntroLine, IntroSub, IntroButton, IntroFooter })
        {
            var tt = new TranslateTransform(0, 16);
            el.RenderTransform = tt;
            el.Opacity = 0;
            var delay = TimeSpan.FromMilliseconds(260 + i++ * 90);
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { BeginTime = delay });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = delay, EasingFunction = EaseOut });
        }
    }

    private void IntroStart_Click(object sender, RoutedEventArgs e)
    {
        if (_introDone) return;
        _introDone = true;
        Sfx.Play(Sound.Start);
        Startup();   // segue o fluxo de antes: configuração inicial ou tela de programas
        _ = CheckUpdateAsync(silent: true);   // em segundo plano; só avisa se houver versão nova
    }

    // =====================================================================
    //  Inicialização
    // =====================================================================
    private void Startup()
    {
        _cfg = ConfigService.Load();
        if (IsDir(_cfg.Base))
        {
            RefreshHome();
            ShowView(MenuView);
        }
        else
        {
            WelcomePathBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (ConfigService.Exists && !string.IsNullOrWhiteSpace(_cfg.Base))
                WelcomeError.Text = "A pasta de projetos salva não está disponível. Escolha um novo local.";
            ShowView(WelcomeView);
        }
    }

    // Um disco de rede desconectado pode demorar muito para responder e congelar a janela.
    // Esperamos no máximo 1,5 s e lembramos por 20 s que aquele disco não responde.
    private readonly Dictionary<string, DateTime> _deadRoots = new(StringComparer.OrdinalIgnoreCase);

    private bool IsDir(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var root = Path.GetPathRoot(path) ?? "";
            if (_deadRoots.TryGetValue(root, out var when) && (DateTime.Now - when).TotalSeconds < 20) return false;
            var task = Task.Run(() => Directory.Exists(path));
            if (task.Wait(1500)) return task.Result;
            _deadRoots[root] = DateTime.Now;
            Log.Write("Local não respondeu a tempo: " + path);
            return false;
        }
        catch { return false; }
    }

    // =====================================================================
    //  Navegação com animação
    // =====================================================================
    private int _navToken;

    private void ShowView(Grid next)
    {
        int token = ++_navToken;   // se o usuário clicar rápido, só a última navegação vale

        void Enter()
        {
            foreach (var v in new[] { IntroView, MenuView, WelcomeView, NameFolderView, HomeView, WizardView, SuccessView, SettingsView })
                if (v != next) v.Visibility = Visibility.Collapsed;

            next.Visibility = Visibility.Visible;
            var tt = (TranslateTransform)next.RenderTransform;
            next.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = EaseOut });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = EaseOut });
            _current = next;

            if (next == IntroView) AnimateIntro();
            if (next == MenuView) AnimateMenu();
            if (next == MenuView) StartTitleShine(MenuTitle, MenuLine); else StopTitleShine(MenuTitle, MenuLine);
            if (next == WelcomeView) StartTitleShine(WelcomeTitle, WelcomeLine); else StopTitleShine(WelcomeTitle, WelcomeLine);
            if (next == HomeView) Dispatcher.BeginInvoke(new Action(AnimateCards), System.Windows.Threading.DispatcherPriority.Loaded);
            if (next == SuccessView)
            {
                CheckScale.ScaleX = 0; CheckScale.ScaleY = 0;
                var pop = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520))
                { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }, BeginTime = TimeSpan.FromMilliseconds(120) };
                CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
        }

        if (_current == null || _current == next || _current.Visibility != Visibility.Visible) { Enter(); return; }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => { if (token == _navToken) Enter(); };
        _current.BeginAnimation(OpacityProperty, fade);
    }

    // ----- menu: entrada em sequência -----
    private void AnimateMenu()
    {
        int i = 0;
        foreach (var el in new FrameworkElement[] { MenuHead, MenuHero, MenuRow, MenuFoot })
        {
            var tt = new TranslateTransform(0, 18);
            el.RenderTransform = tt;
            el.Opacity = 0;
            var delay = TimeSpan.FromMilliseconds(80 + i++ * 90);
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360)) { BeginTime = delay });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = delay, EasingFunction = EaseOut });
        }
        MenuBasePath.Text = _cfg.Base;
        MenuGreeting.Text = $"{Greeting()}, {CurrentName()}";
    }

    // ----- título "Project Organizer": brilho que passa pelas letras, flutuação leve e traço que respira -----
    private void StartTitleShine(TextBlock title, Border line)
    {
        var text = (Color)ColorConverter.ConvertFromString("#F4F4F6");
        var glint = Mix(_accent, Colors.White, 0.65);

        var sweep = new TranslateTransform(-1, 0);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), RelativeTransform = sweep };
        brush.GradientStops.Add(new GradientStop(text, 0.00));
        brush.GradientStops.Add(new GradientStop(text, 0.38));
        brush.GradientStops.Add(new GradientStop(glint, 0.50));
        brush.GradientStops.Add(new GradientStop(text, 0.62));
        brush.GradientStops.Add(new GradientStop(text, 1.00));
        title.Foreground = brush;

        var pass = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        pass.KeyFrames.Add(new LinearDoubleKeyFrame(-1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pass.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.7)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        pass.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5.0))));   // pausa entre uma passada e outra
        sweep.BeginAnimation(TranslateTransform.XProperty, pass);

        var shift = new TranslateTransform();
        title.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -4, TimeSpan.FromSeconds(2.4))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });

        var scale = new ScaleTransform(1, 1);
        line.RenderTransformOrigin = new Point(0.5, 0.5);
        line.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1.5, TimeSpan.FromSeconds(1.9))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    private void StopTitleShine(TextBlock title, Border line)
    {
        if (title.Foreground is LinearGradientBrush { RelativeTransform: TranslateTransform sweep })
            sweep.BeginAnimation(TranslateTransform.XProperty, null);
        if (title.RenderTransform is TranslateTransform shift) shift.BeginAnimation(TranslateTransform.YProperty, null);
        if (line.RenderTransform is ScaleTransform scale) scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        title.Foreground = (Brush)FindResource("TextBrush");
    }

    private void AnimateCards()
    {
        for (int i = 0; i < AppsList.Items.Count; i++)
        {
            if (AppsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement el) continue;
            var tt = new TranslateTransform(0, 22);
            el.RenderTransform = tt;
            el.Opacity = 0;
            var delay = TimeSpan.FromMilliseconds(40 + i * 55);
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = delay });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(380)) { BeginTime = delay, EasingFunction = EaseOut });
        }
    }

    // =====================================================================
    //  Barra de título
    // =====================================================================
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // =====================================================================
    //  Boas-vindas
    // =====================================================================
    private void WelcomeBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Diretório principal dos projetos" };
        if (IsDir(WelcomePathBox.Text)) dlg.InitialDirectory = WelcomePathBox.Text;
        if (dlg.ShowDialog(this) == true) WelcomePathBox.Text = dlg.FolderName;
    }

    private void WelcomeStart_Click(object sender, RoutedEventArgs e)
    {
        var raw = Environment.ExpandEnvironmentVariables((WelcomePathBox.Text ?? "").Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathRooted(raw))
        { WelcomeError.Text = "Informe um caminho completo, como E:\\Trabalhos."; return; }
        try
        {
            var full = Path.GetFullPath(raw);
            if (File.Exists(full)) { WelcomeError.Text = "Esse caminho é um arquivo, não uma pasta."; return; }
            WelcomeError.Text = "";
            OpenNameFolder(full, fromHome: false);
        }
        catch (Exception ex) { Log.Write("Local inválido: " + raw, ex); WelcomeError.Text = "Não foi possível usar esse local: " + ex.Message; }
    }

    // =====================================================================
    //  Nomear a pasta dos projetos
    // =====================================================================
    private string _nameParent = "";
    private bool _nameFromHome;
    private const string SuggestedFolderName = "Projetos Design";

    private void OpenNameFolder(string parent, bool fromHome)
    {
        _nameParent = parent;
        _nameFromHome = fromHome;
        SuggestionText.Text = SuggestedFolderName;
        FolderNameBox.Text = SuggestedFolderName;
        NameFolderError.Text = "";
        UpdateFolderPreview();
        ShowView(NameFolderView);
        FolderNameBox.Focus();
        FolderNameBox.SelectAll();
    }

    private void UpdateFolderPreview()
    {
        if (FolderPreview == null) return;
        var name = (FolderNameBox.Text ?? "").Trim();
        FolderPreview.Text = name.Length == 0 ? _nameParent : Path.Combine(_nameParent, name);
    }

    private void FolderNameBox_TextChanged(object sender, TextChangedEventArgs e) { if (_ready) UpdateFolderPreview(); }

    private void UseSuggestion_Click(object sender, RoutedEventArgs e)
    {
        FolderNameBox.Text = SuggestedFolderName;
        FolderNameBox.Focus();
        FolderNameBox.CaretIndex = FolderNameBox.Text.Length;
    }

    private void NameFolderBack_Click(object sender, RoutedEventArgs e)
    {
        if (_nameFromHome) { RefreshHome(); ShowView(MenuView); }
        else ShowView(WelcomeView);
    }

    private void NameFolderCreate_Click(object sender, RoutedEventArgs e)
    {
        var name = (FolderNameBox.Text ?? "").Trim();
        if (name.Length == 0) { NameFolderError.Text = "Digite um nome para a pasta."; return; }
        if (!Validation.IsValidName(name)) { NameFolderError.Text = "Nome inválido. Evite caracteres como \\ / : * ? \" < > |"; return; }
        try
        {
            var full = Path.Combine(_nameParent, name);
            if (File.Exists(full)) { NameFolderError.Text = "Já existe um arquivo com esse nome nesse local."; return; }
            Directory.CreateDirectory(full);       // se a pasta já existir, ela é reaproveitada
            _cfg.Base = full;
            ConfigService.Save(_cfg);
            NameFolderError.Text = "";
            RefreshHome();
            ShowView(MenuView);
        }
        catch (Exception ex) { Log.Write("Falha ao criar a pasta dos projetos", ex); NameFolderError.Text = "Não foi possível criar a pasta: " + ex.Message; }
    }

    // =====================================================================
    //  Início
    // =====================================================================
    private string? ResolveAppDir(string app)
    {
        if (!_cfg.AppFolders.TryGetValue(app, out var stored) || string.IsNullOrWhiteSpace(stored)) return null;
        string? dir = null;
        if (Path.IsPathRooted(stored)) dir = stored;                                  // v7 em diante
        else if (Validation.IsValidName(stored)) dir = Path.Combine(_cfg.Base, stored); // registro antigo (v6)
        return IsDir(dir) ? dir : null;
    }

    /// <summary>Carrega Icons\&lt;nome&gt;.png embutido no .exe; sem arquivo, usa a sigla.</summary>
    private static ImageSource? LoadIcon(string app)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/Icons/{Presets.Slug(app)}.png", UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void RefreshHome()
    {
        var items = new List<AppItem>();
        foreach (var name in Presets.DefaultApps.Concat(_cfg.CustomApps))
            items.Add(new AppItem
            {
                Name = name,
                IsCustom = _cfg.CustomApps.Contains(name, StringComparer.OrdinalIgnoreCase),
                Badge = Presets.Badge(name),
                Icon = LoadIcon(name),
                Status = ResolveAppDir(name) != null ? "Pasta pronta" : "Primeiro projeto",
            });
        AppsList.ItemsSource = items;
        BasePathText.Text = _cfg.Base;
        MenuBasePath.Text = _cfg.Base;
    }

    private void AppCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string app }) StartWizard(app);
    }

    // ----- menu inicial -----
    private void MenuNew_Click(object sender, RoutedEventArgs e)
    {
        RefreshHome();
        ShowView(HomeView);
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!IsDir(_cfg.Base)) { MessageBox.Show(this, "A pasta dos projetos não está disponível agora.", "Finish", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            Process.Start(new ProcessStartInfo(_cfg.Base) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("Não foi possível abrir " + _cfg.Base, ex); MessageBox.Show(this, ex.Message, "Finish", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ----- remover programa cadastrado -----
    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        var ask = MessageBox.Show(this,
            $"Remover \"{name}\" da lista de programas?\n\nAs pastas já criadas no computador não serão apagadas.",
            "Finish", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        _cfg.CustomApps.RemoveAll(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        _cfg.AppFolders.Remove(name);
        try { ConfigService.Save(_cfg); }
        catch (Exception ex) { Log.Write("Não foi possível salvar após remover " + name, ex); MessageBox.Show(this, "Erro ao salvar: " + ex.Message, "Finish", MessageBoxButton.OK, MessageBoxImage.Warning); }

        RefreshHome();
        Dispatcher.BeginInvoke(new Action(AnimateCards), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ChangeBase_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Escolha o local da nova pasta de projetos" };
        if (IsDir(_cfg.Base)) dlg.InitialDirectory = Path.GetDirectoryName(_cfg.Base.TrimEnd('\\', '/')) ?? _cfg.Base;
        if (dlg.ShowDialog(this) != true) return;
        OpenNameFolder(dlg.FolderName, fromHome: true);
    }

    // ----- cadastrar programa -----
    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        NewAppBox.Text = "";
        AddAppError.Text = "";
        AddAppOverlay.Visibility = Visibility.Visible;
        AddAppOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        var s = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = EaseOut };
        OverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
        OverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
        NewAppBox.Focus();
    }

    private void CloseAddApp()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => AddAppOverlay.Visibility = Visibility.Collapsed;
        AddAppOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void AddAppCancel_Click(object sender, RoutedEventArgs e) => CloseAddApp();
    private void NewAppBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddAppConfirm_Click(sender, e); }

    private void AddAppConfirm_Click(object sender, RoutedEventArgs e)
    {
        var name = (NewAppBox.Text ?? "").Trim();
        if (name.Length == 0) { AddAppError.Text = "Digite o nome do programa."; return; }
        if (!Validation.IsValidName(name)) { AddAppError.Text = "Nome inválido. Evite caracteres como \\ / : * ? \" < > |"; return; }
        if (Presets.DefaultApps.Concat(_cfg.CustomApps).Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { AddAppError.Text = "Este programa já está cadastrado."; return; }

        _cfg.CustomApps.Add(name);
        try { ConfigService.Save(_cfg); }
        catch (Exception ex) { _cfg.CustomApps.Remove(name); AddAppError.Text = "Erro ao salvar cadastro: " + ex.Message; return; }

        CloseAddApp();
        RefreshHome();
        Dispatcher.BeginInvoke(new Action(AnimateCards), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // =====================================================================
    //  Assistente de novo projeto
    // =====================================================================
    private void StartWizard(string app)
    {
        _app = app;
        var dir = ResolveAppDir(app);
        _firstSetup = dir == null;
        _appDir = dir ?? "";
        _customParent = null;
        _selected.Clear();
        _projectName = "";
        _target = "";
        _parentDir = "";
        _clientOnly = false;

        WizardTitle.Text = "Novo projeto · " + app;
        ProjectNameBox.Text = "";
        ClientNameBox.Text = "";
        CustomFolderBox.Text = "";
        WizardError.Text = "";

        FirstSetupPanel.Visibility = _firstSetup ? Visibility.Visible : Visibility.Collapsed;
        ReuseInfo.Visibility = _firstSetup ? Visibility.Collapsed : Visibility.Visible;
        if (_firstSetup)
        {
            FirstSetupText.Text = $"Será criada uma pasta \"{app}\" uma única vez. Os próximos projetos entram nela automaticamente.";
            AppDirDefaultPath.Text = Path.Combine(_cfg.Base, app);
            AppDirCustomPath.Text = "Nenhum local escolhido ainda.";
            AppDirDefault.IsChecked = true;
            AppDirBrowse.Visibility = Visibility.Collapsed;
        }
        else ReusePath.Text = _appDir;

        OrgNew.IsChecked = true;                         // padrão: nomear a pasta central (cliente)
        OrgExisting.IsEnabled = true;
        ExistingPanel.Visibility = Visibility.Collapsed;
        NewClientPanel.Visibility = Visibility.Visible;

        ChipsPanel.Children.Clear();
        foreach (var f in Presets.For(app)) ChipsPanel.Children.Add(MakeChip(f, false));

        LoadExistingFolders();
        ShowView(WizardView);
        SetStep(0, animate: false);
        ClientNameBox.Focus();
    }

    private void WizardCancel_Click(object sender, RoutedEventArgs e) => GoApps();

    // ----- passo 1 -----
    private void AppDir_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppDirBrowse.Visibility = AppDirCustom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppDirBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Diretório onde ficará a pasta do programa" };
        if (IsDir(_cfg.Base)) dlg.InitialDirectory = _cfg.Base;
        if (dlg.ShowDialog(this) != true) return;
        _customParent = dlg.FolderName;
        AppDirCustomPath.Text = Path.Combine(_customParent, _app);
    }

    // ----- passo 2 -----
    private void Org_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ExistingPanel.Visibility = OrgExisting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NewClientPanel.Visibility = OrgNew.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (OrgNew.IsChecked == true) ClientNameBox.Focus();
    }

    private void ExistingList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOrgExamples();

    private void ClientNameBox_TextChanged(object sender, TextChangedEventArgs e) { if (_ready) UpdateOrgExamples(); }

    private void UpdateOrgExamples()
    {
        if (!_ready) return;
        var appName = string.IsNullOrEmpty(_appDir) ? _app : Path.GetFileName(_appDir.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(appName)) appName = _app;

        OrgExample1.Text = $"Exemplo: {appName}\\Projeto";

        OrgExample2.Text = ExistingList.SelectedItem is string g
            ? $"Exemplo: {appName}\\{g}\\Projeto"
            : "Escolha o cliente na lista abaixo.";

        var client = (ClientNameBox.Text ?? "").Trim();
        OrgExample3.Text = $"Exemplo: {appName}\\{(client.Length > 0 ? client : "Cliente")}\\Projeto";
    }

    private void LoadExistingFolders()
    {
        var groups = new List<string>();
        try
        {
            if (IsDir(_appDir))
                groups = new DirectoryInfo(_appDir).GetDirectories().Select(d => d.Name)
                    .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex) { Log.Write("Não foi possível listar " + _appDir, ex); /* sem permissão: tratada como vazia */ }

        ExistingList.ItemsSource = groups;

        if (groups.Count == 0)
        {
            OrgExistingDesc.Text = "Nenhum cliente encontrado ainda para este programa. Use a opção de novo cliente.";
            OrgExisting.IsEnabled = false;
            if (OrgExisting.IsChecked == true) OrgNew.IsChecked = true;
        }
        else
        {
            OrgExistingDesc.Text = "O projeto entra como uma nova pasta dentro do cliente escolhido.";
            OrgExisting.IsEnabled = true;
        }
        UpdateOrgExamples();
    }

    // ----- passo 3: chips -----
    private ToggleButton MakeChip(string name, bool isChecked)
    {
        var chip = new ToggleButton { Content = name, Tag = name, Style = (Style)FindResource("Chip") };
        chip.Checked += Chip_Toggled;
        chip.Unchecked += Chip_Toggled;
        if (isChecked) chip.IsChecked = true;
        return chip;
    }

    private void Chip_Toggled(object sender, RoutedEventArgs e)
    {
        var chip = (ToggleButton)sender;
        var name = (string)chip.Tag;
        if (chip.IsChecked == true) { if (!_selected.Contains(name)) _selected.Add(name); }
        else _selected.Remove(name);
        UpdateSummary();
    }

    private void CustomFolderBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddCustomFolder_Click(sender, e); }

    private void AddCustomFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = (CustomFolderBox.Text ?? "").Trim().Replace('/', '\\');
        if (!Validation.IsValidRelative(folder))
        { WizardError.Text = "Nome inválido. Use nomes de pasta válidos, separados por barra invertida para subpastas."; return; }

        if (_target.Length > 0 && Path.Combine(_target, folder).Length > ProjectCreator.MaxPathLength)
        { WizardError.Text = $"Esse nome deixaria o caminho longo demais (limite de {ProjectCreator.MaxPathLength} caracteres)."; return; }

        var existing = ChipsPanel.Children.OfType<ToggleButton>()
            .FirstOrDefault(c => string.Equals((string)c.Tag, folder, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (existing.IsChecked == true) { WizardError.Text = "Essa pasta já foi adicionada."; return; }
            existing.IsChecked = true;
        }
        else ChipsPanel.Children.Add(MakeChip(folder, true));

        WizardError.Text = "";
        CustomFolderBox.Text = "";
    }

    private void UpdateSummary()
    {
        if (SummaryTree == null || string.IsNullOrEmpty(_target)) return;
        SummaryTarget.Text = _target;

        var root = new Node();
        foreach (var f in _selected)
        {
            var n = root;
            foreach (var part in f.Split('\\'))
            {
                if (!n.Kids.TryGetValue(part, out var child)) n.Kids[part] = child = new Node();
                n = child;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(Path.GetFileName(_target));
        if (root.Kids.Count == 0) sb.AppendLine("└─ (projeto vazio)");
        else Render(root, "", sb);
        SummaryTree.Text = sb.ToString();
    }

    private sealed class Node { public readonly SortedDictionary<string, Node> Kids = new(StringComparer.OrdinalIgnoreCase); }

    private static void Render(Node n, string prefix, StringBuilder sb)
    {
        int i = 0;
        foreach (var kv in n.Kids)
        {
            bool last = ++i == n.Kids.Count;
            sb.Append(prefix).Append(last ? "└─ " : "├─ ").AppendLine(kv.Key);
            Render(kv.Value, prefix + (last ? "   " : "│  "), sb);
        }
    }

    // =====================================================================
    //  Passos: 1 Cliente (pasta central) -> 2 Projeto -> 3 Subpastas
    // =====================================================================
    private void SetStep(int step, bool animate = true)
    {
        _step = step;
        if (step < 2) _clientOnly = false;
        WizardError.Text = "";
        WizardSubtitle.Text = $"Etapa {step + 1} de 3";

        StepClient.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepProject.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;

        if (step == 1)
        {
            ProjectContext.Text = "Será criado em: " + _parentDir;
            BlankHint.Visibility = OrgNew.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ProjectNameBox.Focus();
        }

        var active = (Brush)FindResource("AccentBrush");
        var dim = (Brush)FindResource("TextDimBrush");
        StepLbl1.Foreground = step >= 0 ? active : dim;
        StepLbl2.Foreground = step >= 1 ? active : dim;
        StepLbl3.Foreground = step >= 2 ? active : dim;

        var target = (step + 1) / 3.0;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(animate ? 380 : 1)) { EasingFunction = EaseOut };
        StepProgress.BeginAnimation(ScaleTransform.ScaleXProperty, anim);

        UIElement panel = step == 0 ? StepClient : step == 1 ? StepProject : Step3;
        if (animate)
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut });
        else panel.BeginAnimation(OpacityProperty, null);

        BackButton.Content = step == 0 ? "Cancelar" : "Voltar";
        UpdateNextLabel();
    }

    /// <summary>No passo do projeto, com novo cliente e nome em branco, o botão cria só a pasta do cliente.</summary>
    private void UpdateNextLabel()
    {
        if (NextButton == null) return;
        bool onlyClient = _step == 1 && OrgNew.IsChecked == true && string.IsNullOrWhiteSpace(ProjectNameBox.Text);
        NextButton.Content = _step == 2 ? "Criar projeto" : onlyClient ? "Criar só a pasta do cliente" : "Avançar";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 0) GoApps();
        else SetStep(_step - 1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (WizardView.Visibility != Visibility.Visible) return;
        switch (_step)
        {
            case 0: if (ValidateClientStep()) SetStep(1); break;
            case 1:
                if (!ValidateProjectStep()) break;
                if (_clientOnly) CreateClientOnly();
                else { UpdateSummary(); SetStep(2); }
                break;
            case 2: CreateProject(); break;
        }
    }

    private bool Fail(string msg) { WizardError.Text = ""; WizardError.Text = msg; return false; }   // limpar antes garante o som mesmo se o erro se repetir

    // ----- passo 1: pasta central (cliente / empresa) -----
    private bool ValidateClientStep()
    {
        if (_firstSetup)
        {
            string baseParent;
            if (AppDirCustom.IsChecked == true)
            {
                if (_customParent == null) return Fail("Escolha o local clicando em \"Procurar pasta…\".");
                baseParent = _customParent;
            }
            else baseParent = _cfg.Base;

            var dir = Path.Combine(baseParent, _app);
            if (File.Exists(dir)) return Fail("Já existe um arquivo com esse nome no destino.");
            _appDir = dir;
        }

        if (OrgExisting.IsChecked == true)
        {
            if (ExistingList.SelectedItem is not string group) return Fail("Selecione um cliente da lista.");
            _parentDir = Path.Combine(_appDir, group);
        }
        else if (OrgNew.IsChecked == true)
        {
            var client = (ClientNameBox.Text ?? "").Trim();
            if (client.Length == 0) return Fail("Digite o nome do cliente ou empresa.");
            if (!Validation.IsValidName(client)) return Fail("Nome de pasta inválido. Evite caracteres como \\ / : * ? \" < > |");
            _parentDir = Path.Combine(_appDir, client);
            if (Directory.Exists(_parentDir) || File.Exists(_parentDir))
                return Fail("Esse cliente já existe. Volte e use a opção \"Dentro de um cliente que já existe\".");
        }
        else _parentDir = _appDir;

        return true;
    }

    // ----- passo 2: nome do projeto -----
    private bool ValidateProjectStep()
    {
        var name = (ProjectNameBox.Text ?? "").Trim();

        if (name.Length == 0)
        {
            if (OrgNew.IsChecked == true)
            {   // novo cliente sem projeto: cria só a pasta central
                _clientOnly = true;
                _projectName = "";
                _target = _parentDir;
                return true;
            }
            return Fail("Digite o nome do projeto.");
        }

        _clientOnly = false;
        if (!Validation.IsValidName(name)) return Fail("Nome do projeto inválido. Evite caracteres como \\ / : * ? \" < > |");

        var target = Path.Combine(_parentDir, name);
        if (Directory.Exists(target) || File.Exists(target)) return Fail("Este projeto já existe. Nada foi alterado.");

        var tooLong = ProjectCreator.CheckLength(target, _selected);
        if (tooLong != null) return Fail(tooLong);

        _projectName = name;
        _target = target;
        return true;
    }

    // =====================================================================
    //  Criação
    // =====================================================================
    private void CreateProject()
    {
        if (Directory.Exists(_target) || File.Exists(_target)) { WizardError.Text = "Este projeto já existe. Nada foi alterado."; return; }

        var tooLong = ProjectCreator.CheckLength(_target, _selected);
        if (tooLong != null) { WizardError.Text = tooLong; return; }

        // se qualquer pasta falhar, tudo o que foi criado é desfeito (e dá para tentar de novo)
        var error = ProjectCreator.Create(_target, _selected);
        if (error != null) { WizardError.Text = "Não foi possível criar o projeto (nada foi deixado pela metade): " + error; return; }

        RememberAppDir();

        SuccessTitle.Text = "Projeto criado!";
        SuccessPath.Text = _target;
        SuccessCount.Text = _selected.Count == 0 ? "Projeto vazio criado." :
            _selected.Count == 1 ? "1 subpasta criada." : $"{_selected.Count} subpastas criadas.";
        ShowView(SuccessView);
        Sfx.Play(Sound.Success);
    }

    private void CreateClientOnly()
    {
        if (Directory.Exists(_target) || File.Exists(_target)) { WizardError.Text = "Essa pasta já existe. Nada foi alterado."; return; }

        var error = ProjectCreator.Create(_target, Array.Empty<string>());
        if (error != null) { WizardError.Text = "Erro ao criar a pasta: " + error; return; }

        RememberAppDir();

        SuccessTitle.Text = "Pasta do cliente criada!";
        SuccessPath.Text = _target;
        SuccessCount.Text = "Para criar projetos nela, escolha \"Dentro de um cliente que já existe\".";
        ShowView(SuccessView);
        Sfx.Play(Sound.Success);
    }

    /// <summary>Guarda a pasta do programa na configuração (só depois de a criação dar certo).</summary>
    private void RememberAppDir()
    {
        if (_cfg.AppFolders.TryGetValue(_app, out var saved) && saved == _appDir) return;
        _cfg.AppFolders[_app] = _appDir;
        try { ConfigService.Save(_cfg); } catch (Exception ex) { Log.Write("Não foi possível salvar a pasta do programa", ex); /* o projeto já foi criado */ }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Não foi possível abrir " + _target, ex); MessageBox.Show(this, ex.Message, "Finish", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CreateAnother_Click(object sender, RoutedEventArgs e) => StartWizard(_app);

    /// <summary>Volta para o menu inicial.</summary>
    private void GoHome_Click(object sender, RoutedEventArgs e)
    {
        RefreshHome();
        ShowView(MenuView);
    }

    /// <summary>Volta para a lista de programas (usada ao cancelar o assistente).</summary>
    private void GoApps()
    {
        RefreshHome();
        ShowView(HomeView);
    }
}

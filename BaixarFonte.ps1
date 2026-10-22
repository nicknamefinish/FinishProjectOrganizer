# Baixa a fonte Outfit (SIL Open Font License - uso livre, inclusive comercial) para a pasta Fonts
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$dir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Fonts'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

# 1) arquivos estaticos (Regular + Bold) - melhor qualidade no WPF
$ok = $true
try {
  $base = 'https://github.com/Outfitio/Outfit-Fonts/raw/main/fonts/ttf/'
  foreach ($f in 'Outfit-Regular','Outfit-Bold') {
    Invoke-WebRequest -UseBasicParsing -Uri ($base + $f + '.ttf') -OutFile (Join-Path $dir ($f + '.ttf'))
  }
} catch { $ok = $false }

# 2) plano B: arquivo variavel do Google Fonts (o WPF usa o peso Regular)
if (-not $ok) {
  Remove-Item (Join-Path $dir 'Outfit-*.ttf') -ErrorAction SilentlyContinue
  try {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/google/fonts/raw/main/ofl/outfit/Outfit%5Bwght%5D.ttf' -OutFile (Join-Path $dir 'Outfit-Regular.ttf')
    $ok = $true
  } catch { $ok = $false }
}
if ($ok) { Write-Host 'Fonte Outfit pronta.' } else { Write-Host 'Nao foi possivel baixar a fonte Outfit.'; exit 1 }

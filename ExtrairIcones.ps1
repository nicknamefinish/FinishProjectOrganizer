# Extrai os icones dos programas JA INSTALADOS neste PC e salva em Icons\<nome>.png
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class IconX {
  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  public static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] phicon, int[] piconid, uint nIcons, uint flags);
  [DllImport("user32.dll")]
  public static extern bool DestroyIcon(IntPtr h);
}
"@

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $root 'Icons'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$pf  = $env:ProgramFiles
$pf2 = ${env:ProgramFiles(x86)}

$apps = [ordered]@{
  'adobe-photoshop'            = @("$pf\Adobe\Adobe Photoshop*\Photoshop.exe")
  'adobe-after-effects'        = @("$pf\Adobe\Adobe After Effects*\Support Files\AfterFX.exe")
  'adobe-premiere'             = @("$pf\Adobe\Adobe Premiere Pro*\Adobe Premiere Pro.exe")
  'davinci-resolve'            = @("$pf\Blackmagic Design\DaVinci Resolve\Resolve.exe")
  'adobe-illustrator'          = @("$pf\Adobe\Adobe Illustrator*\Support Files\Contents\Windows\Illustrator.exe")
  'blender'                    = @("$pf\Blender Foundation\Blender*\blender.exe")
  'autodesk-maya'              = @("$pf\Autodesk\Maya*\bin\maya.exe")
  'zbrush'                     = @("$pf\Maxon ZBrush*\ZBrush.exe", "$pf\Pixologic\ZBrush*\ZBrush.exe", "$pf\Maxon\ZBrush*\ZBrush.exe")
  'adobe-substance-3d-painter' = @("$pf\Adobe\Adobe Substance 3D Painter\Adobe Substance 3D Painter.exe")
}

function Save-ExeIcon([string]$exe, [string]$png) {
  $h = New-Object IntPtr[] 1
  $id = New-Object int[] 1
  $n = [IconX]::PrivateExtractIcons($exe, 0, 256, 256, $h, $id, 1, 0)
  $ic = $null
  if ($n -ge 1 -and $h[0] -ne [IntPtr]::Zero) { $ic = [System.Drawing.Icon]::FromHandle($h[0]) }
  else { $ic = [System.Drawing.Icon]::ExtractAssociatedIcon($exe) }
  if (-not $ic) { return $false }
  $bmp = $ic.ToBitmap()
  $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  if ($h[0] -ne [IntPtr]::Zero) { [void][IconX]::DestroyIcon($h[0]) }
  return $true
}

$ok = 0
foreach ($k in $apps.Keys) {
  $found = $null
  foreach ($pattern in $apps[$k]) {
    $found = Get-ChildItem -Path $pattern | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($found) { break }
  }
  if ($found -and (Save-ExeIcon $found.FullName (Join-Path $out "$k.png"))) {
    Write-Host "[OK]  $k  <-  $($found.FullName)"
    $ok++
  } else {
    Write-Host "[--]  $k  (nao encontrado; usando a sigla). Coloque Icons\$k.png manualmente se quiser."
  }
}
Write-Host ""
Write-Host "$ok icone(s) extraido(s) para: $out"

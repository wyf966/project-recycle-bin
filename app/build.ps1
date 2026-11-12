param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path "$dist\ui" | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ "/out:$dist\ScriptRecycleBin.exe" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/reference:$root\lib\Microsoft.Web.WebView2.Core.dll" "$root\src\RbWeb.cs" "$root\src\VersionInfo.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
Copy-Item "$root\lib\*.dll" $dist -Force
Copy-Item "$root\lib\WebView2-LICENSE.txt", "$root\UPSTREAM-LICENSE.txt" $dist -Force
$template = Get-Content -Raw -Encoding UTF8 "$root\src\ui\template.html"
$logic = Get-Content -Raw -Encoding UTF8 "$root\src\ui\logic.js"
[IO.File]::WriteAllText("$dist\ui\index.html", $template.Replace('/* LOGIC */', $logic), (New-Object System.Text.UTF8Encoding $false))
Write-Host "Built $dist\ScriptRecycleBin.exe"

param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\release'),
    [string]$Wix = 'wix'
)

$ErrorActionPreference = 'Stop'
$wixCommand = (Get-Command $Wix -CommandType Application).Source
$output = New-Item -ItemType Directory -Path $OutputDirectory -Force
$tempRoot = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).ProviderPath
$stage = New-Item -ItemType Directory -Path (Join-Path $tempRoot ('ProjectRecycleBin-' + [guid]::NewGuid()))

try {
    & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $stage.FullName
    $exe = Get-Item -LiteralPath (Join-Path $stage.FullName 'ScriptRecycleBin.exe')
    $version = $exe.VersionInfo
    $msiVersion = "$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart)"
    $installer = Join-Path $output.FullName "ProjectRecycleBin-$($version.ProductVersion)-x64.msi"
    & $wixCommand build (Join-Path $PSScriptRoot 'installer\Package.wxs') -arch x64 `
        -d "ProductVersion=$msiVersion" -d "PayloadDir=$($stage.FullName)" `
        -intermediateFolder (Join-Path $stage.FullName 'wix') -pdbtype none -out $installer
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed' }
    Write-Host "Built $installer"
}
finally {
    $resolvedStage = (Resolve-Path -LiteralPath $stage.FullName).ProviderPath
    if ([IO.Path]::GetDirectoryName($resolvedStage) -eq $tempRoot.TrimEnd('\')) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

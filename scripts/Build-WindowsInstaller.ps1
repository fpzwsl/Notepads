[CmdletBinding()]
param(
    [ValidateSet('Production', 'Release', 'Debug')]
    [string]$Configuration = 'Production',
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = 'x64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\Artifacts'),
    [switch]$NoSign
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $repoRoot 'src\Notepads.sln'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Resolve-MsBuildPath {
    param([Parameter(Mandatory = $true)][string]$Candidate)

    if (-not $Candidate) { return $null }
    $trimmed = $Candidate.Trim().Trim('"')
    if (-not $trimmed) { return $null }
    if (-not (Test-Path -LiteralPath $trimmed -PathType Leaf)) { return $null }
    return (Resolve-Path -LiteralPath $trimmed).Path
}

function Resolve-UwpMsBuildPath {
    param([Parameter(Mandatory = $true)][string]$Candidate)

    $msbuildPath = Resolve-MsBuildPath -Candidate $Candidate
    if (-not $msbuildPath) { return $null }

    # This solution contains legacy UWP XAML projects, so a generic MSBuild is insufficient.
    $msbuildRoot = Split-Path -Parent $msbuildPath
    while ($msbuildRoot -and (Split-Path -Leaf $msbuildRoot) -ne 'MSBuild') {
        $parent = Split-Path -Parent $msbuildRoot
        if ($parent -eq $msbuildRoot) { return $null }
        $msbuildRoot = $parent
    }
    if (-not $msbuildRoot) { return $null }

    $xamlTargets = Get-ChildItem -LiteralPath (Join-Path $msbuildRoot 'Microsoft\WindowsXaml') `
        -Filter 'Microsoft.Windows.UI.Xaml.CSharp.targets' -File -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $xamlTargets) { return $null }

    return $msbuildPath
}

$msbuild = $null

$vswherePaths = @(foreach ($programFilesRoot in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
    if ($programFilesRoot) {
        $vswherePath = Join-Path $programFilesRoot 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswherePath -PathType Leaf) { $vswherePath }
    }
}) | Select-Object -Unique

foreach ($vswhere in $vswherePaths) {
    # Prefer a Visual Studio instance explicitly provisioned for UWP development.
    # Avoid -utf8 here: PowerShell may mis-decode UTF-8 stdout and corrupt non-ASCII install paths.
    $candidateOutput = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -requires Microsoft.VisualStudio.Workload.Universal -find 'MSBuild\**\Bin\MSBuild.exe' |
        Select-Object -First 1
    $msbuild = Resolve-UwpMsBuildPath -Candidate ([string]$candidateOutput)
    if ($msbuild) { break }
}

if (-not $msbuild) {
    foreach ($commandName in @('msbuild.exe', 'MSBuild.exe', 'msbuild', 'MSBuild')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $command) { continue }
        $pathFromCommand = if ($command.Source) { $command.Source } elseif ($command.Path) { $command.Path } else { $command.Definition }
        $msbuild = Resolve-UwpMsBuildPath -Candidate $pathFromCommand
        if ($msbuild) { break }
    }
}

if (-not $msbuild) {
    throw 'A Visual Studio installation with the Universal Windows Platform development workload is required. Install the workload (including the Windows 10/11 SDK) and run this script again.'
}

$certificatePath = Join-Path $output 'Notepads-Development.pfx'
$certificatePassword = 'NotepadsDevelopment!2026'
$publisher = 'CN=40E66D07-5A3A-4954-9CA3-A1EB15ED0804'

if (-not $NoSign) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $certificate) {
        $certificate = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'Notepads Development Package Certificate' -CertStoreLocation Cert:\CurrentUser\My
    }
    $securePassword = ConvertTo-SecureString $certificatePassword -AsPlainText -Force
    Export-PfxCertificate -Cert $certificate -FilePath $certificatePath -Password $securePassword | Out-Null
    Export-Certificate -Cert $certificate -FilePath (Join-Path $output 'Notepads-Development.cer') | Out-Null
}

& $msbuild $solution /t:Restore /p:Configuration=$Configuration /p:Platform=$Platform /v:minimal
if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed with exit code $LASTEXITCODE." }

$buildArgs = @(
    $solution,
    ('/p:Configuration=' + $Configuration),
    ('/p:Platform=' + $Platform),
    '/p:UapAppxPackageBuildMode=Sideload',
    '/p:AppxBundle=Always',
    ('/p:AppxBundlePlatforms=' + $Platform),
    ('/p:AppxPackageDir=' + $output + '\\'),
    ('/p:AppxPackageSigningEnabled=' + ([string](-not $NoSign)).ToLowerInvariant())
)
if (-not $NoSign) {
    $buildArgs += ('/p:PackageCertificateKeyFile=' + $certificatePath)
    $buildArgs += ('/p:PackageCertificatePassword=' + $certificatePassword)
}

& $msbuild @buildArgs /v:minimal
if ($LASTEXITCODE -ne 0) { throw "MSIX build failed with exit code $LASTEXITCODE." }

$bundle = Get-ChildItem $output -Recurse -Filter '*.msixbundle' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $bundle) { throw "Build completed but no .msixbundle was found under $output." }
Write-Host "Installer: $($bundle.FullName)"
if (-not $NoSign) { Write-Host "Certificate: $(Join-Path $output 'Notepads-Development.cer')" }

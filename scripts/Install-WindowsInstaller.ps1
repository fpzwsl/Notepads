[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundlePath,
    [string]$CertificatePath = (Join-Path (Split-Path $BundlePath) 'Notepads-Development.cer')
)

$ErrorActionPreference = 'Stop'
$bundle = (Resolve-Path $BundlePath).Path
if (-not (Test-Path $CertificatePath)) { throw "Certificate not found: $CertificatePath" }

$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertificatePath)
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'CurrentUser')
$store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
try {
    if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint })) {
        $store.Add($certificate)
    }
}
finally { $store.Close() }

Add-AppxPackage -Path $bundle
Write-Host 'Notepads was installed successfully.'

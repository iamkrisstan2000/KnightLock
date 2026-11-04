<#
    Signs a KnightLock build with the KnightWin certificate.

    The repository ships only the PUBLIC half (cert/KnightWin.cer) — a private key must never be
    committed. To sign, point this script at the .pfx that holds the private key:

        tools\sign.ps1 -Pfx C:\path\to\KnightWin.pfx -Password (Read-Host -AsSecureString)

    What to expect from a self-signed certificate:
      * Explorer's Properties -> Digital Signatures will show the KnightWin identity, and the binary
        is sealed against modification after signing (any edit invalidates it).
      * SmartScreen will still warn, because SmartScreen reputation and "publisher trust" require a
        certificate issued by a trusted CA. That is a trust problem, not a signing problem.
      * To make the certificate trusted on your own machines:
            Import-Certificate -FilePath cert\KnightWin.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
        Do NOT do this for a certificate you did not create.

    Requires signtool.exe (Windows SDK) on PATH or in a default SDK location.
#>

param(
    [string] $Exe = "",
    [Parameter(Mandatory = $true)][string] $Pfx,
    [System.Security.SecureString] $Password,
    [string] $TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Exe)) { $Exe = Join-Path $root "release\KnightLock.exe" }

if (-not (Test-Path $Exe)) { throw "Not found: $Exe (publish first, or pass -Exe)" }
if (-not (Test-Path $Pfx)) { throw "Not found: $Pfx" }

$signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue)?.Source
if (-not $signtool) {
    $signtool = Get-ChildItem "$env:ProgramFiles(x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "x64" } |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK or add it to PATH." }

# Note: the shipped binary has the version resource of the final exe already. Sign LAST — any later
# edit to the file, including the version-resource patch, invalidates the signature.
$plain = if ($Password) { [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
              [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)) } else { $null }

$args = @("sign", "/fd", "SHA256", "/td", "SHA256", "/tr", $TimestampUrl, "/f", $Pfx)
if ($plain) { $args += @("/p", $plain) }
$args += $Exe

Write-Host "Signing $Exe" -ForegroundColor Cyan
& $signtool @args
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }

Write-Host "Verifying..." -ForegroundColor Cyan
& $signtool verify /pa /v $Exe

$hash = (Get-FileHash $Exe -Algorithm SHA256).Hash
Write-Host ""
Write-Host "Signed. SHA-256: $hash" -ForegroundColor Green
Write-Host "Remember to sign LAST: any further edit to the exe invalidates this signature."

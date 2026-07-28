#Requires -Version 7.0
<#
.SYNOPSIS
    Trusts the development certificate and installs the signed MSIX. Phase 0 gate 1.

.DESCRIPTION
    Two steps, and they fail for unrelated reasons:

      1. Import the PUBLIC certificate into LocalMachine\TrustedPeople. Needs administrator
         rights. On a managed device this can be blocked by policy, and whether it is blocked
         is itself gate 1 evidence — it cannot be assumed either way and it is not a defect in
         the package.

      2. Install the package with Add-AppxPackage. This exercises the sideload policy.

    The script reports which step failed, because a certificate-trust failure and a sideload
    failure call for completely different responses: the first is a device-policy conversation,
    the second may be a manifest or dependency problem.

    Only the public certificate is ever touched. The private key stays in CurrentUser\My and is
    never exported, so nothing here can leak signing material.

.PARAMETER PackagePath
    The signed .msix. Defaults to the newest package in the AppPackages folder.

.PARAMETER PublicCertificatePath
    The exported public certificate written by New-DevelopmentCertificate.ps1.

.PARAMETER SkipCertificateTrust
    Skip step 1. Use when the certificate is already trusted, or to test whether installation
    fails for a reason other than trust.

.PARAMETER Uninstall
    Remove the installed package instead of installing.

.EXAMPLE
    pwsh -File scripts/Install-DevelopmentPackage.ps1

.EXAMPLE
    pwsh -File scripts/Install-DevelopmentPackage.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$PublicCertificatePath = (Join-Path $env:LOCALAPPDATA 'OutlookWidget\signing\OutlookWidget-Development.cer'),
    [switch]$SkipCertificateTrust,
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageName = '415Group.OutlookInboxWidget'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Output "Elevated: $isAdmin"
Write-Output ''

# ---------------------------------------------------------------------------
# Uninstall
# ---------------------------------------------------------------------------

if ($Uninstall) {
    $installed = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue

    if (-not $installed) {
        Write-Output "Not installed: $packageName"
        exit 0
    }

    Write-Output "Removing $($installed.PackageFullName)..."
    Remove-AppxPackage -Package $installed.PackageFullName
    Write-Output 'Removed.'
    Write-Output ''
    Write-Output 'Note: this removes package-local cache and settings, and widget pins. It does'
    Write-Output 'NOT revoke tenant consent and does NOT remove the Windows or WAM account.'
    exit 0
}

# ---------------------------------------------------------------------------
# Step 1: trust the public certificate
# ---------------------------------------------------------------------------

if ($SkipCertificateTrust) {
    Write-Output 'Step 1: skipped by request.'
}
elseif (-not $isAdmin) {
    Write-Output 'Step 1: BLOCKED — not elevated.'
    Write-Output 'Trusting a certificate in LocalMachine\TrustedPeople requires administrator rights.'
    Write-Output 'Re-run this script from an elevated session, or pass -SkipCertificateTrust if the'
    Write-Output 'certificate is already trusted.'
    exit 2
}
else {
    if (-not (Test-Path -LiteralPath $PublicCertificatePath)) {
        throw "Public certificate not found: $PublicCertificatePath. Run scripts/New-DevelopmentCertificate.ps1 first."
    }

    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($PublicCertificatePath)

    if ($certificate.HasPrivateKey) {
        # Should be impossible with a .cer, but a private key reaching a machine-wide store would
        # be a genuine credential exposure, so refuse rather than assume.
        throw 'The supplied certificate file contains a private key. Only the public certificate may be imported.'
    }

    Write-Output "Step 1: trusting $($certificate.Subject)"
    Write-Output "  Thumbprint: $($certificate.Thumbprint)"
    Write-Output "  Expires:    $($certificate.NotAfter.ToString('yyyy-MM-dd'))"

    try {
        # TrustedPeople rather than Root: it is the store the MSIX sideload path consults, and it
        # does not make this certificate trusted for arbitrary TLS or code across the machine.
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
        $store.Open('ReadWrite')
        $store.Add($certificate)
        $store.Close()

        Write-Output '  Imported into LocalMachine\TrustedPeople.'
    }
    catch {
        Write-Output ''
        Write-Output 'Step 1 FAILED. This is Phase 0 gate 1 evidence, not a packaging defect.'
        Write-Output "  $($_.Exception.GetType().Name): $($_.Exception.Message)"
        Write-Output ''
        Write-Output 'If managed-device policy prevents trusting a certificate on this machine, gate 1'
        Write-Output 'fails as a UNIVERSAL gate: it stops the product rather than triggering the tray'
        Write-Output 'fallback, because the fallback is also a packaged MSIX using the same certificate.'
        exit 1
    }
}

# ---------------------------------------------------------------------------
# Step 2: install the package
# ---------------------------------------------------------------------------

if (-not $PackagePath) {
    $candidates = @(Get-ChildItem -Path (Join-Path $repoRoot 'src\OutlookWidget.Package\AppPackages') `
        -Filter '*.msix' -ErrorAction SilentlyContinue | Sort-Object -Property LastWriteTime -Descending)

    if ($candidates.Count -eq 0) {
        throw 'No .msix found. Run scripts/Build-Package.ps1 first.'
    }

    $PackagePath = $candidates[0].FullName
}

if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Package not found: $PackagePath"
}

Write-Output ''
Write-Output "Step 2: installing $(Split-Path -Leaf $PackagePath)"

$existing = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Output "  Replacing installed version $($existing.Version)."
    Write-Output '  MSIX will not install a lower version over a higher one; if this fails with a'
    Write-Output '  version error, remove the package first and record that rollback loses widget pins.'
}

try {
    Add-AppxPackage -Path $PackagePath -ErrorAction Stop
    Write-Output '  Installed.'
}
catch {
    Write-Output ''
    Write-Output 'Step 2 FAILED.'
    Write-Output "  $($_.Exception.GetType().Name): $($_.Exception.Message)"
    Write-Output ''
    Write-Output 'Distinguish the causes before changing anything:'
    Write-Output '  - certificate not trusted        -> step 1 did not take effect'
    Write-Output '  - sideloading blocked by policy  -> device policy, same class as a gate 1 failure'
    Write-Output '  - publisher mismatch             -> manifest Publisher differs from the certificate Subject'
    Write-Output '  - missing dependency             -> a framework package the manifest requires'
    exit 1
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

$installed = Get-AppxPackage -Name $packageName

Write-Output ''
Write-Output 'Gate 1 result: PASS'
Write-Output "  Package full name: $($installed.PackageFullName)"
Write-Output "  Family:            $($installed.PackageFamilyName)"
Write-Output "  Install location:  $($installed.InstallLocation)"
Write-Output "  Signed by:         $($installed.Publisher)"
Write-Output ''
Write-Output 'Record the package full name and the certificate thumbprint in docs/phase0-evidence.md.'
Write-Output 'Launch the app from the Start menu to confirm package activation and to see where the'
Write-Output 'packaged per-user local data directory actually resolves to.'

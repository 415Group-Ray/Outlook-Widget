#Requires -Version 7.0
<#
.SYNOPSIS
    Creates the development code-signing certificate and records the section 15 decisions.

.DESCRIPTION
    Produces a self-signed code-signing certificate in CurrentUser\My and exports only its
    PUBLIC part. The private key never leaves the certificate store, so no .pfx is created
    anywhere — least of all inside this OneDrive-backed repository.

    Three section 15 decisions are made here, and they are made now because they are
    effectively irreversible once a package is installed:

    1. Subject. Package identity is name plus publisher, and the manifest Publisher must
       exactly match the certificate Subject. Signing a later build with an enterprise
       certificate whose Subject differs produces a DIFFERENT package identity: it cannot
       upgrade the installed package, it installs alongside it, and the widget must be
       re-pinned and reconfigured.

       The default Subject below is a deliberate attempt at section 15's "match now" option:
       a plain organizational name that a future enterprise signer might plausibly also use.
       It is a guess, and an enterprise certificate carrying extra fields such as L, S, or a
       serial number will not match it. The guess costs nothing over a
       development-specific name and gives a chance of continuity where a
       development-specific name gives none.

    2. Validity. Long, because it is the only precondition that keeps the MSIX
       persistent-identity bridge available. That bridge needs BOTH certificates in hand
       while the OLD one is still valid, at any time before expiry. Letting this certificate
       lapse is what forecloses the option — not failing to build the bridge now.

    3. Key retention. CurrentUser\My, which is outside the repository and outside OneDrive.
       Losing this key has the same effect as letting it expire.

    The stated v1 position remains: expanding beyond one user may mean remove-and-reinstall.
    That is a defensible choice for a one-user tool, but it must be a choice rather than a
    discovery.

    Requires no elevation. Trusting the exported public certificate does, and that is a
    separate step performed by Install-DevelopmentPackage.ps1.

.PARAMETER Subject
    The certificate Subject. Must be byte-for-byte identical to the manifest Publisher.

.PARAMETER ValidYears
    Validity in years.

.PARAMETER PublicCertificatePath
    Where to write the exported public certificate. Defaults to a non-synced local path,
    NOT the repository: .cer files are gitignored, but the safer habit is to keep signing
    material out of the working tree entirely.

.PARAMETER Force
    Replace an existing certificate with the same Subject instead of reusing it.

.EXAMPLE
    pwsh -File scripts/New-DevelopmentCertificate.ps1

.EXAMPLE
    pwsh -File scripts/New-DevelopmentCertificate.ps1 -Subject 'CN=Contoso Ltd' -ValidYears 10
#>
[CmdletBinding()]
param(
    [string]$Subject = 'CN=415 Group, Inc.',
    [int]$ValidYears = 10,
    [string]$PublicCertificatePath = (Join-Path $env:LOCALAPPDATA 'OutlookWidget\signing\OutlookWidget-Development.cer'),
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# Refuse to write signing material into the repository or anywhere in OneDrive, regardless of
# what was passed. This is the one rule in section 15 with no acceptable exception.
$resolvedOutput = [System.IO.Path]::GetFullPath($PublicCertificatePath)

if ($resolvedOutput.StartsWith([System.IO.Path]::GetFullPath($repoRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write certificate material inside the repository: $resolvedOutput"
}

if ($env:OneDrive -and $resolvedOutput.StartsWith([System.IO.Path]::GetFullPath($env:OneDrive), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write certificate material into OneDrive: $resolvedOutput"
}

# ---------------------------------------------------------------------------
# Reuse or create
# ---------------------------------------------------------------------------

$existing = @(Get-ChildItem -Path 'Cert:\CurrentUser\My' |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey })

if ($existing.Count -gt 0 -and -not $Force) {
    $certificate = $existing | Sort-Object -Property NotAfter -Descending | Select-Object -First 1
    Write-Output "Reusing the existing certificate for '$Subject'."
    Write-Output 'Pass -Force to replace it. Replacing it discards the identity continuity that'
    Write-Output 'the persistent-identity option depends on, so do that only deliberately.'
}
else {
    if ($existing.Count -gt 0) {
        Write-Output "Replacing $($existing.Count) existing certificate(s) for '$Subject'."
        $existing | Remove-Item -Force
    }

    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears($ValidYears) `
        -FriendlyName 'Outlook Inbox Widget development signing'

    Write-Output "Created a code-signing certificate for '$Subject'."
}

# ---------------------------------------------------------------------------
# Export the public certificate only
# ---------------------------------------------------------------------------

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutput) | Out-Null
Export-Certificate -Cert $certificate -FilePath $resolvedOutput -Type CERT -Force | Out-Null

# ---------------------------------------------------------------------------
# Report the values that must be recorded in the evidence report
# ---------------------------------------------------------------------------

$record = [pscustomobject]@{
    Subject               = $certificate.Subject
    ManifestPublisher     = $certificate.Subject
    Thumbprint            = $certificate.Thumbprint
    NotBefore             = $certificate.NotBefore.ToString('yyyy-MM-dd')
    NotAfter              = $certificate.NotAfter.ToString('yyyy-MM-dd')
    PrivateKeyLocation    = 'Cert:\CurrentUser\My (never exported)'
    PublicCertificatePath = $resolvedOutput
    ContinuityPosition    = 'Accept the break as the stated v1 position; keep persistent identity available via long validity and key retention'
    Timestamping          = 'Decide at signing time. Without a timestamp the signature becomes invalid at expiry, so retained rollback packages stop being installable while the installed package keeps running'
}

Write-Output ''
Write-Output 'Record these in docs/phase0-evidence.md before the first install:'
Write-Output ''
$record | Format-List | Out-String -Width 200 | Write-Output

Write-Output 'The manifest Publisher must match the Subject byte for byte:'
Write-Output "  Publisher=`"$($certificate.Subject)`""
Write-Output ''
Write-Output 'Next: build and sign the package, then run Install-DevelopmentPackage.ps1 elevated'
Write-Output 'to trust the public certificate and install. Whether managed-device policy permits'
Write-Output 'trusting it is itself Phase 0 gate 1 evidence and cannot be assumed either way.'

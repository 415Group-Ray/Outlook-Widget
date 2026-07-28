#Requires -Version 7.0
<#
.SYNOPSIS
    Builds and signs the MSIX package.

.DESCRIPTION
    Assembles a package layout from published output, packs it with makeappx, and signs it with
    signtool. Both tools come from the Windows SDK.

    Packaging is done directly with the SDK tools rather than through a Visual Studio packaging
    project. That is a deliberate choice, not a workaround: it needs no VS installation, the
    layout is inspectable before packing, and every step is reproducible from a script that lives
    in the repository. The plan's build workflow already requires deploying a signed package
    rather than relying on an unpackaged run, and this is the smallest way to satisfy that.

    The script refuses to proceed when the manifest Publisher and the signing certificate Subject
    disagree, because that mismatch is silent and expensive: package identity is name plus
    publisher, so the resulting package installs alongside the previous one instead of upgrading
    it, and widget pins and package-local state are lost.

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER CertificateSubject
    Subject of the signing certificate in CurrentUser\My.

.PARAMETER TimestampUrl
    RFC 3161 timestamp authority. Without a timestamp the signature becomes invalid when the
    certificate expires, so a retained package stops being installable while an already-installed
    package keeps running — which silently invalidates the remove-then-install rollback runbook.
    Pass an empty string to skip timestamping deliberately, and record that decision.

.PARAMETER SkipSigning
    Produce an unsigned package. Useful only for inspecting layout problems; an unsigned package
    cannot be installed under the sideload policy this project targets.

.EXAMPLE
    pwsh -File scripts/Build-Package.ps1

.EXAMPLE
    pwsh -File scripts/Build-Package.ps1 -TimestampUrl ''
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$CertificateSubject = 'CN=415 Group, Inc.',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$SkipSigning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageProject = Join-Path $repoRoot 'src\OutlookWidget.Package'
$manifestPath = Join-Path $packageProject 'Package.appxmanifest'
$assetsPath = Join-Path $packageProject 'Assets'

$outputRoot = Join-Path $repoRoot 'src\OutlookWidget.Package\AppPackages'
$layoutPath = Join-Path $outputRoot 'layout'

# ---------------------------------------------------------------------------
# Locate the SDK tools
# ---------------------------------------------------------------------------

function Find-SdkTool {
    param([Parameter(Mandatory)][string]$Name)

    $binRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'

    if (-not (Test-Path -LiteralPath $binRoot)) {
        throw "The Windows SDK is not installed: $binRoot does not exist. Install it with: winget install --id Microsoft.WindowsSDK.10.0.26100"
    }

    # Highest SDK version wins, x64 host tools.
    $tool = Get-ChildItem -LiteralPath $binRoot -Filter $Name -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -match '\\x64$' } |
        Sort-Object -Property FullName -Descending |
        Select-Object -First 1

    if (-not $tool) {
        throw "$Name was not found under $binRoot. The Windows SDK may be installed without the packaging tools."
    }

    return $tool.FullName
}

$makeAppx = Find-SdkTool -Name 'makeappx.exe'
Write-Output "makeappx: $makeAppx"

if (-not $SkipSigning) {
    $signTool = Find-SdkTool -Name 'signtool.exe'
    Write-Output "signtool: $signTool"
}

# ---------------------------------------------------------------------------
# Verify the publisher matches the certificate BEFORE building anything
# ---------------------------------------------------------------------------

[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$manifestPublisher = $manifest.Package.Identity.Publisher
$identityName = $manifest.Package.Identity.Name
$identityVersion = $manifest.Package.Identity.Version

Write-Output ''
Write-Output "Package identity: $identityName / $manifestPublisher / $identityVersion"

if (-not $SkipSigning) {
    # Match tolerantly across quoting differences, for the same reason
    # New-DevelopmentCertificate.ps1 does: 'CN=415 Group, Inc.' is stored as
    # CN="415 Group, Inc." because the comma requires quoting, and the unquoted form is not a
    # valid X.500 name so it cannot simply be parsed and normalized. Matching raw strings made the
    # documented no-argument example fail with "no code-signing certificate" unless the caller
    # happened to pass the quoted form by hand.
    $comparableRequested = (($CertificateSubject -replace '"', '') -replace '\s+', ' ').Trim().ToLowerInvariant()
    $managedFriendlyName = 'Outlook Inbox Widget development signing'
    $codeSigningEkuOid = '1.3.6.1.5.5.7.3.3'

    function Test-IsManagedCertificate {
        param(
            [Parameter(Mandatory)]
            [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
        )

        if ($Certificate.FriendlyName -ne $managedFriendlyName) {
            return $false
        }

        foreach ($extension in $Certificate.Extensions) {
            if ($extension -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
                foreach ($usage in $extension.EnhancedKeyUsages) {
                    if ($usage.Value -eq $codeSigningEkuOid) {
                        return $true
                    }
                }
            }
        }

        return $false
    }

    $certificate = @(Get-ChildItem -Path 'Cert:\CurrentUser\My' |
        Where-Object {
            $_.HasPrivateKey -and
            ((($_.Subject -replace '"', '') -replace '\s+', ' ').Trim().ToLowerInvariant()) -eq $comparableRequested -and
            (Test-IsManagedCertificate -Certificate $_)
        }) |
        Sort-Object -Property NotAfter -Descending | Select-Object -First 1

    if (-not $certificate) {
        throw "No managed Outlook Inbox Widget code-signing certificate with private key matching Subject '$CertificateSubject' in CurrentUser\My. Run scripts/New-DevelopmentCertificate.ps1 first."
    }

    # The manifest, by contrast, is compared EXACTLY against the certificate's stored subject.
    # Tolerance is right for finding the certificate the user meant; it would be wrong here,
    # because the package identity Windows computes uses the exact string. This is the check that
    # catches a mismatch before it becomes a package that installs alongside instead of upgrading.
    if ($manifestPublisher -ne $certificate.Subject) {
        throw @"
Publisher mismatch. This must be fixed before packaging.

  Manifest Publisher:  $manifestPublisher
  Certificate Subject: $($certificate.Subject)

These must be byte-for-byte identical. Package identity is name plus publisher, so a mismatch
produces a different package that installs alongside the existing one rather than upgrading it,
losing widget pins and package-local cache and settings.
"@
    }

    if ($certificate.NotAfter -lt (Get-Date)) {
        throw "The signing certificate expired on $($certificate.NotAfter.ToString('yyyy-MM-dd'))."
    }

    Write-Output "Certificate: $($certificate.Thumbprint), expires $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
}

# ---------------------------------------------------------------------------
# Publish the application into the layout
# ---------------------------------------------------------------------------

Write-Output ''
Write-Output 'Publishing OutlookWidget.App...'

if (Test-Path -LiteralPath $layoutPath) {
    Remove-Item -LiteralPath $layoutPath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $layoutPath | Out-Null

$appLayout = Join-Path $layoutPath 'OutlookWidget.App'

# Executable paths in the manifest are relative to the package root, so each application gets its
# own subdirectory in the layout and the manifest refers to it as OutlookWidget.App\...exe.
#
# Publish to a comma-free staging directory, then copy into the layout.
#
# This is not superstition. The dotnet CLI turns --output into an MSBuild PublishDir property,
# and MSBuild splits property values on commas — so any output path containing one is parsed as
# several bogus switches and fails with MSB1006. This repository lives under
# "OneDrive - 415 Group, Inc", which contains a comma, so publishing directly into the layout
# cannot work here.
#
# Staging outside the repository also keeps publish intermediates out of OneDrive's sync scope,
# which section 12 asks for. MSBuild comma escaping (%2C) would be the alternative, but it is
# fragile across CLI versions and hides the constraint instead of naming it.
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("OutlookWidget-publish-" + [Guid]::NewGuid().ToString('N'))

if ($staging -like '*,*') {
    throw "The staging path unexpectedly contains a comma, which MSBuild cannot accept: $staging"
}

try {
    $publishOutput = dotnet publish (Join-Path $repoRoot 'src\OutlookWidget.App\OutlookWidget.App.csproj') `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained false `
        --output $staging `
        --nologo 2>&1

    if ($LASTEXITCODE -ne 0) {
        # Surface the real reason. Swallowing publish output and reporting only "publish failed"
        # turns a one-line MSBuild message into a debugging session.
        $publishOutput | ForEach-Object { "  $_" }
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    New-Item -ItemType Directory -Force -Path $appLayout | Out-Null
    Copy-Item -Path (Join-Path $staging '*') -Destination $appLayout -Recurse -Force
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$publishedExe = Join-Path $appLayout 'OutlookWidget.App.exe'
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Published output is missing the executable the manifest references: $publishedExe"
}

# ---------------------------------------------------------------------------
# Assemble the rest of the layout
# ---------------------------------------------------------------------------

Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $layoutPath 'AppxManifest.xml') -Force
Copy-Item -LiteralPath $assetsPath -Destination (Join-Path $layoutPath 'Assets') -Recurse -Force

# Every asset the manifest names must exist, or makeappx fails late with a less obvious message.
$referencedAssets = @(
    $manifest.Package.Properties.Logo
    $manifest.Package.Applications.Application.VisualElements.Square150x150Logo
    $manifest.Package.Applications.Application.VisualElements.Square44x44Logo
) | Where-Object { $_ }

foreach ($asset in $referencedAssets) {
    $assetPath = Join-Path $layoutPath $asset
    if (-not (Test-Path -LiteralPath $assetPath)) {
        throw "The manifest references '$asset' but it is not in the layout. Run scripts/New-PlaceholderAssets.ps1."
    }
}

Write-Output "Layout assembled at $layoutPath"

# ---------------------------------------------------------------------------
# Pack
# ---------------------------------------------------------------------------

$msixPath = Join-Path $outputRoot "$identityName`_$identityVersion`_x64.msix"

if (Test-Path -LiteralPath $msixPath) {
    Remove-Item -LiteralPath $msixPath -Force
}

Write-Output ''
Write-Output 'Packing...'

& $makeAppx pack /d $layoutPath /p $msixPath /o | ForEach-Object { "  $_" }

if ($LASTEXITCODE -ne 0) {
    throw "makeappx failed with exit code $LASTEXITCODE."
}

Write-Output "Packed: $msixPath"

# ---------------------------------------------------------------------------
# Sign
# ---------------------------------------------------------------------------

if ($SkipSigning) {
    Write-Output ''
    Write-Output 'Skipped signing. An unsigned package cannot be installed under this sideload policy.'
}
else {
    Write-Output ''
    Write-Output 'Signing...'

    $signArguments = @(
        'sign'
        '/fd', 'SHA256'
        '/sha1', $certificate.Thumbprint
        '/s', 'My'
    )

    if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
        Write-Output 'WARNING: signing without a timestamp.'
        Write-Output "Record that this package stops being installable after $($certificate.NotAfter.ToString('yyyy-MM-dd')),"
        Write-Output 'which gives every retained rollback artifact a shelf life. An already-installed'
        Write-Output 'package keeps running past that date, so this only surfaces when rollback is needed.'
    }
    else {
        $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
    }

    $signArguments += $msixPath

    & $signTool @signArguments | ForEach-Object { "  $_" }

    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code $LASTEXITCODE. If timestamping failed, the timestamp authority may be unreachable; re-run with -TimestampUrl '' to sign without one and record that decision."
    }

    Write-Output ''
    Write-Output 'Verifying the signature...'
    & $signTool verify /pa /v $msixPath | Select-Object -Last 12 | ForEach-Object { "  $_" }
}

Write-Output ''
Write-Output "Package: $msixPath"
Write-Output 'Next: run scripts/Install-DevelopmentPackage.ps1 ELEVATED to trust the certificate and install.'

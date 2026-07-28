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

if (Test-Path -LiteralPath $layoutPath) {
    Remove-Item -LiteralPath $layoutPath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $layoutPath | Out-Null

function Publish-IntoLayout {
    <#
    .SYNOPSIS
        Publishes one project into its own subdirectory of the package layout.

    .DESCRIPTION
        Executable paths in the manifest are relative to the package root, and the two
        executables must not share a directory: they have different target frameworks and the
        provider carries Windows App SDK projection assemblies the companion does not. Merging
        them would let one project's assembly versions silently win over the other's.

        Publishes to a comma-free staging directory, then copies into the layout. This is not
        superstition. The dotnet CLI turns --output into an MSBuild PublishDir property, and
        MSBuild splits property values on commas — so any output path containing one is parsed as
        several bogus switches and fails with MSB1006. This repository lives under
        "OneDrive - 415 Group, Inc", which contains a comma, so publishing directly into the
        layout cannot work here.

        Staging outside the repository also keeps publish intermediates out of OneDrive's sync
        scope, which section 12 asks for. MSBuild comma escaping (%2C) would be the alternative,
        but it is fragile across CLI versions and hides the constraint instead of naming it.
    #>
    param(
        [Parameter(Mandatory)][string]$ProjectName,
        [Parameter(Mandatory)][string]$ExecutableName
    )

    Write-Output "Publishing $ProjectName..."

    $targetDirectory = Join-Path $layoutPath $ProjectName
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("OutlookWidget-publish-" + [Guid]::NewGuid().ToString('N'))

    if ($staging -like '*,*') {
        throw "The staging path unexpectedly contains a comma, which MSBuild cannot accept: $staging"
    }

    try {
        $projectPath = Join-Path $repoRoot "src\$ProjectName\$ProjectName.csproj"

        $publishOutput = dotnet publish $projectPath `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained false `
            --output $staging `
            --nologo 2>&1

        if ($LASTEXITCODE -ne 0) {
            # Surface the real reason. Swallowing publish output and reporting only "publish
            # failed" turns a one-line MSBuild message into a debugging session.
            $publishOutput | ForEach-Object { "  $_" }
            throw "dotnet publish failed for $ProjectName with exit code $LASTEXITCODE."
        }

        New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
        Copy-Item -Path (Join-Path $staging '*') -Destination $targetDirectory -Recurse -Force
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $publishedExe = Join-Path $targetDirectory $ExecutableName
    if (-not (Test-Path -LiteralPath $publishedExe)) {
        throw "Published output is missing the executable the manifest references: $publishedExe"
    }
}

Publish-IntoLayout -ProjectName 'OutlookWidget.App' -ExecutableName 'OutlookWidget.App.exe'
Publish-IntoLayout -ProjectName 'OutlookWidget.Provider' -ExecutableName 'OutlookWidget.Provider.exe'

# ---------------------------------------------------------------------------
# Assemble the rest of the layout
# ---------------------------------------------------------------------------

Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $layoutPath 'AppxManifest.xml') -Force
Copy-Item -LiteralPath $assetsPath -Destination (Join-Path $layoutPath 'Assets') -Recurse -Force

# The widget AppExtension declares PublicFolder="Public", which names a folder the widget host may
# read from the package. MSIX cannot contain an empty directory, so the folder needs at least one
# file to exist at all. Nothing is published through it today, and the note says so rather than
# leaving a mystery file in an installed package.
$publicFolder = Join-Path $layoutPath 'Public'
New-Item -ItemType Directory -Force -Path $publicFolder | Out-Null
Set-Content -LiteralPath (Join-Path $publicFolder 'README.txt') -Encoding utf8 -Value @'
Declared by the widget AppExtension as PublicFolder. Present because an MSIX cannot contain an
empty directory. Nothing is published through it; the widget provider passes its content to the
Widgets host through UpdateWidget rather than through files.
'@

# Every path the manifest names must exist in the layout, or makeappx fails late with a message
# that does not say which reference was wrong.
#
# Collected by walking the manifest rather than by listing paths here a second time. A hardcoded
# list is the thing that goes stale: the widget icon and screenshot were added to the manifest and
# a duplicated list would still have validated only the three application logos, so a missing
# screenshot would have surfaced as a widget absent from the picker.
$namespaces = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaces.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$namespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$namespaces.AddNamespace('uap3', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
$namespaces.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')

$referencedPaths = [System.Collections.Generic.List[string]]::new()

# Attribute references. Unprefixed attributes are in no namespace, so these need no prefix in the
# expression even though every element in this manifest is namespaced.
foreach ($attribute in @('Square150x150Logo', 'Square44x44Logo', 'Path', 'Executable')) {
    foreach ($node in $manifest.SelectNodes("//@$attribute")) {
        $referencedPaths.Add($node.Value)
    }
}

# Properties/Logo is an ELEMENT whose text is the path, not an attribute. Collecting it with the
# attributes above silently found nothing, so the store logo was the one manifest reference this
# check did not cover.
foreach ($node in $manifest.SelectNodes('//m:Properties/m:Logo', $namespaces)) {
    $referencedPaths.Add($node.InnerText)
}

# The manifest itself also names the two executables, which the publish step has already verified,
# so those are re-checked here only because a wrong Executable attribute and a failed publish
# produce the same symptom and this distinguishes them.
foreach ($reference in ($referencedPaths | Sort-Object -Unique)) {
    $resolved = Join-Path $layoutPath $reference
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw @"
The manifest references '$reference' but it is not in the layout: $resolved

If it is an image, run scripts/New-PlaceholderAssets.ps1. If it is an executable, check the
Executable attribute against the subdirectory names Publish-IntoLayout creates.
"@
    }
}

Write-Output "Layout assembled at $layoutPath"
Write-Output "Verified $($referencedPaths.Count) manifest path reference(s)."

# The COM class the provider registers at runtime, the class the manifest declares, and the class
# the widget extension activates must all be the same GUID. A mismatch installs cleanly and then
# fails activation with nothing surfaced in the Widgets Board, so it is checked before packing as
# well as in the test suite — the test suite does not run as part of packaging.
$comClassNode = $manifest.SelectSingleNode('//com:Class/@Id', $namespaces)
# The m: prefix is required even though CreateInstance carries no prefix in the manifest. The
# document element declares the foundation namespace as the DEFAULT namespace, so every unprefixed
# descendant element inherits it — including the widget registration elements inside
# uap3:Properties. In XPath an unprefixed name means "no namespace", so //CreateInstance matches
# nothing and this check silently reported a missing ClassId on a manifest that had one.
$activationNode = $manifest.SelectSingleNode('//m:CreateInstance/@ClassId', $namespaces)

if (-not $comClassNode) {
    throw 'The manifest declares no com:Class. The provider cannot be COM-activated without one.'
}

if (-not $activationNode) {
    throw 'The widget extension declares no CreateInstance ClassId. The widget will not activate.'
}

$comClassId = $comClassNode.Value
$activationClassId = $activationNode.Value

if ($comClassId -ne $activationClassId) {
    throw @"
Widget activation CLSID mismatch. This installs cleanly and then fails to activate.

  com:Class Id:            $comClassId
  CreateInstance ClassId:  $activationClassId
"@
}

Write-Output "Provider CLSID: $comClassId"

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

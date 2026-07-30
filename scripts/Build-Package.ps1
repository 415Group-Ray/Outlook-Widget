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
# Host runtime
# ---------------------------------------------------------------------------

<#
.SYNOPSIS
    The .NET major version OutlookWidget.Core targets, read from its project file.

.DESCRIPTION
    Derived rather than hardcoded. This value gates whether the packaging host can load the
    product's own assembly to validate authentication configuration, so a hardcoded 10 would go
    stale the moment the project moved to a newer framework - and it would go stale silently, in
    the direction of accepting a host that cannot actually load the assembly.
#>
function Get-CoreRuntimeMajor {
    $coreProject = Join-Path $repoRoot 'src\OutlookWidget.Core\OutlookWidget.Core.csproj'

    if (-not (Test-Path -LiteralPath $coreProject)) {
        throw "Cannot determine the required host runtime: $coreProject does not exist."
    }

    [xml]$coreXml = Get-Content -LiteralPath $coreProject -Raw
    $tfm = $coreXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1

    if ($tfm -notmatch '^net(?<major>\d+)\.') {
        throw "Cannot parse a .NET major version from OutlookWidget.Core's TargetFramework '$tfm'."
    }

    return [int]$Matches['major']
}

# Checked BEFORE publishing anything, because the alternative is a full publish followed by a
# failure that a two-second check could have reported.
#
# The configuration validation later in this script loads the built Core assembly with Add-Type, so
# the host's runtime must be at least as new as the framework Core targets. PowerShell's own
# `#Requires -Version 7.0` cannot express this: PowerShell 7.0 through 7.4 run on .NET 3.1 to 8, and
# only 7.5 and later are on .NET 9 or newer. So a perfectly supported PowerShell 7 host with an
# installed .NET 10 SDK can still be unable to load a net10.0 assembly - the SDK builds it, the host
# runs it.
$requiredRuntimeMajor = Get-CoreRuntimeMajor
$hostRuntimeMajor = [System.Environment]::Version.Major

if ($hostRuntimeMajor -lt $requiredRuntimeMajor) {
    throw @"
This PowerShell host cannot package the app.

  PowerShell:            $($PSVersionTable.PSVersion)
  Host .NET runtime:     $([System.Environment]::Version)
  OutlookWidget.Core:    targets .NET $requiredRuntimeMajor

Packaging validates the authentication configuration by loading the product's own loader, which
needs a host runtime at least as new as the assembly. An installed .NET $requiredRuntimeMajor SDK is
not sufficient: the SDK builds the assembly, the host has to run it.

Use PowerShell 7.6 or later, which runs on .NET 10. Check with:
  `$PSVersionTable.PSVersion; [System.Environment]::Version
"@
}

Write-Output "Host: PowerShell $($PSVersionTable.PSVersion) on .NET $([System.Environment]::Version) (Core targets .NET $requiredRuntimeMajor)"

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

$makePri = Find-SdkTool -Name 'makepri.exe'
Write-Output "makepri:  $makePri"

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
$baseVersion = $manifest.Package.Identity.Version

<#
.SYNOPSIS
    Derives the package version, stamping Build and Revision automatically.

.DESCRIPTION
    Major and minor come from the manifest and stay a deliberate decision. Build and Revision are
    derived, because a manual edit is the wrong mechanism for a value that must change on every build:
    forgetting it always fails at install time with HRESULT 0x80073CFB rather than at build time, and
    the failure names the package rather than the omission.

    Build is the git commit height. Revision counts builds within one commit, from state kept beside the
    package output.

    Both parts are needed and neither is sufficient. Commit height alone does not change when rebuilding
    a dirty tree, which is the normal development loop. A counter alone would not be meaningful across
    clones. Together they are monotonic in the order builds actually happen.

    This exists because it was measured that ANY commit changes EVERY assembly in the package: the .NET
    SDK embeds the git commit SHA in each assembly's informational version, so a documentation-only
    commit produces a different payload under the same version. That made a manual bump a per-commit
    obligation, which is exactly the kind of obligation to remove rather than to remember.
#>
function Resolve-PackageVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $BaseVersion,
        [Parameter(Mandatory)] [string] $StateDirectory,
        [Parameter(Mandatory)] [string] $IdentityName,

        # Every git query is scoped to this with -C, never to the ambient working directory. Invoked
        # from inside a different repository, the unscoped commands answered about *that* repository:
        # its shallow status and its commit height, stamped into this package with no error raised.
        # Passed rather than read from the enclosing scope for the same reason $IdentityName is —
        # dynamic scoping makes the dependency invisible at the call site.
        [Parameter(Mandatory)] [string] $RepositoryRoot
    )

    $parts = $BaseVersion.Split('.')

    if ($parts.Count -ne 4) {
        throw "The manifest version '$BaseVersion' is not four-part. MSIX requires Major.Minor.Build.Revision."
    }

    $major = [int]$parts[0]
    $minor = [int]$parts[1]

    # Commit height, with the preconditions checked FIRST rather than inferred from the count.
    #
    # An earlier version of this checked only the exit code and claimed in a comment that a shallow
    # clone would therefore be caught. It would not: `git rev-list --count HEAD` SUCCEEDS in a shallow
    # clone and returns the fetched depth. A `--depth 1` clone reports 1, so the derived version would
    # be 0.3.1.0 — lower than anything already installed, and MSIX refuses a downgrade.
    #
    # Neither safety net below catches it either. The backward-version guard reads state that a fresh
    # clone does not have, and the failure surfaces at install time as a version error naming the
    # package rather than the clone.

    $insideWorkTree = & git -C $RepositoryRoot rev-parse --is-inside-work-tree 2>$null

    if ($LASTEXITCODE -ne 0 -or $insideWorkTree -ne 'true') {
        throw 'Not inside a git work tree, so the package version cannot be derived. Build from a clone of the repository.'
    }

    # Shallow is rejected rather than worked around. Deepening the clone here would silently change the
    # caller's repository, and guessing a floor would produce a version with no relationship to history.
    $isShallow = & git -C $RepositoryRoot rev-parse --is-shallow-repository 2>$null

    if ($LASTEXITCODE -ne 0) {
        throw 'Cannot determine whether this repository is shallow, so the package version cannot be derived safely.'
    }

    if ($isShallow -eq 'true') {
        throw @'
This is a shallow clone, so commit height is the fetched depth rather than the real history and the
derived package version would be too low to install over an existing one.

Fix it with:  git fetch --unshallow
'@
    }

    $height = & git -C $RepositoryRoot rev-list --count HEAD 2>$null

    if ($LASTEXITCODE -ne 0 -or -not ($height -match '^\d+$')) {
        throw 'Cannot determine the git commit height, so the package version cannot be derived.'
    }

    $build = [int]$height

    $statePath = Join-Path $StateDirectory '.package-version.json'
    $revision = 0
    $previousVersion = $null

    if (Test-Path -LiteralPath $statePath) {
        # Read AND interpret the state in one guarded block that never throws. The deliberate monotonicity
        # failure is raised after it, further down, and that separation is the point.
        #
        # It used to be raised from inside a try like this one, with a `catch [RuntimeException] { throw }`
        # to let it through. But PowerShell's `throw "string"` produces a RuntimeException, and so does a
        # missing property under Set-StrictMode, and so does a failed [version] cast — so state that was
        # valid JSON without a usable `version` (an interrupted write, or an older format) was
        # indistinguishable from the deliberate failure, got rethrown, and broke every package build until
        # the file was deleted by hand. Exactly the opposite of the recovery this catch promises.
        try {
            $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json

            # Property bags rather than dotted access, because Set-StrictMode makes a missing property a
            # terminating error rather than $null.
            $storedBuild = $state.PSObject.Properties['build']
            $storedRevision = $state.PSObject.Properties['revision']
            $storedVersion = $state.PSObject.Properties['version']

            if ($storedBuild -and $storedRevision -and [int]$storedBuild.Value -eq $build) {
                $revision = [int]$storedRevision.Value + 1
            }

            if ($storedVersion -and $storedVersion.Value) {
                $previousVersion = [version]$storedVersion.Value
            }
        }
        catch {
            # Malformed state costs at most a repeated revision, which the installed-package check below
            # still catches. It must never fail the build: this file is local, disposable, and not the
            # authority on anything.
            Write-Warning "Ignoring unreadable version state at $statePath."
            $revision = 0
            $previousVersion = $null
        }
    }

    # Also consult the INSTALLED package, because the state file is not the authority on what must be
    # exceeded — the installed version is.
    #
    # The file is deliberately outside source control and can be absent for reasons that have nothing to
    # do with history: a fresh clone at the same commit, or simply cleaning the package output. Revision
    # then restarts at 0 and collides with, or falls below, a package already installed from that same
    # commit — a rebuild of which is likely to differ anyway, through uncommitted source or a different
    # authentication.json. The shallow-clone check does not help here, because the clone is complete and
    # the height is correct.
    #
    # Asking the machine what is installed makes the counter clone-independent for the case that actually
    # matters. It only ever raises the revision, so building for a different machine is unaffected.
    # Absence and failure are different answers, and treating them alike defeated the guard below.
    #
    # `Get-AppxPackage -Name` is a filter: a name that matches nothing returns an EMPTY RESULT rather than
    # an error, verified rather than assumed. So a thrown error never means "not installed" — it means the
    # question could not be asked. Swallowing it left $installedVersion null, the complete-version guard
    # was skipped, and with a missing or reset counter the script could emit a version that cannot
    # upgrade. Absence is representable; ignorance is not, so ignorance stops the build.
    $installedVersion = $null

    try {
        $installed = Get-AppxPackage -Name $IdentityName -ErrorAction Stop |
            Sort-Object -Property { [version]$_.Version } -Descending |
            Select-Object -First 1

        # Empty is the normal not-installed case and leaves $installedVersion null legitimately.
        if ($installed) {
            $installedVersion = [version]$installed.Version

            if ($installedVersion.Build -eq $build -and $installedVersion.Revision -ge $revision) {
                $revision = $installedVersion.Revision + 1
            }
        }
    }
    catch [System.Management.Automation.CommandNotFoundException] {
        # Separated because the remedy is completely different from a failed query, and the generic
        # message would send someone hunting a package problem that does not exist.
        throw @'
Get-AppxPackage is not available, so the installed package version cannot be checked and the derived
version could be one that will not install.

This script is Windows-only by construction -- it also needs makeappx, signtool and makepri -- so this
usually means it is running on the wrong platform or a PowerShell edition without the Appx cmdlets.
'@
    }
    catch {
        throw "Cannot determine the installed package version, so the derived version cannot be checked against it: $($_.Exception.Message)"
    }

    if ($build -gt 65535 -or $revision -gt 65535) {
        throw "Derived version component out of range (build $build, revision $revision). MSIX allows 0-65535."
    }

    $resolved = "$major.$minor.$build.$revision"

    # Compare the WHOLE resolved version against what is installed, not just the revision.
    #
    # The revision adjustment above only fires when the installed Build equals this one. A branch whose
    # commit height is LOWER than the installed package — a fork point, or a fresh clone of a shorter
    # branch — skips it entirely and derives a version MSIX will refuse as a downgrade. No revision can
    # rescue that, because Build is compared before Revision: 0.3.20.999 is still below 0.3.30.0.
    #
    # So this stops rather than producing an unusable package. Failing here names the cause; failing at
    # install time names the package, and the tempting remedy there is to uninstall, which loses the pin.
    if ($installedVersion -and [version]$resolved -le $installedVersion) {
        throw @"
Derived version $resolved does not exceed the installed $installedVersion, so the package could not be
installed over it.

This normally means the current branch's commit height is below the branch the installed package was
built from. Either:

  1. Build from the branch with the greater height, or merge it in.
  2. Raise Minor in Package.appxmanifest -- a deliberate identity decision, not a workaround.

Removing the installed package would also let the build through and is deliberately NOT offered: it
destroys the widget pin and package-local state, and both options above avoid that.
"@
    }

    # Refuse to go backwards relative to this machine's own build history, for the same reason.
    #
    # Raised out here, on a value already parsed and validated above, rather than from inside a try that
    # also has to survive a malformed file. There is no exception-type filtering left to get wrong: if
    # $previousVersion is null the state was absent or unusable and this check simply does not apply.
    if ($previousVersion -and [version]$resolved -le $previousVersion) {
        throw "Derived version $resolved does not exceed the previous $previousVersion. Commit, or delete $statePath if the history was rewritten."
    }

    New-Item -ItemType Directory -Force -Path $StateDirectory | Out-Null

    # Written before packing rather than after. A failed build then burns a revision, which costs
    # nothing, whereas writing afterwards would let two builds share a number if the first failed late.
    [ordered]@{
        version  = $resolved
        build    = $build
        revision = $revision
    } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8

    return $resolved
}

# State lives beside the project rather than in AppPackages, which is build output and gets cleaned.
# Losing the counter is not catastrophic — the installed-package check above covers the case that
# matters — but there is no reason to store it in the one directory whose whole purpose is to be
# deletable.
$identityVersion = Resolve-PackageVersion `
    -BaseVersion $baseVersion `
    -StateDirectory $packageProject `
    -IdentityName $identityName `
    -RepositoryRoot $repoRoot

Write-Output ''
Write-Output "Package identity: $identityName / $manifestPublisher / $identityVersion"
Write-Output "  Version: $identityVersion (base $baseVersion; build.revision derived from git height and build count)"

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
# Authentication configuration
# ---------------------------------------------------------------------------

# The Entra registration identifiers ship beside BOTH executables rather than once at the package
# root. Each process then reads the file from its own directory, so neither has to walk a relative
# path out of it - the sibling-directory coupling that CompanionLauncher already needs as a
# fallback is not worth reproducing for configuration that is loaded on every start.
#
# Neither identifier is a secret; both appear in ordinary network requests. They are kept out of Git
# because a committed development value is the one most likely to be aimed at the wrong environment.
$authSource = Join-Path $packageProject 'config\authentication.local.json'

if (-not (Test-Path -LiteralPath $authSource)) {
    throw @"
Authentication configuration is missing: $authSource

Copy src\OutlookWidget.Package\config\authentication.template.json to authentication.local.json in
the same directory and set the real tenantId and clientId from the Entra app registration. The
.local.json name is git-ignored deliberately; see docs/app-registration.md.
"@
}

# The unedited template gets its own message, because it is the most likely mistake and "still the
# template" is more use to the operator than "invalid".
$authContent = Get-Content -LiteralPath $authSource -Raw

if ($authContent -match '00000000-0000-0000-0000-000000000000') {
    throw @"
Authentication configuration still contains placeholder zeros: $authSource

Replace tenantId and clientId with the real values from the Entra app registration. A package built
with these would install and then fail every sign-in attempt.
"@
}

foreach ($projectName in @('OutlookWidget.App', 'OutlookWidget.Provider')) {
    Copy-Item -LiteralPath $authSource `
        -Destination (Join-Path (Join-Path $layoutPath $projectName) 'authentication.json') -Force
}

# Validate the STAGED copies with the product's own loader.
#
# Checking for placeholder zeros was not enough, and the gap was not hypothetical: malformed JSON, a
# missing property, or a non-GUID value all passed that check, got copied into the package, and then
# failed at runtime as Malformed or Invalid - producing exactly the package the registration
# documentation promised could not be built.
#
# The loader is invoked rather than reimplemented so the two cannot drift. Replicating "valid JSON,
# both properties present, both non-empty GUIDs" in PowerShell would work today and silently diverge
# the first time the loader gains a rule. This is possible because PowerShell 7 here runs on .NET 10,
# matching the assembly's target framework, and it validates what actually ships rather than the
# source file.
$coreAssembly = Join-Path $layoutPath 'OutlookWidget.Provider\OutlookWidget.Core.dll'

if (-not (Test-Path -LiteralPath $coreAssembly)) {
    throw "Cannot validate authentication configuration: $coreAssembly is missing from the layout."
}

try {
    Add-Type -LiteralPath $coreAssembly
}
catch {
    # Fail rather than fall back to a weaker check. A validation step that quietly downgrades itself
    # is worse than none, because the surrounding output still claims the configuration was verified.
    throw @"
Cannot load $coreAssembly to validate authentication configuration.

  $($_.Exception.GetType().Name): $($_.Exception.Message)

This host is PowerShell $($PSVersionTable.PSVersion) on .NET $([System.Environment]::Version); the
assembly targets a newer framework. Packaging stops rather than shipping configuration it could not
check.
"@
}

foreach ($projectName in @('OutlookWidget.App', 'OutlookWidget.Provider')) {
    $stagedDirectory = Join-Path $layoutPath $projectName
    $result = [OutlookWidget.Core.Authentication.AuthenticationConfiguration]::Load($stagedDirectory, $null)

    if ($result.Status.ToString() -ne 'Loaded') {
        throw @"
Authentication configuration is not usable: $($result.Status) for $projectName

  Source: $authSource

This is the product's own loader reporting on the copy that would have shipped, so a package built
now would install and fail every sign-in. Expected two properties, tenantId and clientId, each a
non-empty GUID:

  { "tenantId": "<guid>", "clientId": "<guid>" }
"@
    }
}

Write-Output 'Authentication configuration staged beside both executables and validated by the product loader.'

# ---------------------------------------------------------------------------
# Assemble the rest of the layout
# ---------------------------------------------------------------------------

# The layout manifest carries the derived version; the tracked manifest is not modified. Keeping the
# committed file stable means the version is not a line that shows up in every diff, and the packaged
# value stays derived rather than remembered.
#
# A scoped text substitution rather than loading and re-saving the XML: the manifest has comments,
# several namespaces, and three other Version attributes (the target device family and the framework
# dependency both use MinVersion). Re-serialising would rewrite formatting for no benefit, and a
# regex that is not scoped to the Identity element would silently retarget the framework dependency.
$layoutManifestPath = Join-Path $layoutPath 'AppxManifest.xml'
$manifestText = Get-Content -LiteralPath $manifestPath -Raw

$stampedText = [regex]::Replace(
    $manifestText,
    '(?s)(<Identity\b[^>]*?Version=")([^"]*)(")',
    { param($m) $m.Groups[1].Value + $identityVersion + $m.Groups[3].Value },
    1)

Set-Content -LiteralPath $layoutManifestPath -Value $stampedText -Encoding utf8 -NoNewline

# Assert the stamp landed. A substitution that silently matched nothing would pack the base version,
# and the install would then fail with 0x80073CFB naming the package rather than the cause.
[xml]$stampedManifest = Get-Content -LiteralPath $layoutManifestPath -Raw

if ($stampedManifest.Package.Identity.Version -ne $identityVersion) {
    throw "Failed to stamp the layout manifest: expected $identityVersion, found $($stampedManifest.Package.Identity.Version)."
}

# And that nothing else moved: the framework dependency's MinVersion is checked by a test and must
# still match the pinned runtime.
if ($stampedManifest.Package.Dependencies.PackageDependency.MinVersion -ne '2.3.1.0') {
    throw 'Stamping the version altered the framework dependency MinVersion. The substitution is not correctly scoped.'
}
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

If it is an image, run scripts/New-Assets.ps1. If it is an executable, check the
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

# The third value: the GUID the provider actually registers with OLE at runtime.
#
# Comparing only the two manifest values was the original mistake. Those two agreeing proves the
# manifest is internally consistent and says nothing about whether the running provider registers
# that class - and the runtime GUID is the one that can drift independently, because it lives in a
# different file that packaging does not otherwise read. A drift there produces a package that
# installs cleanly and a widget that fails to activate, which is precisely the failure this section
# exists to prevent. The test suite checks all three, but the test suite does not run during
# packaging, so the check has to exist here too.
$providerProgram = Join-Path $repoRoot 'src\OutlookWidget.Provider\Program.cs'

if (-not (Test-Path -LiteralPath $providerProgram)) {
    throw "Cannot verify the runtime CLSID: $providerProgram does not exist."
}

$providerSource = Get-Content -LiteralPath $providerProgram -Raw
$runtimeMatch = [regex]::Match($providerSource, 'ProviderClassId\s*=\s*new\("(?<guid>[0-9A-Fa-f-]{36})"\)')

if (-not $runtimeMatch.Success) {
    # Refuse rather than skip. A silently unverifiable check is worse than no check, because the
    # surrounding output would still claim all three values were compared.
    throw @"
Could not read Program.ProviderClassId from $providerProgram.

The declaration form changed, so the runtime CLSID cannot be compared against the manifest. Update
this pattern together with the declaration rather than removing the check.
"@
}

$runtimeClassId = $runtimeMatch.Groups['guid'].Value

# Parsed rather than string-compared. Casing and brace style are irrelevant to COM, so a mismatch
# there would be noise; a genuinely different value must fail.
$ids = [ordered]@{
    'com:Class Id'            = $comClassId
    'CreateInstance ClassId'  = $activationClassId
    'Program.ProviderClassId' = $runtimeClassId
}

$distinct = @($ids.Values | ForEach-Object { [Guid]::Parse($_) } | Select-Object -Unique)

if ($distinct.Count -ne 1) {
    $detail = ($ids.Keys | ForEach-Object { "  {0,-24} {1}" -f $_, $ids[$_] }) -join "`n"

    throw @"
Provider CLSID mismatch. This installs cleanly and then fails to activate, with nothing surfaced in
the Widgets Board.

$detail

All three must be the same GUID.
"@
}

Write-Output "Provider CLSID: $comClassId (manifest, widget extension, and provider source agree)"

# ---------------------------------------------------------------------------
# Index the resources
# ---------------------------------------------------------------------------

# Without a resources.pri, Windows resolves the manifest's Assets\Square44x44Logo.png to that exact
# file and every scale- and targetsize-qualified sibling is ignored. The icon then renders from a
# single 44px bitmap on a high-DPI display and looks soft, and the unplated taskbar variant never
# applies at all. MakePri indexes the qualifiers so the resource loader can pick the right one.
#
# This is the step a Visual Studio packaging project would run for us. Packaging directly with the
# SDK tools means running it here.
Write-Output ''
Write-Output 'Indexing resources...'

$priConfig = Join-Path $outputRoot 'priconfig.xml'
$priOutput = Join-Path $layoutPath 'resources.pri'

if (Test-Path -LiteralPath $priConfig) {
    Remove-Item -LiteralPath $priConfig -Force
}

# /dq en-US matches the manifest's single declared Resource language. A default qualifier that
# disagreed with the manifest would produce a package whose resources cannot be resolved.
& $makePri createconfig /cf $priConfig /dq en-US /o | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw "makepri createconfig failed with exit code $LASTEXITCODE."
}

# /pr is the project root that gets indexed, /mn the manifest that names the resources. Run from
# the layout so the indexed paths are package-relative.
& $makePri new /pr $layoutPath /cf $priConfig /of $priOutput /mn (Join-Path $layoutPath 'AppxManifest.xml') /o |
    ForEach-Object { "  $_" }

if ($LASTEXITCODE -ne 0) {
    throw "makepri new failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $priOutput)) {
    throw "makepri reported success but produced no resources.pri at $priOutput."
}

Remove-Item -LiteralPath $priConfig -Force

$qualified = @(Get-ChildItem -LiteralPath (Join-Path $layoutPath 'Assets') -Filter '*.scale-*.png' -ErrorAction SilentlyContinue).Count
$targeted = @(Get-ChildItem -LiteralPath (Join-Path $layoutPath 'Assets') -Filter '*.targetsize-*.png' -ErrorAction SilentlyContinue).Count

Write-Output "Indexed resources.pri ($qualified scale variant(s), $targeted target size(s))."

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

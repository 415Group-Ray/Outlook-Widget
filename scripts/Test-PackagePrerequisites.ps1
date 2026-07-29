#Requires -Version 7.0
<#
.SYNOPSIS
    Phase 0 preflight for the Outlook Inbox Widget.

.DESCRIPTION
    Checks every device-level precondition the technical plan requires before a
    signed MSIX is built or installed, and reports each one as an explicit
    outcome rather than a single pass/fail.

    Outcomes are deliberately distinguishable, because the plan's section 17 gate
    grouping depends on knowing *which* precondition failed:

      Pass     - the precondition is satisfied.
      Fail     - a universal product precondition is not satisfied. Building or
                 installing will not fix it; the underlying device or tenant
                 problem must be resolved first.
      Blocked  - the check could not be performed from this session (for example
                 it needs elevation). Not evidence of either outcome.
      Warn     - satisfied, but with a caveat worth recording in the evidence
                 report.
      Info     - recorded for the evidence report; not a gate.

    The script is read-only. It changes no policy, installs nothing, and requires
    no elevation, though one check reports as Blocked without it.

.PARAMETER Json
    Emit the results as JSON instead of a table, for pasting into the Phase 0
    evidence report.

.EXAMPLE
    pwsh -File scripts/Test-PackagePrerequisites.ps1

.EXAMPLE
    pwsh -File scripts/Test-PackagePrerequisites.ps1 -Json > phase0-preflight.json
#>
[CmdletBinding()]
param(
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The plan's initial supported baseline: Windows 11 24H2, build 26100 or later.
$MinimumBuild = 26100

# The plan pins Windows App SDK 2.3.1 stable. The runtime package is versioned
# 2.3.1.0 and its package name carries the major.minor.
$RequiredAppRuntimeName = 'Microsoft.WindowsAppRuntime.2'
$RequiredAppRuntimeVersion = [Version]'2.3.1.0'

$results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('Pass', 'Fail', 'Blocked', 'Warn', 'Info')][string]$Outcome,
        [Parameter(Mandatory)][string]$Detail,
        [ValidateSet('Universal', 'Native', 'Tooling', 'Evidence')][string]$Category = 'Evidence'
    )
    $results.Add([pscustomobject]@{
        Name     = $Name
        Outcome  = $Outcome
        Category = $Category
        Detail   = $Detail
    })
}

function Get-RegistryValueOrNull {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )
    # Get-ItemPropertyValue rather than Get-ItemProperty plus a property probe: under
    # Set-StrictMode the probe throws when the key exists but carries no values, which is
    # exactly the AppModelUnlock case on a machine that has never enabled Developer Mode.
    # A missing key and a missing value are both "not set" here, and neither is an error.
    try {
        return Get-ItemPropertyValue -LiteralPath $Path -Name $Name -ErrorAction Stop
    }
    catch {
        return $null
    }
}

# ---------------------------------------------------------------------------
# Operating system and architecture
# ---------------------------------------------------------------------------

$osVersion = [System.Environment]::OSVersion.Version
$build = $osVersion.Build

if ($build -ge $MinimumBuild) {
    Add-Result -Name 'Windows build' -Outcome 'Pass' -Category 'Universal' `
        -Detail "Build $build meets the $MinimumBuild baseline (full version $osVersion)."
}
else {
    Add-Result -Name 'Windows build' -Outcome 'Fail' -Category 'Universal' `
        -Detail "Build $build is below the $MinimumBuild baseline. This is an unsupported OS, not a widget or packaging failure."
}

# PROCESSOR_ARCHITECTURE reflects the architecture of *this process*, which under
# an x86 shell would misreport an x64 machine. Read the machine architecture.
$machineArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($machineArchitecture -eq 'X64') {
    Add-Result -Name 'Architecture' -Outcome 'Pass' -Category 'Universal' `
        -Detail 'x64, which is the only architecture v1 produces.'
}
else {
    Add-Result -Name 'Architecture' -Outcome 'Fail' -Category 'Universal' `
        -Detail "OS architecture is $machineArchitecture. v1 produces x64 only; ARM64 is added only if the author actually runs an ARM64 device."
}

# ---------------------------------------------------------------------------
# Widgets policy
#
# Either the NewsAndInterests CSP or the corresponding Allow widgets Group Policy
# can disable the entire Widgets experience. A disabled Widgets Board means the
# native surface can never appear, so this must report and stop before any build
# work rather than surfacing later as a mysteriously missing widget.
# ---------------------------------------------------------------------------

$policyPaths = @(
    @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh'; Name = 'AllowNewsAndInterests'; Source = 'Allow widgets GPO / NewsAndInterests CSP (machine policy)' }
    @{ Path = 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\NewsAndInterests'; Name = 'AllowNewsAndInterests'; Source = 'NewsAndInterests CSP (PolicyManager)' }
)

$policyDisabled = $false
$policyDetails = [System.Collections.Generic.List[string]]::new()

foreach ($policy in $policyPaths) {
    $value = Get-RegistryValueOrNull -Path $policy.Path -Name $policy.Name
    if ($null -ne $value) {
        $policyDetails.Add("$($policy.Source) = $value")
        if ([int]$value -eq 0) {
            $policyDisabled = $true
        }
    }
}

if ($policyDisabled) {
    Add-Result -Name 'Widgets policy' -Outcome 'Fail' -Category 'Native' `
        -Detail "Widgets are disabled by policy ($($policyDetails -join '; ')). The native surface can never appear. Report widgets disabled by policy and do not install a native-only package."
}
elseif ($policyDetails.Count -gt 0) {
    Add-Result -Name 'Widgets policy' -Outcome 'Pass' -Category 'Native' `
        -Detail "Widgets permitted by explicit policy ($($policyDetails -join '; '))."
}
else {
    Add-Result -Name 'Widgets policy' -Outcome 'Pass' -Category 'Native' `
        -Detail 'No AllowNewsAndInterests policy value present, so Widgets are not policy-disabled on this device.'
}

# ---------------------------------------------------------------------------
# Widgets host and Windows App Runtime
# ---------------------------------------------------------------------------

$widgetsRuntime = Get-AppxPackage | Where-Object { $_.Name -eq 'Microsoft.WidgetsPlatformRuntime' } |
    Sort-Object -Property Version -Descending | Select-Object -First 1
$webExperience = Get-AppxPackage | Where-Object { $_.Name -eq 'MicrosoftWindows.Client.WebExperience' } |
    Sort-Object -Property Version -Descending | Select-Object -First 1

if ($widgetsRuntime) {
    Add-Result -Name 'Widgets platform runtime' -Outcome 'Pass' -Category 'Native' `
        -Detail "Microsoft.WidgetsPlatformRuntime $($widgetsRuntime.Version)."
}
else {
    Add-Result -Name 'Widgets platform runtime' -Outcome 'Fail' -Category 'Native' `
        -Detail 'Microsoft.WidgetsPlatformRuntime is not installed. Third-party widgets cannot be hosted.'
}

if ($webExperience) {
    Add-Result -Name 'Widgets Board host' -Outcome 'Pass' -Category 'Native' `
        -Detail "MicrosoftWindows.Client.WebExperience $($webExperience.Version)."
}
else {
    Add-Result -Name 'Widgets Board host' -Outcome 'Fail' -Category 'Native' `
        -Detail 'MicrosoftWindows.Client.WebExperience is not installed. There is no Widgets Board to pin into.'
}

$appRuntimes = Get-AppxPackage | Where-Object { $_.Name -eq $RequiredAppRuntimeName }
$matchingRuntime = $appRuntimes | Where-Object { [Version]$_.Version -ge $RequiredAppRuntimeVersion } |
    Sort-Object -Property Version -Descending | Select-Object -First 1

if ($matchingRuntime) {
    Add-Result -Name 'Windows App Runtime' -Outcome 'Pass' -Category 'Tooling' `
        -Detail "$RequiredAppRuntimeName $($matchingRuntime.Version) satisfies the pinned $RequiredAppRuntimeVersion."
}
elseif ($appRuntimes) {
    $found = ($appRuntimes | ForEach-Object { $_.Version }) -join ', '
    Add-Result -Name 'Windows App Runtime' -Outcome 'Warn' -Category 'Tooling' `
        -Detail "$RequiredAppRuntimeName present at $found but none satisfies the pinned $RequiredAppRuntimeVersion. A framework-dependent package will not launch until the matching runtime is installed."
}
else {
    Add-Result -Name 'Windows App Runtime' -Outcome 'Warn' -Category 'Tooling' `
        -Detail "$RequiredAppRuntimeName is not installed. Required only for a framework-dependent package; a self-contained package does not need it."
}

# ---------------------------------------------------------------------------
# New Outlook
#
# The plan supports New Outlook only. There is no Classic Outlook path, so a
# missing New Outlook is recorded as such rather than falling back.
# ---------------------------------------------------------------------------

$newOutlook = Get-AppxPackage | Where-Object { $_.Name -eq 'Microsoft.OutlookForWindows' } |
    Sort-Object -Property Version -Descending | Select-Object -First 1

if ($newOutlook) {
    Add-Result -Name 'New Outlook package' -Outcome 'Pass' -Category 'Native' `
        -Detail "Microsoft.OutlookForWindows $($newOutlook.Version), family $($newOutlook.PackageFamilyName)."
}
else {
    Add-Result -Name 'New Outlook package' -Outcome 'Fail' -Category 'Native' `
        -Detail 'Microsoft.OutlookForWindows is not installed. Launch gates cannot run on this device; install New Outlook or designate another test PC.'
}

# Resolution of the bare olk.exe command is a compatibility test, not a
# contract. Record what resolves, and never hard-code a versioned WindowsApps
# path in the product.
$olk = Get-Command -Name 'olk.exe' -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1

if ($olk) {
    Add-Result -Name 'olk.exe alias' -Outcome 'Pass' -Category 'Native' `
        -Detail "Bare olk.exe resolves to $($olk.Source) via the app execution alias. Resolution only; launch behaviour is a separate gate."
}
else {
    Add-Result -Name 'olk.exe alias' -Outcome 'Warn' -Category 'Native' `
        -Detail 'Bare olk.exe does not resolve on PATH. Per section 17 gate 7, this degrades the Open Outlook action to an approved web-only mode rather than stopping the product.'
}

# ---------------------------------------------------------------------------
# Sideloading preconditions
# ---------------------------------------------------------------------------

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isAdmin) {
    Add-Result -Name 'Elevation' -Outcome 'Pass' -Category 'Tooling' `
        -Detail 'Session is elevated, so the public certificate can be placed in LocalMachine\TrustedPeople.'
}
else {
    Add-Result -Name 'Elevation' -Outcome 'Blocked' -Category 'Tooling' `
        -Detail 'Session is not elevated. Trusting the public certificate in LocalMachine\TrustedPeople needs administrator rights, so gate 1 cannot be exercised from here.'
}

$devMode = Get-RegistryValueOrNull `
    -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
    -Name 'AllowDevelopmentWithoutDevLicense'

if ($devMode -eq 1) {
    Add-Result -Name 'Developer Mode' -Outcome 'Pass' -Category 'Tooling' `
        -Detail 'AllowDevelopmentWithoutDevLicense = 1.'
}
else {
    $observed = if ($null -eq $devMode) { 'value absent' } else { "value $devMode" }
    Add-Result -Name 'Developer Mode' -Outcome 'Warn' -Category 'Tooling' `
        -Detail "Developer Mode appears off ($observed). A properly signed, trusted MSIX installs without it; it is needed for loose-file deployment and F5 debugging."
}

# Whether a managed-device policy blocks trusting the certificate is a real
# Phase 0 question and cannot be answered by reading a single value, so report
# what is observable and leave the conclusion to the install attempt.
$trustedPeopleCount = try {
    (Get-ChildItem -Path 'Cert:\LocalMachine\TrustedPeople' -ErrorAction Stop | Measure-Object).Count
}
catch {
    $null
}

if ($null -ne $trustedPeopleCount) {
    Add-Result -Name 'TrustedPeople store' -Outcome 'Info' -Category 'Universal' `
        -Detail "LocalMachine\TrustedPeople is readable and contains $trustedPeopleCount certificate(s). Whether policy permits *adding* one is proven only by attempting it elevated."
}
else {
    Add-Result -Name 'TrustedPeople store' -Outcome 'Blocked' -Category 'Universal' `
        -Detail 'LocalMachine\TrustedPeople could not be read from this session.'
}

# ---------------------------------------------------------------------------
# Build toolchain
# ---------------------------------------------------------------------------

$dotnet = Get-Command -Name 'dotnet' -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1

if ($dotnet) {
    $sdks = & $dotnet.Source --list-sdks 2>&1
    $sdkLines = @($sdks | Where-Object { $_ -match '^\d+\.\d+\.\d+' })
    $hasNet10 = @($sdkLines | Where-Object { $_ -match '^10\.' }).Count -gt 0

    if ($hasNet10) {
        Add-Result -Name '.NET SDK' -Outcome 'Pass' -Category 'Tooling' `
            -Detail "A .NET 10 SDK is installed ($($sdkLines -join ' | '))."
    }
    elseif ($sdkLines.Count -gt 0) {
        Add-Result -Name '.NET SDK' -Outcome 'Fail' -Category 'Tooling' `
            -Detail "No .NET 10 SDK. Installed: $($sdkLines -join ' | '). The solution targets .NET 10 LTS."
    }
    else {
        Add-Result -Name '.NET SDK' -Outcome 'Fail' -Category 'Tooling' `
            -Detail 'The dotnet command exists but reports no SDKs, so only runtimes are installed.'
    }
}
else {
    Add-Result -Name '.NET SDK' -Outcome 'Fail' -Category 'Tooling' `
        -Detail 'The dotnet command was not found.'
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $vswhere) {
    $vsVersions = & $vswhere -all -prerelease -format value -property installationVersion 2>&1
    Add-Result -Name 'Visual Studio' -Outcome 'Pass' -Category 'Tooling' `
        -Detail "Visual Studio present: $(($vsVersions | Where-Object { $_ }) -join ', ')."
}
else {
    Add-Result -Name 'Visual Studio' -Outcome 'Warn' -Category 'Tooling' `
        -Detail 'vswhere.exe not found, so no Visual Studio installation was detected. MSIX packaging and the WinUI designer path need the VS workload; the Core library and its tests do not.'
}

# The PowerShell host's own runtime, which is a packaging prerequisite and not an obvious one.
#
# Build-Package.ps1 validates the authentication configuration by loading the built Core assembly
# with Add-Type, so the host runtime must be at least as new as the framework Core targets.
# `#Requires -Version 7.0` cannot express this: PowerShell 7.0 through 7.4 run on .NET 3.1 to 8, and
# only 7.5 and later are on .NET 9 or newer. So a supported PowerShell 7 host with a .NET 10 SDK
# installed can still fail to load a net10.0 assembly - the SDK builds it, the host runs it. Without
# this row the preflight would pass such a machine and every package build would stop.
#
# The required version is read from the project file rather than hardcoded, so it cannot go stale in
# the direction of accepting a host that is too old.
$coreProjectPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\OutlookWidget.Core\OutlookWidget.Core.csproj'
$requiredRuntimeMajor = $null

if (Test-Path -LiteralPath $coreProjectPath) {
    [xml]$coreProjectXml = Get-Content -LiteralPath $coreProjectPath -Raw
    $coreTfm = $coreProjectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1

    if ($coreTfm -match '^net(?<major>\d+)\.') {
        $requiredRuntimeMajor = [int]$Matches['major']
    }
}

$hostRuntime = [System.Environment]::Version

if (-not $requiredRuntimeMajor) {
    Add-Result -Name 'PowerShell host runtime' -Outcome 'Warn' -Category 'Tooling' `
        -Detail "Could not read OutlookWidget.Core's TargetFramework from $coreProjectPath, so the host runtime requirement could not be checked. Packaging verifies it again and will stop if the host is too old."
}
elseif ($hostRuntime.Major -ge $requiredRuntimeMajor) {
    Add-Result -Name 'PowerShell host runtime' -Outcome 'Pass' -Category 'Tooling' `
        -Detail "PowerShell $($PSVersionTable.PSVersion) on .NET $hostRuntime, which can load the net$requiredRuntimeMajor.0 Core assembly that packaging uses to validate authentication configuration."
}
else {
    Add-Result -Name 'PowerShell host runtime' -Outcome 'Fail' -Category 'Tooling' `
        -Detail "PowerShell $($PSVersionTable.PSVersion) runs on .NET $hostRuntime, but OutlookWidget.Core targets .NET $requiredRuntimeMajor. Packaging loads that assembly to validate authentication configuration and will stop here. An installed .NET $requiredRuntimeMajor SDK is not sufficient - the SDK builds the assembly, the host has to run it. Use PowerShell 7.6 or later, which runs on .NET 10."
}

# MakeAppx, SignTool and MakePri ship with the Windows SDK. Without them there is no way
# to produce or sign an MSIX, which is a hard stop for every packaging gate.
#
# MakePri belongs in this list even though it is easy to think of as optional. Build-Package.ps1 runs
# it to index the scale- and targetsize-qualified icon assets, and throws if it is absent - so a
# preflight that checked only MakeAppx and SignTool would report a machine ready to package when the
# build would in fact stop. Verifying the tools the build actually invokes is the whole point of the
# probe, and the set is derived here rather than assumed.
$sdkBinRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'

function Find-SdkToolOrNull {
    param([Parameter(Mandatory)][string]$Name)

    if (-not (Test-Path -LiteralPath $sdkBinRoot)) {
        return $null
    }

    # Highest SDK version, x64 host - the same selection Build-Package.ps1 makes, so the probe cannot
    # report a tool the build would not choose.
    return Get-ChildItem -LiteralPath $sdkBinRoot -Filter $Name -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -match '\\x64$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
}

$requiredSdkTools = [ordered]@{
    'makeappx.exe' = Find-SdkToolOrNull -Name 'makeappx.exe'
    'signtool.exe' = Find-SdkToolOrNull -Name 'signtool.exe'
    'makepri.exe'  = Find-SdkToolOrNull -Name 'makepri.exe'
}

$missingTools = @($requiredSdkTools.Keys | Where-Object { -not $requiredSdkTools[$_] })

if ($missingTools.Count -eq 0) {
    $found = ($requiredSdkTools.Keys | ForEach-Object { "$_ at $($requiredSdkTools[$_].FullName)" }) -join '; '

    Add-Result -Name 'Windows SDK packaging tools' -Outcome 'Pass' -Category 'Tooling' `
        -Detail "$found."
}
else {
    Add-Result -Name 'Windows SDK packaging tools' -Outcome 'Fail' -Category 'Tooling' `
        -Detail "$($missingTools -join ', ') not found under Windows Kits\10\bin. A signed MSIX cannot be produced on this machine until the Windows SDK packaging tools are installed. MakePri is required because Build-Package.ps1 uses it to index the qualified icon assets; without it the icon renders from a single unscaled bitmap."
}

# ---------------------------------------------------------------------------
# Repository hygiene, per the revised section 12
# ---------------------------------------------------------------------------

$repoRoot = Split-Path -Parent $PSScriptRoot

$repoAttributes = (Get-Item -LiteralPath $repoRoot -Force).Attributes
$isPinned = ($repoAttributes -band [System.IO.FileAttributes]::NotContentIndexed) -ne 0
# The pinned/unpinned state is exposed through attrib's P and U flags rather than
# a managed enum, so read them the way OneDrive sets them.
$attribOutput = & attrib $repoRoot 2>&1 | Out-String
$hasPinFlag = $attribOutput -match '(?m)^\s*[A-Z\s]*P[A-Z\s]*\s+\S'
$hasUnpinFlag = $attribOutput -match '(?m)^\s*[A-Z\s]*U[A-Z\s]*\s+\S'

if ($hasPinFlag -and -not $hasUnpinFlag) {
    Add-Result -Name 'OneDrive always-keep-local' -Outcome 'Pass' -Category 'Tooling' `
        -Detail 'Repository root carries the pinned (P) attribute without the unpinned (U) attribute, so builds do not depend on Files On-Demand hydration.'
}
else {
    Add-Result -Name 'OneDrive always-keep-local' -Outcome 'Warn' -Category 'Tooling' `
        -Detail "Repository root is not confirmed pinned (attrib reports: $($attribOutput.Trim())). Mark it Always keep on this device, or run: attrib +P -U `"$repoRoot`" /s /d"
}

$gitignorePath = Join-Path $repoRoot '.gitignore'
if (Test-Path -LiteralPath $gitignorePath) {
    # Ask Git whether representative paths are actually ignored, rather than string-matching
    # the .gitignore text. Case-insensitive character classes such as [Oo]bj/ are correct and
    # idiomatic but do not contain the literal "obj/", so a text search reports a problem that
    # does not exist. check-ignore tests the behaviour that matters.
    $probePaths = @(
        'src/OutlookWidget.Core/bin/Debug/x.dll'
        'src/OutlookWidget.Core/obj/project.assets.json'
        '.vs/slnx.sqlite'
        'src/OutlookWidget.Package/AppPackages/x.msix'
        'tests/OutlookWidget.Core.Tests/TestResults/x.trx'
        'signing.pfx'
    )

    $notIgnored = [System.Collections.Generic.List[string]]::new()

    foreach ($probe in $probePaths) {
        git -C $repoRoot check-ignore --quiet -- $probe 2>$null
        if ($LASTEXITCODE -ne 0) {
            $notIgnored.Add($probe)
        }
    }

    if ($notIgnored.Count -eq 0) {
        Add-Result -Name 'Volatile outputs excluded' -Outcome 'Pass' -Category 'Tooling' `
            -Detail 'Git ignores build output, packaging output, test results, and key material.'
    }
    else {
        Add-Result -Name 'Volatile outputs excluded' -Outcome 'Warn' -Category 'Tooling' `
            -Detail "Git does not ignore: $($notIgnored -join ', ')."
    }
}
else {
    Add-Result -Name 'Volatile outputs excluded' -Outcome 'Fail' -Category 'Tooling' `
        -Detail 'No .gitignore. Build output would be committed and synced.'
}

# A private key inside the repository or anywhere in OneDrive is a credential
# exposure, and the plan forbids it outright rather than mitigating it.
$keyPatterns = @('*.pfx', '*.p12', '*.snk', '*.key')
$strayKeys = foreach ($pattern in $keyPatterns) {
    Get-ChildItem -LiteralPath $repoRoot -Filter $pattern -Recurse -Force -File -ErrorAction SilentlyContinue
}

if ($strayKeys) {
    $paths = ($strayKeys | ForEach-Object { $_.FullName }) -join '; '
    Add-Result -Name 'No private key in tree' -Outcome 'Fail' -Category 'Universal' `
        -Detail "Private key material found inside the OneDrive-backed repository: $paths. Remove it and keep the signing key in CurrentUser\My or a dedicated non-synced path."
}
else {
    Add-Result -Name 'No private key in tree' -Outcome 'Pass' -Category 'Universal' `
        -Detail 'No .pfx, .p12, .snk, or .key file exists anywhere under the repository root.'
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

if ($Json) {
    $results | ConvertTo-Json -Depth 4
}
else {
    $results | Format-Table -AutoSize -Property Outcome, Category, Name, Detail | Out-String -Width 400 | Write-Output

    $failed = @($results | Where-Object { $_.Outcome -eq 'Fail' })
    $blocked = @($results | Where-Object { $_.Outcome -eq 'Blocked' })
    $warned = @($results | Where-Object { $_.Outcome -eq 'Warn' })

    Write-Output ("Summary: {0} pass, {1} fail, {2} blocked, {3} warn, {4} info." -f
        @($results | Where-Object { $_.Outcome -eq 'Pass' }).Count,
        $failed.Count, $blocked.Count, $warned.Count,
        @($results | Where-Object { $_.Outcome -eq 'Info' }).Count)

    foreach ($item in $failed) {
        Write-Output "FAIL      [$($item.Category)] $($item.Name): $($item.Detail)"
    }
    foreach ($item in $blocked) {
        Write-Output "BLOCKED   [$($item.Category)] $($item.Name): $($item.Detail)"
    }
}

# Exit code carries the gate result so this can front a build: 0 when nothing
# failed, 1 when a precondition failed. Blocked checks do not fail the run,
# because an unproven check is not a proven failure.
if (@($results | Where-Object { $_.Outcome -eq 'Fail' }).Count -gt 0) {
    exit 1
}
exit 0

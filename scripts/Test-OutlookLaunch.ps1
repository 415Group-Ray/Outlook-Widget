#Requires -Version 7.0
<#
.SYNOPSIS
    Phase 0 gate 7 evidence: how New Outlook can be launched on this machine.

.DESCRIPTION
    The plan treats New Outlook launch through olk.exe as a tested compatibility path
    rather than a guaranteed contract, and forbids hard-coding a versioned
    C:\Program Files\WindowsApps\Microsoft.OutlookForWindows_<version> path. This script
    records which launch strategies are available, in the order the implementation should
    prefer them, so OutlookLauncher can select the least brittle method actually
    demonstrated on current client builds.

    Strategies compared, per section 9:
      1. App execution alias resolution  - bare olk.exe on PATH.
      2. Package activation              - the installed Microsoft.OutlookForWindows
                                           package's application user model ID.

    By default nothing is launched: the script reports what is resolvable. Pass -Launch to
    actually start New Outlook and observe the behaviour, which is the part of gate 7 that
    cannot be established by inspection.

.PARAMETER Launch
    Actually launch New Outlook using the preferred available strategy.

.PARAMETER Json
    Emit results as JSON for the Phase 0 evidence report.

.EXAMPLE
    pwsh -File scripts/Test-OutlookLaunch.ps1

.EXAMPLE
    pwsh -File scripts/Test-OutlookLaunch.ps1 -Launch
#>
[CmdletBinding()]
param(
    [switch]$Launch,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Strategy,
        [Parameter(Mandatory)][ValidateSet('Available', 'Unavailable', 'Prohibited')][string]$Status,
        [Parameter(Mandatory)][string]$Detail,
        [string]$Target = ''
    )
    $results.Add([pscustomobject]@{
        Strategy = $Strategy
        Status   = $Status
        Target   = $Target
        Detail   = $Detail
    })
}

# ---------------------------------------------------------------------------
# Is New Outlook installed at all?
# ---------------------------------------------------------------------------

$package = Get-AppxPackage | Where-Object { $_.Name -eq 'Microsoft.OutlookForWindows' } |
    Sort-Object -Property Version -Descending | Select-Object -First 1

if (-not $package) {
    Add-Result -Strategy 'New Outlook installed' -Status 'Unavailable' `
        -Detail 'Microsoft.OutlookForWindows is not installed. There is no Classic Outlook fallback: New Outlook is the only supported client, so launch gates cannot run here.'

    if ($Json) { $results | ConvertTo-Json -Depth 4 } else { $results | Format-Table -AutoSize | Out-String -Width 300 | Write-Output }
    exit 1
}

Add-Result -Strategy 'New Outlook installed' -Status 'Available' `
    -Target $package.PackageFamilyName `
    -Detail "Microsoft.OutlookForWindows $($package.Version)."

# ---------------------------------------------------------------------------
# Strategy 1: app execution alias
# ---------------------------------------------------------------------------

$olk = Get-Command -Name 'olk.exe' -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1

if ($olk) {
    Add-Result -Strategy 'App execution alias' -Status 'Available' -Target $olk.Source `
        -Detail 'Bare olk.exe resolves on PATH. This is a reparse point into WindowsApps rather than a real executable, which is why it survives package updates and a versioned path does not.'
}
else {
    Add-Result -Strategy 'App execution alias' -Status 'Unavailable' `
        -Detail 'Bare olk.exe does not resolve. Per section 17, a failure here degrades the Open Outlook action to an approved web-only mode rather than stopping the product.'
}

# ---------------------------------------------------------------------------
# Strategy 2: package activation through the application user model ID
# ---------------------------------------------------------------------------

$appId = $null

try {
    $manifest = Get-AppxPackageManifest -Package $package.PackageFullName -ErrorAction Stop
    $application = $manifest.Package.Applications.Application | Select-Object -First 1

    if ($application -and $application.Id) {
        $appId = "$($package.PackageFamilyName)!$($application.Id)"
        Add-Result -Strategy 'Package activation' -Status 'Available' -Target $appId `
            -Detail 'The application user model ID resolves from the installed package manifest, so activation needs no versioned path and no PATH lookup.'
    }
    else {
        Add-Result -Strategy 'Package activation' -Status 'Unavailable' `
            -Detail 'The package manifest exposes no application entry to activate.'
    }
}
catch {
    Add-Result -Strategy 'Package activation' -Status 'Unavailable' `
        -Detail 'The package manifest could not be read from this session.'
}

# ---------------------------------------------------------------------------
# What the implementation must never do
# ---------------------------------------------------------------------------

Add-Result -Strategy 'Versioned WindowsApps path' -Status 'Prohibited' `
    -Target $package.InstallLocation `
    -Detail 'Recorded for completeness only. This path contains the package version and changes on every Outlook update, so hard-coding it produces a launch failure after the next update. Section 9 forbids it.'

# ---------------------------------------------------------------------------
# Optionally launch
# ---------------------------------------------------------------------------

if ($Launch) {
    $preferred = $results | Where-Object { $_.Status -eq 'Available' -and $_.Strategy -eq 'App execution alias' } |
        Select-Object -First 1

    if (-not $preferred) {
        $preferred = $results | Where-Object { $_.Status -eq 'Available' -and $_.Strategy -eq 'Package activation' } |
            Select-Object -First 1
    }

    if (-not $preferred) {
        Write-Output 'No launch strategy is available on this machine.'
        exit 1
    }

    Write-Output "Launching New Outlook via: $($preferred.Strategy) -> $($preferred.Target)"

    try {
        if ($preferred.Strategy -eq 'App execution alias') {
            Start-Process -FilePath $preferred.Target
        }
        else {
            # shell:AppsFolder activation, which is the documented way to start a packaged
            # application by its user model ID.
            Start-Process -FilePath "shell:AppsFolder\$($preferred.Target)"
        }

        Write-Output 'Launch command returned without error. Observe and record: whether the window appears, how long it takes, whether it restores a previous view, and what happens when New Outlook is already running, updating, or damaged.'
    }
    catch {
        Write-Output "Launch failed: $($_.Exception.GetType().Name)"
        exit 1
    }
}

if ($Json) {
    $results | ConvertTo-Json -Depth 4
}
else {
    $results | Format-Table -AutoSize -Property Status, Strategy, Target, Detail | Out-String -Width 300 | Write-Output

    if (-not $Launch) {
        Write-Output 'Resolution only. Re-run with -Launch to exercise the launch itself, which is the half of gate 7 that inspection cannot establish.'
    }
}

exit 0

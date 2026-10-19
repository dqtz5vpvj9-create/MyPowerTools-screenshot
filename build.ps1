[CmdletBinding()]
param(
    [string] $MyPowerToolsRepoRoot,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $PrebuiltCliPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = if ([string]::IsNullOrWhiteSpace($MyPowerToolsRepoRoot)) {
    [System.IO.Path]::GetFullPath((Join-Path $toolRoot '..\..'))
} else {
    [System.IO.Path]::GetFullPath($MyPowerToolsRepoRoot)
}
$projectPath = Join-Path $toolRoot 'current-integration\src\Screenshot.MyPowerTools\Screenshot.MyPowerTools.csproj'
$surfaceProjectPath = Join-Path $toolRoot 'current-integration\src\Screenshot.Surface\Screenshot.Surface.csproj'
$modulePackageRoot = Join-Path $toolRoot 'current-integration\modules\screenshot'
$surfacePackageRoot = Join-Path $modulePackageRoot 'ui\surface'
$repositoryModuleRoot = Join-Path $repoRoot 'modules\screenshot'
$artifactsRoot = Join-Path $toolRoot 'artifacts'
$artifactPackage = Join-Path $artifactsRoot 'package'
$cliProject = Join-Path $repoRoot 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj'

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'src\MyPowerTools.Abstractions\MyPowerTools.Abstractions.csproj') -PathType Leaf)) {
    throw "MyPowerToolsRepoRoot '$repoRoot' is invalid."
}

# Only reuse a CLI explicitly supplied by the caller; standalone builds retain their build behavior.
$cliExecutable = $null
$cliInvocationArguments = @('run', '--project', $cliProject, '--configuration', $Configuration, '--')
if ($PSBoundParameters.ContainsKey('PrebuiltCliPath')) {
    if ([string]::IsNullOrWhiteSpace($PrebuiltCliPath) -or
        -not (Test-Path -LiteralPath $PrebuiltCliPath -PathType Leaf)) {
        throw "Prebuilt CLI '$PrebuiltCliPath' is missing or is not a file."
    }
    $cliExecutable = (Resolve-Path -LiteralPath $PrebuiltCliPath -ErrorAction Stop).ProviderPath
    $cliInvocationArguments = @()
}

$dotnet = Get-Command 'dotnet' -CommandType Application -ErrorAction Stop
if ($null -eq $cliExecutable) {
    $cliExecutable = $dotnet.Source
}
$dotnetArguments = @(
    'build'
    $projectPath
    '--configuration'
    $Configuration
    '--nologo'
    "-p:MyPowerToolsRepoRoot=$repoRoot"
)
& $dotnet.Source @dotnetArguments
$dotnetExitCode = $LASTEXITCODE
if ($dotnetExitCode -ne 0) {
    throw "dotnet build failed with exit code $dotnetExitCode."
}

$surfaceOutput = Join-Path $artifactsRoot 'surface'
$surfaceArguments = @(
    'build'
    $surfaceProjectPath
    '--configuration'
    $Configuration
    '--nologo'
    '--output'
    $surfaceOutput
    "-p:MyPowerToolsRepoRoot=$repoRoot"
)
& $dotnet.Source @surfaceArguments
$surfaceExitCode = $LASTEXITCODE
if ($surfaceExitCode -ne 0) {
    throw "dotnet surface build failed with exit code $surfaceExitCode."
}

New-Item -ItemType Directory -Path $surfacePackageRoot -Force | Out-Null
Get-ChildItem -LiteralPath $surfacePackageRoot -File |
    Where-Object { $_.Name -notlike 'Screenshot.Surface.*' } |
    Remove-Item -Force
foreach ($surfaceFileName in @('Screenshot.Surface.dll', 'Screenshot.Surface.pdb', 'Screenshot.Surface.deps.json')) {
    $surfaceFile = Join-Path $surfaceOutput $surfaceFileName
    if (-not (Test-Path -LiteralPath $surfaceFile -PathType Leaf)) {
        throw "Expected Surface file '$surfaceFile' is missing."
    }
    Copy-Item -LiteralPath $surfaceFile -Destination $surfacePackageRoot -Force
}

# Drop any leftover non-surface files so a recursive repo copy cannot reintroduce them.
Get-ChildItem -LiteralPath $surfacePackageRoot -File |
    Where-Object { $_.Name -notin @('Screenshot.Surface.dll', 'Screenshot.Surface.pdb', 'Screenshot.Surface.deps.json') } |
    Remove-Item -Force

$expectedRepositoryModuleRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'modules\screenshot'))
if (-not $repositoryModuleRoot.Equals($expectedRepositoryModuleRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to refresh module path '$repositoryModuleRoot'; expected exactly '$expectedRepositoryModuleRoot'."
}

if (Test-Path -LiteralPath $repositoryModuleRoot) {
    $resolvedRepositoryModule = [System.IO.Path]::GetFullPath($repositoryModuleRoot)
    $modulesRootPrefix = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'modules')).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRepositoryModule.Equals($expectedRepositoryModuleRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected module path '$resolvedRepositoryModule'."
    }
    if (-not $resolvedRepositoryModule.StartsWith($modulesRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside modules root: '$resolvedRepositoryModule'."
    }
    Remove-Item -LiteralPath $repositoryModuleRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $repositoryModuleRoot -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $modulePackageRoot -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $repositoryModuleRoot -Recurse -Force
}

if (Test-Path -LiteralPath $artifactPackage) {
    $resolvedTarget = [System.IO.Path]::GetFullPath($artifactPackage)
    $allowedPrefix = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTarget.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove artifact path outside '$artifactsRoot'."
    }
    Remove-Item -LiteralPath $artifactPackage -Recurse -Force
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Copy-Item -LiteralPath $modulePackageRoot -Destination $artifactPackage -Recurse -Force

$expectedAssembly = Join-Path $artifactPackage 'Screenshot.MyPowerTools.dll'
if (-not (Test-Path -LiteralPath $expectedAssembly -PathType Leaf)) {
    throw "Expected adapter assembly '$expectedAssembly' is missing."
}

$runtimeManifest = Join-Path $artifactPackage 'Screenshot.MyPowerTools.deps.json'
if (-not (Test-Path -LiteralPath $runtimeManifest -PathType Leaf)) {
    throw "Expected Screenshot runtime dependency '$runtimeManifest' is missing."
}

$expectedSurfaceAssembly = Join-Path $artifactPackage 'ui\surface\Screenshot.Surface.dll'
if (-not (Test-Path -LiteralPath $expectedSurfaceAssembly -PathType Leaf)) {
    throw "Expected Surface assembly '$expectedSurfaceAssembly' is missing."
}

$hashArguments = $cliInvocationArguments + @(
    'package'
    'hash'
    $artifactPackage
)
& $cliExecutable @hashArguments
if ($LASTEXITCODE -ne 0) {
    throw "Failed to write package hashes for Screenshot."
}

$signArguments = $cliInvocationArguments + @(
    'package'
    'sign-local'
    $artifactPackage
)
& $cliExecutable @signArguments
if ($LASTEXITCODE -ne 0) {
    throw "Failed to write the local signature hook for Screenshot."
}

$sharedRoot = Join-Path $artifactPackage 'shared'
if (Test-Path -LiteralPath $sharedRoot -PathType Container) {
    foreach ($destination in @($modulePackageRoot, $repositoryModuleRoot)) {
        $sharedDestination = Join-Path $destination 'shared'
        New-Item -ItemType Directory -Path $sharedDestination -Force | Out-Null
        Get-ChildItem -LiteralPath $sharedRoot -Force |
            Copy-Item -Destination $sharedDestination -Force
    }
}

Write-Output "Release package staged at $artifactPackage"

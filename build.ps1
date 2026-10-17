$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildDir = Join-Path $RepoRoot "build"
$PackageRoot = Join-Path $BuildDir "QuickSell-v4.3.0"
$ClientPackage = Join-Path $PackageRoot "BepInEx\plugins\QuickSell"
$ServerPackage = Join-Path $PackageRoot "SPT_Runtime\user\mods\QuickSell"
$ZipPath = Join-Path $BuildDir "QuickSell-v4.3.0.zip"
$RefRoot = Join-Path $BuildDir "_spt_refs"

function Pause-AtEnd {
    Write-Host ""
    Write-Host "The window will stay open so you can copy any errors." -ForegroundColor DarkGray
    [void](Read-Host "Press Enter to close")
}

function Run-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host ""
    Write-Host ("> dotnet " + ($Arguments -join " ")) -ForegroundColor Cyan

    & dotnet @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE"
    }
}

function Require-File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Name not found:`n$Path"
    }
}

function Copy-ReferenceFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$RelativeDestination
    )

    Require-File $Source $RelativeDestination

    $Destination = Join-Path $RefRoot $RelativeDestination
    $DestinationDir = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $DestinationDir -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

try {
    Clear-Host

    Write-Host "==============================================" -ForegroundColor Cyan
    Write-Host " BlackHawk QuickSell v4.3.0" -ForegroundColor Cyan
    Write-Host "==============================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "IMPORTANT:" -ForegroundColor Yellow
    Write-Host " - This script does NOT copy compiled files into your SPT install."
    Write-Host " - Your SPT folder is used only as a source for required game DLLs."
    Write-Host " - Final files are created inside this source folder under .\build\"
    Write-Host ""

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK was not found. Install the .NET SDK and run this script again."
    }

    Write-Host "Installed .NET SDKs:" -ForegroundColor Cyan
    & dotnet --list-sdks
    Write-Host ""

    do {
        $SptPath = Read-Host "Paste your SPT 4.1.x root folder"
        $SptPath = $SptPath.Trim().Trim('"')

        $AssemblyCSharp = Join-Path $SptPath "EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll"
        $BepInExDll = Join-Path $SptPath "BepInEx\core\BepInEx.dll"

        if ((Test-Path -LiteralPath $AssemblyCSharp -PathType Leaf) -and
            (Test-Path -LiteralPath $BepInExDll -PathType Leaf)) {
            break
        }

        Write-Host ""
        Write-Host "That does not look like an SPT root folder." -ForegroundColor Red
        Write-Host "Expected to find:"
        Write-Host "  EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll"
        Write-Host "  BepInEx\core\BepInEx.dll"
        Write-Host ""
    }
    while ($true)

    $ClientProject = Join-Path $RepoRoot "Tarkov-QuickSell\QuickSell.csproj"
    $HelperProject = Join-Path $RepoRoot "tools\TestFixes\QuickSell.TestFixes.csproj"
    $PatcherProject = Join-Path $RepoRoot "tools\Patcher\Patcher.csproj"
    $ServerProject = Join-Path $RepoRoot "QuickSell-Server\QuickSell.Server.csproj"
    $ConfigJson = Join-Path $RepoRoot "Tarkov-QuickSell\config.json"

    Require-File $ClientProject "QuickSell client project"
    Require-File $HelperProject "QuickSell runtime helper project"
    Require-File $PatcherProject "QuickSell patcher project"
    Require-File $ServerProject "QuickSell server project"
    Require-File $ConfigJson "QuickSell config.json"

    Write-Host ""
    Write-Host "SPT source:" -ForegroundColor Green
    Write-Host "  $SptPath"
    Write-Host ""
    Write-Host "Output folder:" -ForegroundColor Green
    Write-Host "  $PackageRoot"
    Write-Host ""

    if (Test-Path -LiteralPath $BuildDir) {
        Remove-Item -LiteralPath $BuildDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $ClientPackage -Force | Out-Null
    New-Item -ItemType Directory -Path $ServerPackage -Force | Out-Null
    New-Item -ItemType Directory -Path $RefRoot -Force | Out-Null

    Write-Host "Preparing safe local reference copy..." -ForegroundColor Cyan

    $ReferenceFiles = @(
        "BepInEx\core\BepInEx.dll",
        "BepInEx\core\0Harmony.dll",
        "BepInEx\plugins\spt\spt-common.dll",
        "BepInEx\plugins\spt\spt-reflection.dll",
        "EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll",
        "EscapeFromTarkov_Data\Managed\Comfort.dll",
        "EscapeFromTarkov_Data\Managed\Comfort.Unity.dll",
        "EscapeFromTarkov_Data\Managed\CommonExtensions.dll",
        "EscapeFromTarkov_Data\Managed\ItemComponent.Types.dll",
        "EscapeFromTarkov_Data\Managed\Newtonsoft.Json.dll",
        "EscapeFromTarkov_Data\Managed\Sirenix.Serialization.dll",
        "EscapeFromTarkov_Data\Managed\Sirenix.OdinInspector.Attributes.dll",
        "EscapeFromTarkov_Data\Managed\Unity.TextMeshPro.dll",
        "EscapeFromTarkov_Data\Managed\UnityEngine.dll",
        "EscapeFromTarkov_Data\Managed\UnityEngine.CoreModule.dll",
        "EscapeFromTarkov_Data\Managed\UnityEngine.IMGUIModule.dll",
        "EscapeFromTarkov_Data\Managed\UnityEngine.InputLegacyModule.dll",
        "EscapeFromTarkov_Data\Managed\UnityEngine.TextRenderingModule.dll",
        "EscapeFromTarkov_Data\Managed\UnityEngine.UI.dll",
        "EscapeFromTarkov_Data\Managed\UnityEngine.UIModule.dll"
    )

    foreach ($RelativePath in $ReferenceFiles) {
        Copy-ReferenceFile (Join-Path $SptPath $RelativePath) $RelativePath
    }

    Write-Host ""
    Write-Host "1/4 Building client..." -ForegroundColor Cyan
    Run-DotNet @(
        "build",
        $ClientProject,
        "-c", "Release",
        "--nologo",
        "-p:SptPath=$RefRoot",
        "-p:DeployOnBuild=false"
    )

    Write-Host ""
    Write-Host "2/4 Building runtime helper..." -ForegroundColor Cyan
    Run-DotNet @(
        "build",
        $HelperProject,
        "-c", "Release",
        "--nologo"
    )

    Write-Host ""
    Write-Host "3/4 Building patcher..." -ForegroundColor Cyan
    Run-DotNet @(
        "build",
        $PatcherProject,
        "-c", "Release",
        "--nologo"
    )

    Write-Host ""
    Write-Host "4/4 Building server component..." -ForegroundColor Cyan
    Run-DotNet @(
        "build",
        $ServerProject,
        "-c", "Release",
        "--nologo",
        "-p:SptPath=$RefRoot",
        "-p:DeployOnBuild=false"
    )

    $ClientDll = Join-Path $RepoRoot "Tarkov-QuickSell\bin\Release\netstandard2.1\QuickSell.dll"
    $HelperDll = Join-Path $RepoRoot "tools\TestFixes\bin\Release\netstandard2.1\QuickSell.TestFixes.dll"
    $PatcherDll = Join-Path $RepoRoot "tools\Patcher\bin\Release\net8.0\Patcher.dll"
    $ServerDll = Join-Path $RepoRoot "QuickSell-Server\bin\Release\net10.0\BlackHawk.QuickSell.Server.dll"

    Require-File $ClientDll "Compiled QuickSell.dll"
    Require-File $HelperDll "Compiled QuickSell.TestFixes.dll"
    Require-File $PatcherDll "Compiled Patcher.dll"
    Require-File $ServerDll "Compiled BlackHawk.QuickSell.Server.dll"

    Write-Host ""
    Write-Host "Inlining runtime helper into QuickSell.dll..." -ForegroundColor Cyan
    Write-Host "Using .NET roll-forward so the net8 patcher can run on an installed newer runtime." -ForegroundColor DarkGray
    Run-DotNet @(
        "--roll-forward", "Major",
        $PatcherDll,
        $ClientDll,
        $HelperDll
    )

    Write-Host ""
    Write-Host "Verifying final one-DLL client build..." -ForegroundColor Cyan
    Run-DotNet @(
        "--roll-forward", "Major",
        $PatcherDll,
        "--verify",
        $ClientDll,
        "4.3.0"
    )

    Copy-Item -LiteralPath $ClientDll -Destination (Join-Path $ClientPackage "QuickSell.dll") -Force
    Copy-Item -LiteralPath $ConfigJson -Destination (Join-Path $ClientPackage "config.json") -Force
    Copy-Item -LiteralPath $ServerDll -Destination (Join-Path $ServerPackage "BlackHawk.QuickSell.Server.dll") -Force

    $ClientDllCount = @(Get-ChildItem -LiteralPath $ClientPackage -Filter "*.dll" -File).Count
    if ($ClientDllCount -ne 1) {
        throw "Final client package should contain exactly one DLL, but found $ClientDllCount."
    }

    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    Compress-Archive -Path (Join-Path $PackageRoot "*") -DestinationPath $ZipPath -CompressionLevel Optimal

    Write-Host ""
    Write-Host "==============================================" -ForegroundColor Green
    Write-Host " BUILD SUCCESSFUL" -ForegroundColor Green
    Write-Host "==============================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Package folder:" -ForegroundColor Cyan
    Write-Host "  $PackageRoot"
    Write-Host ""
    Write-Host "Package ZIP:" -ForegroundColor Cyan
    Write-Host "  $ZipPath"
    Write-Host ""
    Write-Host "Nothing was copied into your SPT installation." -ForegroundColor Yellow
    Write-Host ""
}
catch {
    Write-Host ""
    Write-Host "==============================================" -ForegroundColor Red
    Write-Host " BUILD FAILED" -ForegroundColor Red
    Write-Host "==============================================" -ForegroundColor Red
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ""
    Write-Host "Copy the error above and send it to me." -ForegroundColor Yellow
}
finally {
    Pause-AtEnd
}

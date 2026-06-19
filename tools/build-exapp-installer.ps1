param(
    [string]$Version = "0.1.0",
    [string]$Configuration = "Release",
    [string]$PublishDirectory = "artifacts/app/publish/desktop",
    [string]$OutputDirectory = "artifacts/app",
    [string]$Manufacturer = "ExApp",
    [string]$UpgradeCode = "8B9C5D4B-1805-43A2-9E4C-0CEB7D2D6F28",
    [string]$WixToolPath = ".tools/wix/wix.exe"
)

$ErrorActionPreference = "Stop"

function Convert-ToWixId {
    param([string]$Value)
    $id = [Regex]::Replace($Value, "[^A-Za-z0-9_\.]", "_")
    if ($id -notmatch "^[A-Za-z_]") {
        $id = "Id_$id"
    }
    if ($id.Length -gt 70) {
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value))).Substring(0, 12)
        $id = $id.Substring(0, 57) + "_" + $hash
    }
    return $id
}

function Convert-ToXmlText {
    param([string]$Value)
    return [Security.SecurityElement]::Escape($Value)
}

function Add-WixDirectory {
    param(
        [System.Text.StringBuilder]$Builder,
        [string]$CurrentRelativePath,
        [int]$Indent
    )

    $children = $directories.Keys |
        Where-Object {
            $parent = Split-Path $_ -Parent
            if ($CurrentRelativePath) {
                $parent -eq $CurrentRelativePath
            }
            else {
                [string]::IsNullOrEmpty($parent)
            }
        } |
        Sort-Object

    foreach ($child in $children) {
        $segments = $child -split "[\\/]"
        $id = Convert-ToWixId "Dir_$child"
        $name = Convert-ToXmlText $segments[-1]
        $padding = " " * $Indent
        $Builder.AppendLine("$padding<Directory Id=`"$id`" Name=`"$name`">") | Out-Null
        Add-WixDirectory -Builder $Builder -CurrentRelativePath $child -Indent ($Indent + 2)
        $Builder.AppendLine("$padding</Directory>") | Out-Null
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishRoot = Resolve-Path (Join-Path $repoRoot $PublishDirectory)
$outputRoot = Join-Path $repoRoot $OutputDirectory
$installerRoot = Join-Path $outputRoot "installer"
$sourcePath = Join-Path $installerRoot "ExApp.Installer.wxs"
$msiPath = Join-Path $outputRoot "exapp-$Version-win-x64.msi"
$wixPath = Join-Path $repoRoot $WixToolPath

if (-not (Test-Path (Join-Path $publishRoot "ExApp.Desktop.exe"))) {
    & (Join-Path $PSScriptRoot "package-exapp.ps1") -Configuration $Configuration -Version $Version -OutputDirectory $OutputDirectory | Out-Host
}

if (-not (Test-Path $wixPath)) {
    $toolDir = Split-Path $wixPath -Parent
    New-Item -ItemType Directory -Force $toolDir | Out-Null
    dotnet tool install wix --tool-path $toolDir --version 5.* | Out-Host
}

Remove-Item -Recurse -Force $installerRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $installerRoot | Out-Null

$files = Get-ChildItem -Path $publishRoot -Recurse -File | Sort-Object FullName
$directories = @{}
foreach ($file in $files) {
    $relativeDirectory = Split-Path ([IO.Path]::GetRelativePath($publishRoot, $file.DirectoryName)) -NoQualifier
    if ($relativeDirectory -and $relativeDirectory -ne ".") {
        $segments = $relativeDirectory -split "[\\/]" | Where-Object { $_ }
        $current = ""
        foreach ($segment in $segments) {
            $current = if ($current) { Join-Path $current $segment } else { $segment }
            $directories[$current] = $true
        }
    }
}

$directoryXml = New-Object System.Text.StringBuilder
$directoryXml.AppendLine('      <Directory Id="INSTALLFOLDER" Name="ExApp">') | Out-Null
Add-WixDirectory -Builder $directoryXml -CurrentRelativePath "" -Indent 8
$directoryXml.AppendLine('      </Directory>') | Out-Null

$componentsXml = New-Object System.Text.StringBuilder
$featureRefsXml = New-Object System.Text.StringBuilder
$index = 0
foreach ($file in $files) {
    $relativePath = [IO.Path]::GetRelativePath($publishRoot, $file.FullName)
    $relativeDirectory = Split-Path $relativePath -Parent
    $directoryId = if ($relativeDirectory) { Convert-ToWixId "Dir_$relativeDirectory" } else { "INSTALLFOLDER" }
    $componentId = Convert-ToWixId "Cmp_$index`_$relativePath"
    $fileId = Convert-ToWixId "File_$index`_$relativePath"
    $source = Convert-ToXmlText $file.FullName
    $name = Convert-ToXmlText $file.Name
    $componentsXml.AppendLine("    <Component Id=`"$componentId`" Directory=`"$directoryId`" Guid=`"*`">") | Out-Null
    $componentsXml.AppendLine("      <File Id=`"$fileId`" Source=`"$source`" Name=`"$name`" KeyPath=`"yes`" />") | Out-Null
    $componentsXml.AppendLine("    </Component>") | Out-Null
    $featureRefsXml.AppendLine("      <ComponentRef Id=`"$componentId`" />") | Out-Null
    $index++
}

$iconPath = Join-Path $publishRoot "AppIcon.ico"
if (-not (Test-Path $iconPath)) {
    $iconPath = Join-Path $publishRoot "Assets\AppIcon.ico"
}
$source = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="ExApp" Manufacturer="$([Security.SecurityElement]::Escape($Manufacturer))" Version="$Version" UpgradeCode="$UpgradeCode" Scope="perUser">
    <MajorUpgrade DowngradeErrorMessage="A newer version of ExApp is already installed." />
    <MediaTemplate EmbedCab="yes" />
    <Icon Id="AppIcon.ico" SourceFile="$([Security.SecurityElement]::Escape($iconPath))" />
    <Property Id="ARPPRODUCTICON" Value="AppIcon.ico" />
    <SetProperty Id="ARPINSTALLLOCATION" Value="[INSTALLFOLDER]" After="CostFinalize" Sequence="execute" />

    <StandardDirectory Id="LocalAppDataFolder">
      <Directory Id="UserProgramsFolder" Name="Programs">
$directoryXml      </Directory>
    </StandardDirectory>

    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="AppProgramMenuFolder" Name="ExApp" />
    </StandardDirectory>

    <Component Id="StartMenuShortcutComponent" Directory="AppProgramMenuFolder" Guid="*">
      <Shortcut Id="StartMenuShortcut" Name="ExApp" Description="Launch ExApp" Target="[INSTALLFOLDER]ExApp.Desktop.exe" WorkingDirectory="INSTALLFOLDER" Icon="AppIcon.ico" />
      <RemoveFolder Id="RemoveAppProgramMenuFolder" On="uninstall" />
      <RegistryValue Root="HKCU" Key="Software\ExApp\ExApp" Name="StartMenuShortcut" Type="integer" Value="1" KeyPath="yes" />
    </Component>

$componentsXml
    <Feature Id="MainFeature" Title="ExApp" Level="1">
$featureRefsXml      <ComponentRef Id="StartMenuShortcutComponent" />
    </Feature>
  </Package>
</Wix>
"@

Set-Content -Encoding UTF8 -Path $sourcePath -Value $source
Remove-Item -Force $msiPath -ErrorAction SilentlyContinue
& $wixPath build $sourcePath -arch x64 -o $msiPath
if ($LASTEXITCODE -ne 0) {
    throw "WiX build failed."
}

$hash = (Get-FileHash -Algorithm SHA256 -Path $msiPath).Hash.ToLowerInvariant()
$hash | Set-Content -Encoding ASCII "$msiPath.sha256"
(Get-Item $msiPath).Length | Set-Content -Encoding ASCII "$msiPath.size"
Write-Output $msiPath

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
[xml]$project = Get-Content -LiteralPath (Join-Path $repo 'Gallery.Version.props')
$version = $project.Project.PropertyGroup.Version
$output = Join-Path $repo 'bin\Debug\net9.0-windows10.0.19041.0\win-x64'
$work = Join-Path $repo ('obj\release-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $work ('Gallery-' + $version)
$helper = Join-Path $work 'helper'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
function Run-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. No release packages were produced.' }
}
try {
    Get-Process Gallery -ErrorAction SilentlyContinue | ForEach-Object {
        $_.CloseMainWindow() | Out-Null
        if (!$_.WaitForExit(3000)) { Stop-Process -Id $_.Id -Force }
    }
    $buildArgs = @('publish', 'Gallery.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publish,
        '-p:CopyRetryCount=0', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:TreatWarningsAsErrors=true', '-p:EnableDevelopmentVerification=false',
        ('-p:OutputPath=' + (Join-Path $work 'build')))
    Run-Dotnet $buildArgs
    # 分离 OutputPath 后，dotnet publish 不会把 XAML 编译产物与资源索引带进 publish 目录。
    # 缺这些文件会让安装版启动即崩（XamlParseException 0x802B000A），必须显式补齐。
    $buildDir = Join-Path $work 'build'
    $xamlFiles = @(Get-ChildItem -LiteralPath $buildDir -Recurse -File -Filter '*.xbf' |
        ForEach-Object { [IO.Path]::GetRelativePath($buildDir, $_.FullName) })
    $xamlFiles += 'Gallery.pri'
    if (-not (@($xamlFiles | Where-Object { $_ -eq 'App.xbf' }).Count)) {
        throw 'Release build missing App.xbf; refusing to package a non-starting app.'
    }
    foreach ($file in $xamlFiles) {
        $source = Join-Path $buildDir $file
        $destination = Join-Path $publish $file
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    Write-Output ('XAML artifacts added to package: ' + $xamlFiles.Count)
    Run-Dotnet @('publish', 'Updater/Gallery.Updater.csproj', '-c', 'Release', '-o', $helper, ('-p:Version=' + $version),
        '-p:TreatWarningsAsErrors=true', ('-p:OutputPath=' + (Join-Path $work 'helper-build')))
    Copy-Item -LiteralPath (Join-Path $helper 'Gallery.Updater.exe') -Destination $publish
    Set-Content -LiteralPath (Join-Path $publish 'distribution-kind.txt') -Value 'portable' -Encoding ascii -NoNewline
    # Native dependencies may publish their own symbols even when managed DebugType is None.
    Get-ChildItem -LiteralPath $publish -Recurse -File -Filter '*.pdb' | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
    }
    $files = @(Get-ChildItem -LiteralPath $publish -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($publish, $_.FullName) })
    foreach ($file in $files) {
        if ($file -match '(?i)(^|[\\/])(tests?|tools|archive|models|packs|\.staging)([\\/]|$)|(^|[\\/])DirectML\.Debug\.dll$|\.(pdb|lic|log|db)$|(^|[\\/])(settings|folder_map|personal_rules)\.json$') {
            throw ('Forbidden release file: ' + $file)
        }
    }
    $files += 'application-files.json'
    $files = @($files | Sort-Object)
    ConvertTo-Json -InputObject $files | Set-Content -LiteralPath (Join-Path $publish 'application-files.json') -Encoding utf8
    $fileList = Join-Path $work 'installer-files.iss'
    $lines = foreach ($file in $files) {
        $relativeDir = Split-Path $file -Parent
        $destination = if ($relativeDir) { '{app}\' + $relativeDir } else { '{app}' }
        'Source: "' + (Join-Path $publish $file) + '"; DestDir: "' + $destination + '"; Flags: ignoreversion'
    }
    Set-Content -LiteralPath $fileList -Value $lines -Encoding utf8
    $dist = Join-Path $output 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    $zipName = 'Gallery-' + $version + '-win-x64.zip'
    $setupName = 'Gallery-' + $version + '-setup.exe'
    & tar.exe -a -c -f (Join-Path $dist $zipName) -C $work ('Gallery-' + $version)
    if ($LASTEXITCODE -ne 0) { throw 'ZIP creation failed.' }
    $iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    & $iscc /Q ('/DAppVersion=' + $version) ('/DSourceDir=' + $publish) ('/DOutDir=' + $dist) ('/DFileList=' + $fileList) (Join-Path $repo 'tools\installer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer creation failed.' }
    function Asset([string]$name) {
        $path = Join-Path $dist $name
        @{ name = $name; size = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    @{ version = $version; installer = (Asset $setupName); portable = (Asset $zipName) } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $dist 'update-manifest.json') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $repo 'RELEASE_NOTES.md') -Destination $dist
    # Synchronize only files owned by the new product. Distribution packages remain in dist.
    foreach ($file in $files) {
        $target = Join-Path $output $file
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $publish $file) -Destination $target -Force
    }
    Get-ChildItem -LiteralPath $output -Recurse -File -Filter '*.pdb' | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    $oldDebugLayer = Join-Path $output 'DirectML.Debug.dll'
    if (Test-Path -LiteralPath $oldDebugLayer) { Remove-Item -LiteralPath $oldDebugLayer -Force }
    Write-Output ('Release packages: ' + $dist)
}
finally {
    $resolved = [IO.Path]::GetFullPath($work)
    $expected = [IO.Path]::GetFullPath((Join-Path $repo 'obj')) + [IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

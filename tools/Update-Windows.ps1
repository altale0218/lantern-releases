param([string]$InstallDirectory,[switch]$NoLaunch,[switch]$NonInteractive,[switch]$ForceClose)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
$payloadRoot=[IO.Path]::GetFullPath($PSScriptRoot)
function Below([string]$root,[string]$relative) {
    if([IO.Path]::IsPathRooted($relative)){throw '更新清单包含绝对路径'}
    $full=[IO.Path]::GetFullPath((Join-Path $root $relative))
    if(-not $full.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '更新文件超出安装目录'}
    return $full
}
function Assert-NoLink([string]$file,[string]$root) {
    $current=$file
    while($current.Length -ge $root.Length) {
        if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '安装目录或文件不能是链接'}
        if($current -eq $root){break};$current=[IO.Path]::GetDirectoryName($current)
    }
}
try {
    $manifest=Get-Content -LiteralPath (Join-Path $payloadRoot 'release-files.json') -Encoding UTF8 -Raw | ConvertFrom-Json
    $binaryVersion=([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payloadRoot 'Lantern.dll')).ProductVersion -split '\+')[0]
    if(-not $manifest.files -or $manifest.version -ne $binaryVersion){throw '更新包清单与主程序版本不匹配'}
    $entries=@($manifest.files)
    if(-not ($entries.path -contains 'Lantern.exe')){throw '更新包缺少主程序'}
    foreach($entry in $entries) {
        $source=Below $payloadRoot $entry.path;Assert-NoLink $source $payloadRoot
        if(-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256){throw ('更新包校验失败：'+$entry.path)}
    }
    if(-not $InstallDirectory) {
        $running=@(Get-Process -Name Lantern -ErrorAction SilentlyContinue | Where-Object {$_.Path -and [IO.Path]::GetFileName($_.Path) -eq 'Lantern.exe'} | Select-Object -ExpandProperty Path -Unique)
        if($running.Count -eq 1){$InstallDirectory=Split-Path -Parent $running[0]}
        else {
            $targets=@();$shell=New-Object -ComObject WScript.Shell
            foreach($menu in @([Environment]::GetFolderPath('StartMenu'),[Environment]::GetFolderPath('CommonStartMenu'))) {
                foreach($link in Get-ChildItem -LiteralPath $menu -Filter '*.lnk' -Recurse -File -ErrorAction SilentlyContinue) {
                    if($link.BaseName -match 'Lantern|闪传') {
                        $shortcut=$shell.CreateShortcut($link.FullName);$target=$shortcut.TargetPath
                        if($target -and [IO.Path]::GetFileName($target) -eq 'Lantern.exe' -and (Test-Path -LiteralPath $target)){$targets+=Split-Path -Parent $target}
                    }
                }
            }
            $targets=@($targets | Select-Object -Unique)
            if($targets.Count -eq 1){$InstallDirectory=$targets[0]}
        }
    }
    if(-not $InstallDirectory) {
        if($NonInteractive){throw '请指定原安装目录'}
        $dialog=New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description='选择原 Lantern.exe 所在文件夹，保留开始菜单快捷方式的路径'
        if($dialog.ShowDialog() -ne 'OK'){return};$InstallDirectory=$dialog.SelectedPath;$dialog.Dispose()
    }
    $targetRoot=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    if(-not (Test-Path -LiteralPath (Join-Path $targetRoot 'Lantern.exe') -PathType Leaf)){throw '请选择含有原 Lantern.exe 的安装目录'}
    Assert-NoLink $targetRoot $targetRoot
    if($targetRoot -eq $payloadRoot){throw '请先把新更新包解压到临时文件夹，再运行更新脚本；不要直接覆盖运行中的程序'}
    foreach($entry in $entries){Assert-NoLink (Below $targetRoot $entry.path) $targetRoot}
    while(@(Get-Process -Name Lantern -ErrorAction SilentlyContinue | Where-Object {$_.Path -and [IO.Path]::GetFullPath($_.Path) -eq (Join-Path $targetRoot 'Lantern.exe')}).Count -gt 0) {
        if($ForceClose) {
            foreach($process in Get-Process -Name Lantern -ErrorAction SilentlyContinue | Where-Object {$_.Path -and [IO.Path]::GetFullPath($_.Path) -eq (Join-Path $targetRoot 'Lantern.exe')}) {
                $null=$process.CloseMainWindow()
                if(-not $process.WaitForExit(3000)) {
                    $still=Get-Process -Id $process.Id -ErrorAction SilentlyContinue
                    if($still -and $still.Path -eq (Join-Path $targetRoot 'Lantern.exe')){Stop-Process -Id $still.Id -Force}
                }
            }
            continue
        }
        if($NonInteractive){throw '程序正在运行，请先关闭 Lantern 后重试；未替换任何文件'}
        $choice=[System.Windows.Forms.MessageBox]::Show("请先结束传输并关闭闪传 Lantern，再点击「重试」。`n`n将覆盖原目录：$targetRoot`n保留开始菜单路径、设备密钥、设置和记录。",'关闭程序后更新','RetryCancel','Information')
        if($choice -ne 'Retry'){return}
    }
    $oldFiles=@()
    $oldManifest=Join-Path $targetRoot 'release-files.json'
    if(Test-Path -LiteralPath $oldManifest){try{$oldFiles=@((Get-Content -LiteralPath $oldManifest -Encoding UTF8 -Raw|ConvertFrom-Json).files.path)}catch{}}
    foreach($entry in $entries) {
        $source=Below $payloadRoot $entry.path;$target=Below $targetRoot $entry.path
        $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        $temporary=$target+'.lantern-update.tmp';Copy-Item -LiteralPath $source -Destination $temporary -Force
        if((Get-FileHash -LiteralPath $temporary).Hash -ne $entry.sha256){throw ('复制校验失败：'+$entry.path)}
        Move-Item -LiteralPath $temporary -Destination $target -Force
    }
    # Remove only files owned by the previous package. User data remains in its original directory.
    foreach($relative in $oldFiles) {
        if($entries.path -notcontains $relative) {
            $obsolete=Below $targetRoot $relative;Assert-NoLink $obsolete $targetRoot
            if(Test-Path -LiteralPath $obsolete -PathType Leaf){Remove-Item -LiteralPath $obsolete -Force}
        }
    }
    Copy-Item -LiteralPath (Join-Path $payloadRoot 'release-files.json') -Destination $oldManifest -Force
    Write-Output "已在原路径更新到 $($manifest.version)：$targetRoot；配对、设置与记录目录保留。"
    if(-not $NoLaunch){Start-Process -FilePath (Join-Path $targetRoot 'Lantern.exe') -WorkingDirectory $targetRoot -WindowStyle Hidden}
} catch {
    if(-not $NonInteractive){$null=[System.Windows.Forms.MessageBox]::Show($_.Exception.Message,'更新未完成','OK','Error')}
    throw
}

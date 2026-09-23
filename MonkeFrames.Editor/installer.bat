<# ::
@echo off
powershell -c "iex ((Get-Content '%~f0') -join [Environment]::Newline)"
goto :eof
#>

# (C) Copyright 2026 SirKingBinx
# MIT License

####
#### CHANGEME
####

# Display name for the mod
$DisplayName = "MonkeFrames"

# URL to the mod's download. This will be extracted to Gorilla Tag's root directory.
#
# Inside of the zip, this should be the path to your mod:
# BepInEx\plugins\ModName\[ModName].dll
#
$URL = "https://github.com/sirkingbinx/MonkeFrames/releases/latest/download/MonkeFrames.BepInEx.zip"

# Delete BepInEx/MelonLoader files from any previous installations. Doesn't delete any of the actual folders, just the injection DLL
# (winhttp.dll for bepinex, version.dll for melonloader)
$Clean = $false

# Auto-selects the language to use. Valid culture codes are accepted
$Culture = $PSUICulture

# BepInEx latest release URL
$BepInExURL = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"

####
#### TRANSLATIONS
####

$en_US = data {
    ConvertFrom-StringData @'
    downloading = Downloading {0}...
    installing = Installing {0}...
    
    clean = Cleaning up mod loader files...
    detectedPath = Detected install path: {0}
    noGT = No valid installation of Gorilla Tag was found. Please install the game via Steam or Oculus Rift and rerun this script.
    selectGame_Header = Select game to mod
    selectGame_Body = Multiple installations of Gorilla Tag were found on your computer. You must pick one to install {0} to.
    
    done = {0} is installed!
'@
}

$de = data {
    ConvertFrom-StringData @'
    downloading = {0} wird heruntergeladen...
    installing = Installation von {0}...

    clean = Mod-Loader-Dateien bereinigen...
    detectedPath = Ermittelter Installationspfad: {0}
    noGT = Es wurde keine gültige Installation von Gorilla Tag gefunden. Bitte installiere das Spiel über Steam oder Oculus Rift und führe dieses Skript erneut aus.
    selectGame_Header = Wähle ein Spiel für den Mod aus
    selectGame_Body = Auf Ihrem Computer wurden mehrere Installationen von Gorilla Tag gefunden. Sie müssen eine davon auswählen, auf der {0} installiert werden soll.

    done = {0} ist installiert!
'@
}

$zh = data {
    ConvertFrom-StringData @'
    downloading = 正在下载 {0}...
    installing = 正在安装 {0}...
    
    clean = 正在清理模组加载器文件……
    detectedPath = 检测到的安装路径：{0}
    noGT = 未找到有效的《Gorilla Tag》安装。请通过 Steam 或 Oculus Rift 安装游戏，然后重新运行此脚本。
    selectGame_Header = 选择要修改的游戏
    selectGame_Body = 在您的计算机上发现了多个《Gorilla Tag》的安装程序。您必须选择其中一个来安装 {0}。
    
    done = {0} 已安装！
'@
}

$es = data {
    ConvertFrom-StringData @'
    downloading = Descargando {0}...
    installing = Instalando {0}...
    
    clean = Limpieza de los archivos del cargador de mods...
    detectedPath = Ruta de instalación detectada: {0}
    noGT = No se ha encontrado ninguna instalación válida de Gorilla Tag. Instala el juego a través de Steam u Oculus Rift y vuelve a ejecutar este script.
    selectGame_Header = Selecciona el juego que quieres modificar
    selectGame_Body = Se han encontrado varias instalaciones de Gorilla Tag en tu ordenador. Debes elegir una para instalar {0} en ella.
    
    done = ¡{0} ya está instalado!
'@
}


$local = $null

if ($Culture -eq "de") {
    $local = $de
} elseif ($Culture -eq "zh") {
    $local = $zh
} elseif ($Culture -eq "es") {
    $local = $es
} else {
    $local = $en_US
}

####
#### HELPER FUNCTIONS
####

function Get-RegistryValue {
    param (
        [Parameter(Position = 0)]
        [string]$Name,

        [Parameter(Position = 1)]
        [string]$Key,

        [Parameter(Position = 2)]
        [string]$Default = ""
    )

    return [Microsoft.Win32.Registry]::GetValue($Name, $Key, $Default)
}

function DownloadFile {
    param (
        [Parameter(Position = 0)]
        [string]$URL,

        [Parameter(Position = 1)]
        [string]$Output
    )

    Invoke-WebRequest -Uri $URL -OutFile $Output
}

####
#### GET ALL INSTALLATIONS OF GORILLA TAG
####

$SteamInstallPath = Get-RegistryValue -Name "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1533390" -Key "InstallLocation"

$OculusLibraryId = Get-RegistryValue -Name "HKEY_CURRENT_USER\Software\Oculus VR, LLC\Oculus\Libraries" -Key "DefaultLibrary"
$OculusLibraryPath = Get-RegistryValue -Name "HKEY_CURRENT_USER\Software\Oculus VR, LLC\Oculus\Libraries\$($OculusLibraryId)" -Key "OriginalPath"
$OculusInstallPath = [System.IO.Path]::Combine($OculusLibraryPath, "Software", "another-axiom-gorilla-tag")

$GamePath = $null

if ([System.IO.Directory]::Exists($SteamInstallPath) -and [System.IO.Directory]::Exists($OculusInstallPath)) {
    # Prompts user for which game to install the mod to

    $steam = New-Object System.Management.Automation.Host.ChoiceDescription "&Steam", $SteamInstallPath
    $oculus = New-Object System.Management.Automation.Host.ChoiceDescription "&Oculus", $OculusInstallPath
    $choices = [System.Management.Automation.Host.ChoiceDescription[]]($steam, $oculus)

    $caption = $local.selectGame_Header
    $message = $local.selectGame_Body -f $DisplayName
    $default = 0
    $result = $host.ui.PromptForChoice($caption, $message, $choices, $default)

    if ($result -eq 0) {
        $GamePath = $SteamInstallPath
    } else {
        $GamePath = $OculusInstallPath
    }
} elseif ([System.IO.Directory]::Exists($SteamInstallPath)) {
    $GamePath = $SteamInstallPath
} elseif ([System.IO.Directory]::Exists($OculusInstallPath)) {
    $GamePath = $OculusInstallPath
} else {
    $local.noGT
    exit 1
}

$local.detectedPath -f $GamePath

if ($Clean) {
    # delete mod loader thingies

    $local.clean
    
    $ModLoaderFiles = @(".doorstop_version", "changelog.txt", "doorstop_config.ini", "winhttp.dll", "version.dll")

    foreach ($file in $ModLoaderFiles)
    {
        if (Test-Path $file) {
            Remove-Item $file
        }
    }
}

####
#### ACTUALLY INSTALL THE STUFF
####

$local.installing -f "BepInEx"
$BepInExZipPath =  [System.IO.Path]::Combine($GamePath, "BepInEx.zip")
DownloadFile $BepInExURL $BepInExZipPath
Expand-Archive -Path $BepInExZipPath -DestinationPath $GamePath -Force
Remove-Item $BepInExZipPath

$local.installing -f $DisplayName
$ModZipPath = [System.IO.Path]::Combine($GamePath, [System.IO.Path]::GetFileName($URL))
DownloadFile $URL $ModZipPath
Expand-Archive -Path $ModZipPath -DestinationPath $GamePath -Force
Remove-Item $ModZipPath

####
#### TELL USER WE'RE DONE
####

$local.done -f $DisplayName
pause
